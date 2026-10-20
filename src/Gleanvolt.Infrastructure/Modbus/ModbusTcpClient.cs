using System.Net.Sockets;
using NModbus;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Infrastructure.Modbus;

/// <summary>
/// Modbus TCP client for one SolaX device. Owns the socket and, crucially, its <em>health</em>:
/// every request is serialised, spaced, and — if it fails — followed by a full teardown of the
/// connection.
///
/// <para><b>Why the teardown matters (issue #24).</b> A Modbus TCP response that arrives after its
/// request has given up stays in the socket's receive buffer. The next request then reads <em>that</em>
/// reply instead of its own, and every request afterwards is permanently one or more responses behind
/// — NModbus rejects each one with "Response was not of expected transaction ID". The TCP connection
/// stays perfectly healthy throughout, so a liveness check cannot see the problem: it is the stream
/// that is poisoned, not the socket. Observed in the field as fifteen minutes of normal operation
/// followed by 45 minutes of every single poll failing, recoverable only by restarting the process.
/// So any failed exchange invalidates the connection, and the next call reconnects with a fresh
/// stream and a fresh transaction counter.</para>
/// </summary>
public sealed class ModbusTcpClient : IModbusClient
{
    // TcpClient.ConnectAsync has no built-in timeout: an unreachable or non-responding
    // device (wrong IP, firewalled, powered off) otherwise blocks the caller indefinitely.
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultIoTimeout = TimeSpan.FromSeconds(5);

    private readonly DeviceConfig _device;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _ioTimeout;
    private readonly int? _retries;
    private readonly TimeSpan _minRequestInterval;
    private readonly TimeProvider _timeProvider;

    // One request at a time per device. Reads are issued sequentially today, but a reconnect can now
    // happen in the middle of a call, so the invariant is enforced rather than assumed -- two requests
    // sharing a stream is another way to produce exactly the desync this class exists to avoid.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private TcpClient? _tcpClient;
    private IModbusMaster? _master;
    private long _lastRequestTicks;

    /// <param name="device">Where the device is, and how long to wait between requests to it.</param>
    /// <param name="timeProvider">The clock the request spacing is measured on.</param>
    /// <param name="connectTimeout">How long a connect may take; 5 s by default.</param>
    /// <param name="ioTimeout">How long one request may wait for its reply; 5 s by default.</param>
    /// <param name="retries">
    /// How many times NModbus repeats a request that timed out, or null for its own default. The
    /// appliance leaves it alone; a LAN scan (issue #267) sets 0, because a probe that may not get a
    /// reply would otherwise pay for one timeout several times over before it learnt anything.
    /// </param>
    public ModbusTcpClient(
        DeviceConfig device,
        TimeProvider? timeProvider = null,
        TimeSpan? connectTimeout = null,
        TimeSpan? ioTimeout = null,
        int? retries = null)
    {
        _device = device;
        _minRequestInterval = device.MinRequestInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _connectTimeout = connectTimeout ?? DefaultConnectTimeout;
        _ioTimeout = ioTimeout ?? DefaultIoTimeout;
        _retries = retries;
    }

    public bool IsConnected => _tcpClient?.Connected ?? false;

    /// <summary>
    /// Opens the connection. Callers no longer need to call this — every operation connects on demand —
    /// but it stays part of the interface so a caller can fail fast at startup if a device is
    /// unreachable.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<ushort[]> ReadHoldingRegistersAsync(
        ushort startAddress,
        ushort numberOfPoints,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(master => master.ReadHoldingRegistersAsync(_device.UnitId, startAddress, numberOfPoints), cancellationToken);

    public Task<ushort[]> ReadInputRegistersAsync(
        ushort startAddress,
        ushort numberOfPoints,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(master => master.ReadInputRegistersAsync(_device.UnitId, startAddress, numberOfPoints), cancellationToken);

    public Task WriteSingleRegisterAsync(
        ushort address,
        ushort value,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(master => master.WriteSingleRegisterAsync(_device.UnitId, address, value), cancellationToken);

    public Task WriteMultipleRegistersAsync(
        ushort startAddress,
        ushort[] values,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(master => master.WriteMultipleRegistersAsync(_device.UnitId, startAddress, values), cancellationToken);

    public ValueTask DisposeAsync()
    {
        Invalidate();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<T> ExecuteAsync<T>(Func<IModbusMaster, Task<T>> operation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var master = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            await ThrottleAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return await operation(master).ConfigureAwait(false);
            }
            catch (Exception) when (InvalidateAndRethrow())
            {
                throw; // unreachable: the filter always returns false, having done its work.
            }
            finally
            {
                _lastRequestTicks = _timeProvider.GetTimestamp();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task ExecuteAsync(Func<IModbusMaster, Task> operation, CancellationToken cancellationToken) =>
        ExecuteAsync<object?>(async master =>
        {
            await operation(master).ConfigureAwait(false);
            return null;
        }, cancellationToken);

    private async Task<IModbusMaster> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_master is null || !IsConnected)
        {
            await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
        }

        return _master!;
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(_connectTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var tcpClient = new TcpClient();
        try
        {
            await tcpClient.ConnectAsync(_device.Host, _device.Port, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            tcpClient.Dispose();
            throw new TimeoutException(
                $"Timed out connecting to Modbus device at {_device.Host}:{_device.Port} after {_connectTimeout}.");
        }
        catch
        {
            tcpClient.Dispose();
            throw;
        }

        // Guards the NModbus read/write calls below, which don't accept a CancellationToken.
        tcpClient.ReceiveTimeout = (int)_ioTimeout.TotalMilliseconds;
        tcpClient.SendTimeout = (int)_ioTimeout.TotalMilliseconds;

        Invalidate();
        _tcpClient = tcpClient;
        _master = new ModbusFactory().CreateMaster(tcpClient);

        if (_retries is { } retries)
        {
            _master.Transport.Retries = retries;
        }
    }

    /// <summary>
    /// Waits out the remainder of <see cref="DeviceConfig.MinRequestInterval"/> since the last request.
    /// The SolaX protocol asks for a gap between consecutive instructions; without one the device
    /// starts replying late, which is precisely how a stream ends up desynchronised.
    /// </summary>
    private async Task ThrottleAsync(CancellationToken cancellationToken)
    {
        if (_minRequestInterval <= TimeSpan.Zero || _lastRequestTicks == 0)
        {
            return;
        }

        var elapsed = _timeProvider.GetElapsedTime(_lastRequestTicks);
        var remaining = _minRequestInterval - elapsed;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    // Used as an exception filter so the connection is dropped without swallowing or re-wrapping the
    // original exception -- callers (and their logs) still see exactly what NModbus threw.
    private bool InvalidateAndRethrow()
    {
        Invalidate();
        return false;
    }

    private void Invalidate()
    {
        _master?.Dispose();
        _tcpClient?.Dispose();
        _master = null;
        _tcpClient = null;
    }
}
