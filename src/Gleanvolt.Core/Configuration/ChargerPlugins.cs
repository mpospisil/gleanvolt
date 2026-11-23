namespace Gleanvolt.Core.Configuration;

/// <summary>A charger plugin from gleanvolt-chargers that replaces a built-in charger driver (#444).</summary>
/// <param name="Name">What the plugin is called, e.g. "SolaX".</param>
/// <param name="Profile">Its name in GLEANVOLT_PLUGINS, its compose profile, its folder and its image: <c>solax</c>.</param>
public sealed record ChargerPlugin(string Name, string Profile)
{
    /// <summary>Where it is described and how it is run: its folder's README in gleanvolt-chargers.</summary>
    public string Url => $"https://github.com/mpospisil/gleanvolt-chargers/tree/main/{Profile}";

    /// <summary>Its image.</summary>
    public string Image => $"ghcr.io/mpospisil/gleanvolt-plugin-{Profile}";
}

/// <summary>
/// The charger plugins that replace the built-in charger drivers (#444). The built-in drivers still work in 1.3 and are
/// removed in 1.4 (#445): the log and Settings name the plugin for each one.
/// </summary>
public static class ChargerPlugins
{
    /// <summary>Every charger plugin, in the order the docs list them.</summary>
    public static IReadOnlyList<ChargerPlugin> All { get; } =
    [
        new("SolaX", "solax"),
        new("Huawei SCharger", "huawei"),
        new("go-e", "goe"),
        new("Shelly TopAC", "shelly"),
    ];

    /// <summary>
    /// The plugin that replaces the built-in charger driver <paramref name="driverId"/>, or null for one that has none:
    /// <c>external</c>, which is the plugin's, or an id no driver has.
    /// </summary>
    public static ChargerPlugin? Replacing(string? driverId) => driverId?.Trim().ToLowerInvariant() switch
    {
        "solax" => All[0],
        "huawei-7ks" or "huawei-22kt" => All[1],
        "goe" => All[2],
        _ => null,
    };
}
