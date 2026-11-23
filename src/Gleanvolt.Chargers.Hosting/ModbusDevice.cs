using Gleanvolt.Modbus;
using Microsoft.Extensions.Logging;

namespace Gleanvolt.Chargers;

/// <summary>
/// A Modbus charger at the address the owner set on the page: one client, made for the current address and unit id,
/// recorded while a recording runs, and made anew when either changes. The Gleanvolt Modbus client does the rest: one
/// request at a time, spaced, torn down after any failed exchange, a reconnect backoff.
/// </summary>
public sealed class ModbusDevice : IAsyncDisposable
{
    private readonly SavedSetting _address;
    private readonly SavedSetting _unitId;
    private readonly int _defaultPort;
    private readonly TimeSpan _minRequestInterval;
    private readonly TimeSpan _reconnectBackoff;
    private readonly DeviceRecorder _recorder;
    private readonly ILogger<ModbusDevice> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DeviceAddress Address, byte Unit, IModbusClient Client)? _current;

    /// <param name="address">The charger's address, as the page saves it.</param>
    /// <param name="unitId">Its unit id, as the page saves it; <paramref name="defaultUnitId"/> when not set.</param>
    /// <param name="defaultPort">The port when the address names none: 502.</param>
    /// <param name="defaultUnitId">The unit id when none is set.</param>
    /// <param name="minRequestInterval">The pause between two requests the device asks for.</param>
    /// <param name="recorder">The recording for testers.</param>
    /// <param name="logger">For a new address.</param>
    /// <param name="reconnectBackoff">How long to wait before reconnecting after a failure: Huawei's server refuses one too soon.</param>
    public ModbusDevice(
        SavedSetting address,
        SavedSetting unitId,
        int defaultPort,
        byte defaultUnitId,
        TimeSpan minRequestInterval,
        DeviceRecorder recorder,
        ILogger<ModbusDevice> logger,
        TimeSpan? reconnectBackoff = null)
    {
        _reconnectBackoff = reconnectBackoff ?? TimeSpan.Zero;
        _address = address;
        _unitId = unitId;
        _defaultPort = defaultPort;
        DefaultUnitId = defaultUnitId;
        _minRequestInterval = minRequestInterval;
        _recorder = recorder;
        _logger = logger;
    }

    /// <summary>The unit id when none is set.</summary>
    public byte DefaultUnitId { get; }

    /// <summary>The address in use, or null when none is set or it is not one.</summary>
    public DeviceAddress? Address => DeviceAddress.Parse(_address.Current, _defaultPort);

    /// <summary>The unit id in use.</summary>
    public byte UnitId => byte.TryParse(_unitId.Current, out var unit) && unit > 0 ? unit : DefaultUnitId;

    /// <summary>
    /// Runs <paramref name="exchange"/> on the client for the current address. Throws <see cref="NoAddressException"/>
    /// when there is none; any failure of the exchange passes through.
    /// </summary>
    public async Task<T> UseAsync<T>(Func<IModbusClient, Task<T>> exchange, CancellationToken cancellationToken)
    {
        var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
        return await exchange(client).ConfigureAwait(false);
    }

    /// <summary>A client for <paramref name="address"/>, for the page's check of an address before it is saved; the caller disposes it.</summary>
    public IModbusClient ClientFor(DeviceAddress address, byte unitId) =>
        new RecordingModbusClient(
            new ModbusTcpClient(
                new ModbusEndpoint(address.Host) { Port = address.Port, UnitId = unitId, MinRequestInterval = _minRequestInterval },
                connectTimeout: TimeSpan.FromSeconds(3),
                ioTimeout: TimeSpan.FromSeconds(3),
                retries: 0),
            _recorder);

    public async ValueTask DisposeAsync()
    {
        if (_current is { } current)
        {
            await current.Client.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    private async Task<IModbusClient> ClientAsync(CancellationToken cancellationToken)
    {
        var address = Address ?? throw new NoAddressException();
        var unit = UnitId;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_current is { } current && current.Address == address && current.Unit == unit)
            {
                return current.Client;
            }

            if (_current is { } old)
            {
                await old.Client.DisposeAsync().ConfigureAwait(false);
            }

            _logger.LogInformation("Talking to the charger at {Address}, unit {Unit}.", address, unit);
            var client = new RecordingModbusClient(
                new ModbusTcpClient(new ModbusEndpoint(address.Host)
                {
                    Port = address.Port,
                    UnitId = unit,
                    MinRequestInterval = _minRequestInterval,
                    ReconnectBackoff = _reconnectBackoff,
                }),
                _recorder);
            _current = (address, unit, client);
            return client;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>The charger has no address yet: the owner enters it on the page.</summary>
public sealed class NoAddressException() : InvalidOperationException("There is no address for the charger: enter it on the plugin's page.");
