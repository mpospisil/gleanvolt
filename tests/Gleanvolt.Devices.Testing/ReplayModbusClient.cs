namespace Gleanvolt.Devices.Testing;

/// <summary>
/// An <see cref="IModbusClient"/> that answers from a <see cref="RegisterDump"/> (issue #319): a device on
/// the bench, recorded once and replayed in every test after.
///
/// <para><b>Like the device.</b> A read that touches an address the dump does not have is refused with
/// "illegal data address" (<see cref="ModbusDeviceException.IllegalDataAddress"/>), as the device refuses it.
/// A write is recorded and changes nothing that is read back: on the hardware a command block is not
/// readable, and a test that wrote and read back its own value would prove nothing about the device.</para>
///
/// <para><b>Other units</b> (<see cref="ForUnit"/>, issue #322) answer from the dump's
/// <see cref="RegisterDump.Units"/>, on the same recorded connection: their reads and writes land in this
/// client's <see cref="Reads"/> and <see cref="Writes"/>, tagged with the unit id. A unit the dump does not
/// have refuses every address, as a gateway with nothing behind that id does.</para>
/// </summary>
public sealed class ReplayModbusClient : IModbusClient
{
    private readonly RegisterDump _dump;
    private readonly byte? _unit;
    private readonly ReplayModbusClient _root;
    private readonly List<RegisterWrite> _writes = [];
    private readonly List<RegisterRange> _reads = [];
    private bool _connected;

    public ReplayModbusClient(RegisterDump dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        _dump = dump;
        _root = this;
    }

    private ReplayModbusClient(ReplayModbusClient root, byte unit)
    {
        _root = root;
        _unit = unit;
        _dump = root._dump.Units.GetValueOrDefault(unit) ?? new RegisterDump();
    }

    /// <summary>Every write, in order: the exact sequence a driver sent, on every unit.</summary>
    public IReadOnlyList<RegisterWrite> Writes => _root._writes;

    /// <summary>Every read, in order, on every unit; a read of another unit carries its id.</summary>
    public List<RegisterRange> Reads => _root._reads;

    public bool IsConnected => _root._connected;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _root._connected = true;
        return Task.CompletedTask;
    }

    public IModbusClient ForUnit(byte unitId) => new ReplayModbusClient(_root, unitId);

    public Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort numberOfPoints, CancellationToken cancellationToken = default) =>
        Read(RegisterTable.Holding, startAddress, numberOfPoints);

    public Task<ushort[]> ReadInputRegistersAsync(ushort startAddress, ushort numberOfPoints, CancellationToken cancellationToken = default) =>
        Read(RegisterTable.Input, startAddress, numberOfPoints);

    public Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken cancellationToken = default)
    {
        _root._writes.Add(new RegisterWrite(address, [value], _unit));
        return Task.CompletedTask;
    }

    public Task WriteMultipleRegistersAsync(ushort startAddress, ushort[] values, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        _root._writes.Add(new RegisterWrite(startAddress, [.. values], _unit));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Task<ushort[]> Read(RegisterTable table, ushort start, ushort count)
    {
        Reads.Add(new RegisterRange(table, start, count, _unit));
        var registers = _dump[table];
        var values = new ushort[count];

        for (var offset = 0; offset < count; offset++)
        {
            var address = (ushort)(start + offset);
            if (!registers.TryGetValue(address, out values[offset]))
            {
                return Task.FromException<ushort[]>(new ModbusDeviceException(
                    ModbusDeviceException.IllegalDataAddress,
                    $"The recorded device{(_unit is { } unit ? $" (unit {unit})" : string.Empty)} keeps no {table.ToString().ToLowerInvariant()} register at 0x{address:X4}."));
            }
        }

        return Task.FromResult(values);
    }
}

/// <summary>One write a driver made: the start address and every value, in order, and the other unit it went to, if any.</summary>
public sealed record RegisterWrite(ushort Start, IReadOnlyList<ushort> Values, byte? UnitId = null);
