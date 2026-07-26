using Solax.Core.Interfaces;

namespace Solax.Infrastructure.Tests;

/// <summary>
/// In-memory <see cref="IModbusClient"/> for tests: holding registers are backed by a dictionary,
/// and every write is recorded so tests can assert exactly which registers were written.
/// </summary>
internal class FakeModbusClient : IModbusClient
{
    private readonly Dictionary<ushort, ushort> _holding = new();

    public bool IsConnected { get; private set; }

    public List<(ushort Address, ushort Value)> Writes { get; } = [];

    public void SetHolding(ushort address, ushort value) => _holding[address] = value;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort numberOfPoints, CancellationToken cancellationToken = default)
    {
        var result = new ushort[numberOfPoints];
        for (var i = 0; i < numberOfPoints; i++)
        {
            result[i] = _holding.TryGetValue((ushort)(startAddress + i), out var value) ? value : (ushort)0;
        }

        return Task.FromResult(result);
    }

    public Task<ushort[]> ReadInputRegistersAsync(ushort startAddress, ushort numberOfPoints, CancellationToken cancellationToken = default) =>
        ReadHoldingRegistersAsync(startAddress, numberOfPoints, cancellationToken);

    public Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken cancellationToken = default)
    {
        Writes.Add((address, value));
        _holding[address] = value;
        return Task.CompletedTask;
    }

    public virtual Task WriteMultipleRegistersAsync(ushort startAddress, ushort[] values, CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < values.Length; i++)
        {
            Writes.Add(((ushort)(startAddress + i), values[i]));
            _holding[(ushort)(startAddress + i)] = values[i];
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
