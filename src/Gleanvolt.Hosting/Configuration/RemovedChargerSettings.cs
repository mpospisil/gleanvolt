using Gleanvolt.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace Gleanvolt.Hosting.Configuration;

/// <summary>
/// <c>Pv:Chargers</c>, which chose and addressed a built-in charger driver until 1.4 (#445). Those drivers are gone and
/// every charger is behind a plugin, so the section is ignored. Not refused, as <see cref="RetiredConfigurationKeys"/>
/// are: an upgraded installation still has <c>EV_CHARGER_HOST</c> in its <c>.env</c> (the plugin's containers read it),
/// and perhaps a brand saved on Settings, and neither stops it from running. A warning at startup says what replaced it.
/// </summary>
public static class RemovedChargerSettings
{
    /// <summary>The section.</summary>
    public const string Section = "Pv:Chargers";

    /// <summary>Whether <paramref name="key"/> is one of the section's: <c>Pv:Chargers:0:Host</c>.</summary>
    public static bool IsChargerKey(string key) => key.StartsWith(Section + ":", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The warning for <paramref name="configuration"/>, or null when nothing in <c>Pv:Chargers</c> chose a built-in
    /// driver: a driver, or a host (an empty driver with a host was SolaX). An id, a model or a name alone are labels,
    /// and the shipped configuration had them.
    /// </summary>
    public static string? Warning(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var charger = configuration.GetSection(Section).GetSection("0");
        var driver = charger["Driver"]?.Trim() ?? string.Empty;
        var host = charger["Host"]?.Trim() ?? string.Empty;

        if (driver.Length == 0 && host.Length == 0)
        {
            return null;
        }

        var ignored = "Pv:Chargers is ignored: Gleanvolt 1.4 has no built-in charger drivers, and the charger is driven "
            + "through a charger plugin. Remove the section (Pv__Chargers__0__* in .env; one saved on Settings goes with "
            + "the next save of Settings → Installation).";

        return ChargerPlugins.Replacing(driver, host) is { } plugin
            ? $"{ignored} The {plugin.Name} plugin replaced the built-in driver this configuration names: add {plugin.Profile} "
                + $"to GLEANVOLT_PLUGINS in .env and deploy again, or see {plugin.Url}. Until it connects, no mode starts."
            : $"{ignored} Until one connects, no mode starts.";
    }
}
