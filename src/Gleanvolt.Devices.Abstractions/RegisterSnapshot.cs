namespace Gleanvolt.Devices;

/// <summary>
/// Takes a <see cref="RegisterDump"/> of a device (issue #319): every range a driver's
/// <see cref="RegisterSnapshotSpec"/> names, read once, with the serial number reduced to its family prefix.
/// Reads only. The pacing between requests is the client's: the host's client already spaces requests
/// as the family needs.
/// </summary>
public static class RegisterSnapshot
{
    /// <summary>The most registers one Modbus read may ask for.</summary>
    public const int MaxRegistersPerRead = 125;

    /// <summary>The character a removed serial-number character is replaced with.</summary>
    public const char RedactedCharacter = '*';

    /// <summary>
    /// Reads every range in <paramref name="spec"/>. A block the device refuses with a Modbus exception is
    /// halved until the refused addresses are found, and those are left out of the dump, exactly as a
    /// replay will then refuse them. Anything else -- no answer, a dropped link -- fails the snapshot.
    /// </summary>
    /// <param name="client">The device's client. Connected first if it is not.</param>
    /// <param name="spec">What to read; the driver's.</param>
    /// <param name="template">
    /// The dump's description (driver, family, version, time, note); its tables are replaced by what is read.
    /// </param>
    public static async Task<RegisterDump> TakeAsync(
        IModbusClient client,
        RegisterSnapshotSpec spec,
        RegisterDump template,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(template);

        if (!client.IsConnected)
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        // One pair of tables per unit: null is the client's own, anything else the same connection
        // addressed to another unit (#322).
        var tables = new Dictionary<byte, (SortedDictionary<ushort, ushort> Holding, SortedDictionary<ushort, ushort> Input)>();
        var holding = new SortedDictionary<ushort, ushort>();
        var input = new SortedDictionary<ushort, ushort>();

        foreach (var range in Merge(spec.Ranges))
        {
            var unitClient = client;
            SortedDictionary<ushort, ushort> table;
            if (range.UnitId is { } unit)
            {
                if (!tables.TryGetValue(unit, out var unitTables))
                {
                    unitTables = ([], []);
                    tables[unit] = unitTables;
                }

                unitClient = client.ForUnit(unit);
                table = range.Table == RegisterTable.Holding ? unitTables.Holding : unitTables.Input;
            }
            else
            {
                table = range.Table == RegisterTable.Holding ? holding : input;
            }

            for (var start = range.Start; start < range.Start + range.Count; start += MaxRegistersPerRead)
            {
                var count = (ushort)Math.Min(MaxRegistersPerRead, range.Start + range.Count - start);
                await ReadAsync(unitClient, range.Table, (ushort)start, count, table, cancellationToken).ConfigureAwait(false);
            }
        }

        if (spec.SerialNumber is { } serial)
        {
            var (serialHolding, serialInput) = serial.UnitId is { } serialUnit
                ? tables.GetValueOrDefault(serialUnit)
                : (holding, input);
            if (serialHolding is not null && serialInput is not null)
            {
                Redact(serial.Table == RegisterTable.Holding ? serialHolding : serialInput, serial, spec.SerialPrefixLength);
            }
        }

        return template with
        {
            Holding = new Dictionary<ushort, ushort>(holding),
            Input = new Dictionary<ushort, ushort>(input),
            Units = tables.ToDictionary(
                entry => entry.Key,
                entry => new RegisterDump
                {
                    Holding = new Dictionary<ushort, ushort>(entry.Value.Holding),
                    Input = new Dictionary<ushort, ushort>(entry.Value.Input),
                }),
        };
    }

    /// <summary>
    /// Replaces every serial-number character after the first <paramref name="keep"/> with
    /// <see cref="RedactedCharacter"/>, leaving padding (NUL) as it is.
    /// </summary>
    public static void Redact(IDictionary<ushort, ushort> table, RegisterRange serial, int keep)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(serial);

        var index = 0;
        foreach (var address in serial.Addresses)
        {
            if (table.TryGetValue(address, out var value))
            {
                var high = (byte)(value >> 8);
                var low = (byte)value;
                high = index >= keep && high != 0 ? (byte)RedactedCharacter : high;
                low = index + 1 >= keep && low != 0 ? (byte)RedactedCharacter : low;
                table[address] = (ushort)((high << 8) | low);
            }

            index += 2;
        }
    }

    private static async Task ReadAsync(
        IModbusClient client,
        RegisterTable table,
        ushort start,
        ushort count,
        IDictionary<ushort, ushort> into,
        CancellationToken cancellationToken)
    {
        ushort[] values;
        try
        {
            values = table == RegisterTable.Holding
                ? await client.ReadHoldingRegistersAsync(start, count, cancellationToken).ConfigureAwait(false)
                : await client.ReadInputRegistersAsync(start, count, cancellationToken).ConfigureAwait(false);
        }
        catch (ModbusDeviceException) when (count > 1)
        {
            // Somewhere in the block is an address the device keeps nothing at. Halving finds it in
            // log2(count) requests per refused address, rather than one request per register.
            var half = (ushort)(count / 2);
            await ReadAsync(client, table, start, half, into, cancellationToken).ConfigureAwait(false);
            await ReadAsync(client, table, (ushort)(start + half), (ushort)(count - half), into, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (ModbusDeviceException)
        {
            return;
        }

        for (var offset = 0; offset < values.Length && offset < count; offset++)
        {
            into[(ushort)(start + offset)] = values[offset];
        }
    }

    // Overlapping or touching ranges in one table become one, so no register is read twice and the reads
    // are as few as the 125-register limit allows.
    private static IEnumerable<RegisterRange> Merge(IEnumerable<RegisterRange> ranges)
    {
        foreach (var group in ranges.GroupBy(range => (range.UnitId, range.Table)))
        {
            RegisterRange? current = null;
            foreach (var range in group.Where(range => range.Count > 0).OrderBy(range => range.Start))
            {
                if (current is not null && range.Start <= current.Start + current.Count)
                {
                    var end = Math.Max(current.Start + current.Count, range.Start + range.Count);
                    current = current with { Count = (ushort)(end - current.Start) };
                }
                else
                {
                    if (current is not null)
                    {
                        yield return current;
                    }

                    current = range;
                }
            }

            if (current is not null)
            {
                yield return current;
            }
        }
    }
}
