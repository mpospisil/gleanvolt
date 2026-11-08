namespace Gleanvolt.Devices;

/// <summary>The two register tables a Modbus device keeps.</summary>
public enum RegisterTable
{
    /// <summary>Holding registers: function code 0x03 to read, 0x06/0x10 to write.</summary>
    Holding,

    /// <summary>Input registers: function code 0x04, read-only.</summary>
    Input,
}

/// <summary>A run of consecutive registers in one table.</summary>
/// <param name="Table">Which table.</param>
/// <param name="Start">The first address.</param>
/// <param name="Count">How many registers, at least one.</param>
/// <param name="UnitId">
/// Another unit id on the same connection (<see cref="IModbusClient.ForUnit"/>), for a gateway that answers
/// for itself; null is the client's own unit.
/// </param>
public sealed record RegisterRange(RegisterTable Table, ushort Start, ushort Count, byte? UnitId = null)
{
    /// <summary>Every address in the range.</summary>
    public IEnumerable<ushort> Addresses => Enumerable.Range(Start, Count).Select(address => (ushort)address);
}

/// <summary>
/// What a register snapshot of this family reads (issue #319): every register the driver uses, so that the
/// snapshot replays the driver, and where the serial number is, so that it can be removed before the file
/// leaves the house.
/// </summary>
/// <param name="Ranges">The registers to read. Together they must cover everything the driver reads.</param>
/// <param name="SerialNumber">
/// Where the serial number is, as ASCII two characters to a register, high byte first; null when the family
/// keeps none among <paramref name="Ranges"/>.
/// </param>
/// <param name="SerialPrefixLength">
/// How many leading characters of the serial number to keep: the prefix that names the family and model,
/// which is what a driver may need. The rest identifies one device and is replaced.
/// </param>
public sealed record RegisterSnapshotSpec(
    IReadOnlyList<RegisterRange> Ranges,
    RegisterRange? SerialNumber = null,
    int SerialPrefixLength = 0);
