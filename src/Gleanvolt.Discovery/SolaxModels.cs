using Gleanvolt.Core.Models;

namespace Gleanvolt.Discovery;

/// <summary>
/// What a SolaX serial number says the box is, from its prefix — the one table in code that
/// <c>docs/SUPPORTED_PV_SYSTEMS.md</c> lists by hand. The two must agree; a test checks that every
/// prefix here is in the doc.
///
/// <para>The prefixes are the wills106/homeassistant-solax-modbus integration's
/// (<c>async_determineInverterType</c> in <c>plugin_solax.py</c> and
/// <c>plugin_solax_ev_charger.py</c>), which detects every model this way. The longest matching prefix
/// wins, so <c>H3DE</c> and <c>H3E</c> stay apart from <c>H3B</c>.</para>
/// </summary>
public static class SolaxModels
{
    private static readonly IReadOnlyList<(string Prefix, SolaxModel Model)> InverterTable = Build(
    [
        (["10K"], "X3-HYB-G4 PRO", "GEN6 hybrid", SupportTier.Verified),

        (["H34"], "X3-Hybrid G4", "GEN4 hybrid", SupportTier.Expected),
        (["H31"], "TIGO TSI X3", "GEN4 hybrid", SupportTier.Expected),
        (["H43", "H44", "H450", "H460", "H475"], "X1-Hybrid G4", "GEN4 hybrid", SupportTier.Expected),
        (["H53", "H55", "H56", "H58"], "X1-IES", "GEN5 hybrid", SupportTier.Expected),
        (["H35", "P35"], "X3-IES", "GEN5 hybrid", SupportTier.Expected),
        (["H3B"], "X3-Ultra", "GEN5 hybrid", SupportTier.Expected),
        (["8021"], "X3-Aelio", "GEN5 hybrid", SupportTier.Expected),
        (["10M"], "X1-VAST", "GEN6 hybrid", SupportTier.Expected),

        (["L30", "U30", "L37", "U37", "L50", "U50"], "X1-Hybrid G2", "GEN2 hybrid", SupportTier.ReadingsAndChargingOnly),
        (["H1E", "H1I", "HCC", "HUE", "XRE"], "X1-Hybrid G3", "GEN3 hybrid", SupportTier.ReadingsAndChargingOnly),
        (["H3DE", "H3E", "H3LE", "H3PE", "H3UE"], "X3-Hybrid G3", "GEN3 hybrid", SupportTier.ReadingsAndChargingOnly),

        (["XAC"], "X1-AC G3", "AC-coupled", SupportTier.NotSupported),
        (["F3D", "F3E"], "X3-RetroFit G3", "RetroFit", SupportTier.NotSupported),
        (["F43", "F450", "F460", "F475", "PRE"], "X1-RetroFit G4", "RetroFit", SupportTier.NotSupported),
        (["F34"], "X3-Fit G4", "FIT", SupportTier.NotSupported),
        (["PRI"], "X1-FIT G3", "FIT", SupportTier.NotSupported),
    ], PvDeviceRole.Inverter);

    private static readonly IReadOnlyList<(string Prefix, SolaxModel Model)> EvcTable = Build(
    [
        (["C107"], "X1-EVC 7 kW", "GEN1 (EVC)", SupportTier.Expected),
        (["C311"], "X3-EVC 11 kW", "GEN1 (EVC)", SupportTier.Expected),
        (["C322"], "X3-EVC 22 kW", "GEN1 (EVC)", SupportTier.Expected),
    ], PvDeviceRole.Charger);

    // The HAC's serial is "5" + a two-character model code + a two-character power code. X1/X3-HAC is
    // the reference installation's model; the rest of the line shares its registers.
    private static readonly IReadOnlyDictionary<string, (string Name, SupportTier Tier)> HacModels =
        new Dictionary<string, (string, SupportTier)>(StringComparer.Ordinal)
        {
            ["02"] = ("X1-HAC", SupportTier.Verified),
            ["03"] = ("X3-HAC", SupportTier.Verified),
            ["04"] = ("A1-HAC", SupportTier.Expected),
            ["05"] = ("J1-HAC", SupportTier.Expected),
            ["06"] = ("X1-HAC-S", SupportTier.Expected),
            ["07"] = ("X3-HAC-S", SupportTier.Expected),
            ["08"] = ("C1-HAC", SupportTier.Expected),
            ["09"] = ("C3-HAC", SupportTier.Expected),
        };

    private static readonly IReadOnlyDictionary<string, string> HacPowers =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["04"] = "4.6 kW",
            ["07"] = "7.2 kW",
            ["0B"] = "11 kW",
            ["0M"] = "22 kW",
        };

    /// <summary>Every inverter prefix, for the test that holds the doc to this table.</summary>
    internal static IEnumerable<string> InverterPrefixes => InverterTable.Select(entry => entry.Prefix);

    /// <summary>Every charger prefix with a fixed spelling (the HAC's is a pattern), for the same test.</summary>
    internal static IEnumerable<string> ChargerPrefixes => EvcTable.Select(entry => entry.Prefix);

    /// <summary>The inverter a serial number read from the inverter's serial registers names, or null.</summary>
    public static SolaxModel? Inverter(string? serialNumber) => Match(InverterTable, serialNumber);

    /// <summary>The charger a serial number read from the charger's serial registers names, or null.</summary>
    public static SolaxModel? Charger(string? serialNumber)
    {
        if (Match(EvcTable, serialNumber) is { } evc)
        {
            return evc;
        }

        if (serialNumber is { Length: >= 5 } && serialNumber[0] == '5'
            && HacModels.TryGetValue(serialNumber[1..3], out var model)
            && HacPowers.TryGetValue(serialNumber[3..5], out var power))
        {
            return new SolaxModel(PvDeviceRole.Charger, $"{model.Name} {power}", "GEN2 (HAC)", model.Tier);
        }

        return null;
    }

    /// <summary>
    /// The serial number in <paramref name="registers"/> — ASCII, two characters a register, high byte
    /// first, as upstream decodes it — or null when they do not hold one. A device that is not a SolaX
    /// answers these addresses with whatever it keeps there, so anything that is not plain upper-case
    /// letters and digits is not taken for a serial number.
    /// </summary>
    public static string? DecodeSerialNumber(IReadOnlyList<ushort> registers)
    {
        var characters = new List<char>(registers.Count * 2);

        foreach (var register in registers)
        {
            characters.Add((char)(register >> 8));
            characters.Add((char)(register & 0xFF));
        }

        var text = new string([.. characters]).TrimEnd('\0', ' ');

        return text.Length >= 5 && text.All(character => character is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
            ? text
            : null;
    }

    private static SolaxModel? Match(IReadOnlyList<(string Prefix, SolaxModel Model)> table, string? serialNumber) =>
        serialNumber is null
            ? null
            : table.Where(entry => serialNumber.StartsWith(entry.Prefix, StringComparison.Ordinal))
                .OrderByDescending(entry => entry.Prefix.Length)
                .Select(entry => entry.Model)
                .FirstOrDefault();

    private static List<(string Prefix, SolaxModel Model)> Build(
        IEnumerable<(string[] Prefixes, string Model, string Family, SupportTier Tier)> rows,
        PvDeviceRole role) =>
    [
        .. rows.SelectMany(row => row.Prefixes.Select(prefix =>
            (prefix, new SolaxModel(role, row.Model, row.Family, row.Tier)))),
    ];
}
