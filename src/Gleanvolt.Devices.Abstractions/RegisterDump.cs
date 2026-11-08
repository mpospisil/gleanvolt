using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gleanvolt.Devices;

/// <summary>
/// A recording of a device's registers (issue #319): what the Settings snapshot button saves, what a tester
/// sends, and what <c>ReplayModbusClient</c> answers from in a driver's tests.
///
/// <para><b>The file.</b> JSON, one object per table keyed by address in hex (<c>"0x001C"</c>), so it can be
/// read beside a register map. An address that is not in the file is one the device refused, or one nobody
/// asked for; a replay answers it the way the device would, with "illegal data address".</para>
///
/// <code>
/// {
///   "format": "gleanvolt-register-dump", "formatVersion": 1,
///   "driver": "solax", "family": "SolaX X3-HYB-G4 PRO",
///   "gleanvoltVersion": "1.2.40", "capturedAt": "2026-11-08T12:00:00+00:00",
///   "note": "",
///   "holding": { "0x0000": 18484, ... },
///   "input": { "0x001C": 57, ... },
///   "units": { "0": { "holding": { "0x7BA9": 0, ... }, "input": {} } }
/// }
/// </code>
///
/// <para><c>units</c> is there only for a device reached through a gateway that answers for itself on
/// another unit id, on the same connection (Huawei's EMMA, issue #322); the top-level tables are the
/// configured unit's.</para>
/// </summary>
public sealed record RegisterDump
{
    /// <summary>The <c>format</c> value every dump carries, so a stray JSON file is not mistaken for one.</summary>
    public const string FormatName = "gleanvolt-register-dump";

    /// <summary>The version this code writes, and the highest it reads.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>The driver id the dump was taken with (<c>Pv:Inverter:Driver</c>).</summary>
    public string Driver { get; init; } = string.Empty;

    /// <summary>What the device is, as its owner configured it (<c>Pv:Inverter:Model</c>); free text.</summary>
    public string Family { get; init; } = string.Empty;

    /// <summary>The Gleanvolt version that took the dump.</summary>
    public string GleanvoltVersion { get; init; } = string.Empty;

    /// <summary>When the dump was taken.</summary>
    public DateTimeOffset? CapturedAt { get; init; }

    /// <summary>What was happening when it was taken ("night, battery idle, importing"), for a test fixture.</summary>
    public string Note { get; init; } = string.Empty;

    /// <summary>Holding registers, by address.</summary>
    public IReadOnlyDictionary<ushort, ushort> Holding { get; init; } = new Dictionary<ushort, ushort>();

    /// <summary>Input registers, by address.</summary>
    public IReadOnlyDictionary<ushort, ushort> Input { get; init; } = new Dictionary<ushort, ushort>();

    /// <summary>
    /// Other unit ids on the same connection, each with only its tables; empty for a dump of one unit.
    /// </summary>
    public IReadOnlyDictionary<byte, RegisterDump> Units { get; init; } = new Dictionary<byte, RegisterDump>();

    /// <summary>One table.</summary>
    public IReadOnlyDictionary<ushort, ushort> this[RegisterTable table] => table == RegisterTable.Holding ? Holding : Input;

    /// <summary>The dump as the file holds it.</summary>
    public string ToJson()
    {
        var root = new JsonObject
        {
            ["format"] = FormatName,
            ["formatVersion"] = CurrentFormatVersion,
            ["driver"] = Driver,
            ["family"] = Family,
            ["gleanvoltVersion"] = GleanvoltVersion,
            ["capturedAt"] = CapturedAt?.ToString("O", CultureInfo.InvariantCulture),
            ["note"] = Note,
            ["holding"] = Table(Holding),
            ["input"] = Table(Input),
        };

        if (Units.Count > 0)
        {
            var units = new JsonObject();
            foreach (var (unit, dump) in Units.OrderBy(entry => entry.Key))
            {
                units[unit.ToString(CultureInfo.InvariantCulture)] = new JsonObject
                {
                    ["holding"] = Table(dump.Holding),
                    ["input"] = Table(dump.Input),
                };
            }

            root["units"] = units;
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Reads a dump file.</summary>
    /// <exception cref="FormatException">The text is not a register dump this version can read.</exception>
    public static RegisterDump FromJson(string json)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("A register dump is a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new FormatException("Not JSON: " + ex.Message, ex);
        }

        if ((string?)root["format"] != FormatName)
        {
            throw new FormatException($"Not a register dump: 'format' is not '{FormatName}'.");
        }

        var version = (int?)root["formatVersion"] ?? 0;
        if (version is < 1 or > CurrentFormatVersion)
        {
            throw new FormatException($"Register dump format version {version} is not one this Gleanvolt reads (1 to {CurrentFormatVersion}).");
        }

        var capturedAt = (string?)root["capturedAt"];

        return new RegisterDump
        {
            Driver = (string?)root["driver"] ?? string.Empty,
            Family = (string?)root["family"] ?? string.Empty,
            GleanvoltVersion = (string?)root["gleanvoltVersion"] ?? string.Empty,
            CapturedAt = string.IsNullOrEmpty(capturedAt) ? null : DateTimeOffset.Parse(capturedAt, CultureInfo.InvariantCulture),
            Note = (string?)root["note"] ?? string.Empty,
            Holding = ReadTable(root["holding"], "holding"),
            Input = ReadTable(root["input"], "input"),
            Units = ReadUnits(root["units"]),
        };
    }

    private static Dictionary<byte, RegisterDump> ReadUnits(JsonNode? node)
    {
        var units = new Dictionary<byte, RegisterDump>();
        if (node is null)
        {
            return units;
        }

        if (node is not JsonObject entries)
        {
            throw new FormatException("'units' is an object of unit id to tables.");
        }

        foreach (var (key, value) in entries)
        {
            if (!byte.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var unit))
            {
                throw new FormatException($"'units' key '{key}' is not a unit id (0 to 255).");
            }

            units[unit] = new RegisterDump
            {
                Holding = ReadTable(value?["holding"], $"units.{key}.holding"),
                Input = ReadTable(value?["input"], $"units.{key}.input"),
            };
        }

        return units;
    }

    private static JsonObject Table(IReadOnlyDictionary<ushort, ushort> registers)
    {
        var table = new JsonObject();
        foreach (var (address, value) in registers.OrderBy(entry => entry.Key))
        {
            table[Address(address)] = value;
        }

        return table;
    }

    private static Dictionary<ushort, ushort> ReadTable(JsonNode? node, string name)
    {
        var table = new Dictionary<ushort, ushort>();
        if (node is null)
        {
            return table;
        }

        if (node is not JsonObject entries)
        {
            throw new FormatException($"'{name}' is an object of address to value.");
        }

        foreach (var (key, value) in entries)
        {
            table[ParseAddress(key, name)] = value?.GetValueKind() == JsonValueKind.Number && value.AsValue().TryGetValue<ushort>(out var register)
                ? register
                : throw new FormatException($"'{name}' {key} is not a register value (0 to 65535).");
        }

        return table;
    }

    private static string Address(ushort address) => "0x" + address.ToString("X4", CultureInfo.InvariantCulture);

    private static ushort ParseAddress(string key, string name)
    {
        var hex = key.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        return ushort.TryParse(
            hex ? key[2..] : key,
            hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var address)
            ? address
            : throw new FormatException($"'{name}' key '{key}' is not a register address.");
    }
}
