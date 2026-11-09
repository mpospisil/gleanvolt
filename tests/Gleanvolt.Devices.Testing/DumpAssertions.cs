namespace Gleanvolt.Devices.Testing;

/// <summary>What the inverter and the charger conformance tests check of a recording alike.</summary>
internal static class DumpAssertions
{
    /// <summary>
    /// Fails when <paramref name="dump"/> keeps more of the serial number than the prefix
    /// <paramref name="spec"/> allows. Fixtures are committed: a serial number in one is an installation's
    /// identity in a public repository.
    /// </summary>
    public static void SerialRedacted(RegisterSnapshotSpec spec, RegisterDump dump)
    {
        if (spec.SerialNumber is not { } serial)
        {
            return;
        }

        var unit = serial.UnitId is { } id ? dump.Units.GetValueOrDefault(id) ?? new RegisterDump() : dump;
        var table = unit[serial.Table];
        var characters = serial.Addresses
            .Where(table.ContainsKey)
            .SelectMany(address => new[] { (char)(table[address] >> 8), (char)(table[address] & 0xFF) })
            .ToList();

        for (var index = spec.SerialPrefixLength; index < characters.Count; index++)
        {
            Assert.True(characters[index] is '\0' or RegisterSnapshot.RedactedCharacter,
                $"{Describe(dump)}: serial number character {index} is '{characters[index]}', past the {spec.SerialPrefixLength}-character prefix.");
        }
    }

    /// <summary>Fails when the driver read a register <paramref name="spec"/> does not include.</summary>
    public static void Covers(RegisterSnapshotSpec spec, IEnumerable<RegisterRange> reads)
    {
        var covered = spec.Ranges
            .SelectMany(range => range.Addresses.Select(address => (range.UnitId, range.Table, address)))
            .ToHashSet();

        foreach (var read in reads)
        {
            foreach (var address in read.Addresses)
            {
                Assert.True(covered.Contains((read.UnitId, read.Table, address)),
                    $"The driver reads {read.Table} 0x{address:X4}{(read.UnitId is { } unit ? $" on unit {unit}" : string.Empty)}, which its snapshot does not include; a tester's snapshot could not replay it.");
            }
        }
    }

    /// <summary>A dump as a failure message names it.</summary>
    public static string Describe(RegisterDump dump) =>
        string.IsNullOrEmpty(dump.Note) ? $"The {dump.Family} dump" : $"The {dump.Family} dump ({dump.Note})";
}
