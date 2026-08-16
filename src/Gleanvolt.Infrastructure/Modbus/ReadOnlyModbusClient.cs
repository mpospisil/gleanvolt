using Microsoft.Extensions.Logging;
using Gleanvolt.Core.Interfaces;

namespace Gleanvolt.Infrastructure.Modbus;

/// <summary>
/// Wraps an <see cref="IModbusClient"/> and makes it physically incapable of writing: reads and
/// connection handling pass through, every write is dropped.
///
/// Used to enforce the dry-run guarantee that we never write to a SolaX device. Callers are already
/// expected to skip writes in dry-run, so a suppressed write here means a caller missed its guard --
/// hence the warning, which acts as a tripwire rather than a routine message.
/// </summary>
public sealed class ReadOnlyModbusClient : IModbusClient
{
    private readonly IModbusClient _inner;
    private readonly ILogger<ReadOnlyModbusClient> _logger;

    public ReadOnlyModbusClient(IModbusClient inner, ILogger<ReadOnlyModbusClient> logger)
    {
        _inner = inner;
        _logger = logger;
    }

    public bool IsConnected => _inner.IsConnected;

    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        _inner.ConnectAsync(cancellationToken);

    public Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort numberOfPoints, CancellationToken cancellationToken = default) =>
        _inner.ReadHoldingRegistersAsync(startAddress, numberOfPoints, cancellationToken);

    public Task<ushort[]> ReadInputRegistersAsync(ushort startAddress, ushort numberOfPoints, CancellationToken cancellationToken = default) =>
        _inner.ReadInputRegistersAsync(startAddress, numberOfPoints, cancellationToken);

    public Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning(
            "Dry-run: suppressed a write of {Value} to register {Address}. Nothing was sent to the device, but a caller should have skipped this write.",
            value, address);
        return Task.CompletedTask;
    }

    public Task WriteMultipleRegistersAsync(ushort startAddress, ushort[] values, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning(
            "Dry-run: suppressed a write of {Count} register(s) starting at {Address}. Nothing was sent to the device, but a caller should have skipped this write.",
            values.Length, startAddress);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
