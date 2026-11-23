namespace Gleanvolt.Providers.Contracts.Charger;

/// <summary>
/// Whether a charger can do what Gleanvolt's charge control is set to (charger 1.1, #440), in one place for Gleanvolt
/// and for a plugin's tests. Gleanvolt checks it at every hello, refuses with <see cref="RefusalCodes.Limits"/> on any
/// problem, and words its own refusals in the viewer's language from these same rules.
/// </summary>
public static class ChargerLimitsRules
{
    /// <summary>
    /// Every way <paramref name="limits"/> asks more than <paramref name="charger"/> can do, in English, each naming
    /// the setting to change: empty when it fits.
    /// </summary>
    /// <param name="limits">The charge control's limits.</param>
    /// <param name="charger">What the charger can do.</param>
    /// <param name="name">The charger's name for the messages: the plugin's name.</param>
    public static IReadOnlyList<string> Check(ChargerLimits limits, ChargerCapabilities charger, string name)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(charger);
        var problems = new List<string>();

        if (limits.MinCurrentAmps < charger.MinCurrentAmps)
        {
            problems.Add(
                $"ChargeControl:MinChargingCurrentAmps ({limits.MinCurrentAmps} A) is below the lowest current the {name} "
                + $"charger charges at ({charger.MinCurrentAmps} A).");
        }

        if (limits.MaxCurrentAmps > charger.MaxCurrentAmps)
        {
            problems.Add(
                $"ChargeControl:MaxChargingCurrentAmps ({limits.MaxCurrentAmps} A) is above the highest current the {name} "
                + $"charger accepts ({charger.MaxCurrentAmps} A).");
        }

        if (limits.Phases > charger.Phases)
        {
            problems.Add($"ChargeControl:Phases ({limits.Phases}) is more than the {name} charger supplies ({charger.Phases}).");
        }

        return problems;
    }
}
