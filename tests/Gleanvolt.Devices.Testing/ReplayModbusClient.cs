namespace Gleanvolt.Devices.Testing;

/// <summary>
/// An <see cref="IModbusClient"/> that answers from a <see cref="RegisterDump"/> (issue #319): a device on
/// the bench, recorded once and replayed in every test after.
///
/// <para><b>Like the device.</b> A read that touches an address the dump does not have is refused with
/// "illegal data address" (<see cref="ModbusDeviceException.IllegalDataAddress"/>), as the device refuses it.
/// A write is recorded and changes nothing that is read back: on the hardware a command block is not
/// readable, and a test that wrote and read back its own value would prove nothing about the device.</para>
/// </summary>
public sealed class ReplayModbusClient : IModbusClient
{
    private readonly RegisterDump _dump;
    private readonly List<RegisterWrite> _writes = [];

    public ReplayModbusClient(RegisterDump dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        _dump = dump;
    }

    /// <summary>Every write, in order: the exact sequence a driver sent.</summary>
    public IReadOnlyList<RegisterWrite> Writes => _writes;

    /// <summary>Every read, in order.</summary>
    public List<RegisterRange> Reads { get; } = [];

    public bool IsConnected { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort numberOfPoints, CancellationToken cancellationToken = default) =>
        Read(RegisterTable.Holding, startAddress, numberOfPoints);

    public Task<ushort[]> ReadInputRegistersAsync(ushort startAddress, ushort numberOfPoints, CancellationToken cancellationToken = default) =>
        Read(RegisterTable.Input, startAddress, numberOfPoints);

    public Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken cancellationToken = default)
    {
        _writes.Add(new RegisterWrite(address, [value]));
        return Task.CompletedTask;
    }

    public Task WriteMultipleRegistersAsync(ushort startAddress, ushort[] values, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        _writes.Add(new RegisterWrite(startAddress, [.. values]));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Task<ushort[]> Read(RegisterTable table, ushort start, ushort count)
    {
        Reads.Add(new RegisterRange(table, start, count));
        var registers = _dump[table];
        var values = new ushort[count];

        for (var offset = 0; offset < count; offset++)
        {
            var address = (ushort)(start + offset);
            if (!registers.TryGetValue(address, out values[offset]))
            {
                return Task.FromException<ushort[]>(new ModbusDeviceException(
                    ModbusDeviceException.IllegalDataAddress,
                    $"The recorded device keeps no {table.ToString().ToLowerInvariant()} register at 0x{address:X4}."));
            }
        }

        return Task.FromResult(values);
    }
}

/// <summary>One write a driver made: the start address and every value, in order.</summary>
public sealed record RegisterWrite(ushort Start, IReadOnlyList<ushort> Values);
