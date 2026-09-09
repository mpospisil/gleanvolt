using Microsoft.Extensions.Configuration;
using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Models;
using Gleanvolt.Infrastructure;

namespace Gleanvolt.Hosting.Configuration;

/// <summary>
/// Turns the <c>Pv</c> section into the one <see cref="PvSystemInfo"/> the process runs on (issue #111).
///
/// <para><b>One source.</b> The installation is described in one section and nowhere else. The older
/// keys that used to hold pieces of it — <c>Solax:Inverter</c>, <c>Solax:EvCharger</c>,
/// <c>Weather:Latitude</c>, <c>Weather:Longitude</c> — are gone, and one that is still set is refused
/// by <see cref="RetiredConfigurationKeys"/> rather than ignored. There is therefore no precedence to
/// reason about here, which was the point of the exercise.</para>
///
/// <para><b>Validated here, not at first use.</b> A site that cannot be described is a startup failure
/// naming the key, because every alternative is worse: a missing inverter address surfaces as a
/// connection error minutes later, and a half-set pair of coordinates silently moves the site into the
/// Atlantic. All problems are collected and reported together — fixing configuration one restart per
/// mistake is a miserable way to spend an evening.</para>
///
/// <para><b>What is left here is the binding.</b> The rules themselves live in
/// <see cref="PvSystemRules"/>, in <c>Gleanvolt.Core</c>, because the controller is no longer the only
/// thing that validates an installation — the portal has to refuse at save time exactly what this
/// refuses at startup, and a rule that exists in two repositories is a rule that will eventually mean
/// two different things. This type supplies what only a host knows: which section to read, what the
/// inverter's Modbus client is registered under, and that a problem here stops the process.</para>
/// </summary>
public static class PvSystemResolver
{
    /// <summary>The id a charger takes when none was configured.</summary>
    public const string DefaultChargerId = PvSystemRules.DefaultChargerId;

    /// <summary>How many chargers the control logic can actually drive. See <see cref="PvSystemOptions.Chargers"/>.</summary>
    public const int SupportedChargerCount = PvSystemRules.SupportedChargerCount;

    /// <summary>Reads the <c>Pv</c> section into one validated system.</summary>
    /// <exception cref="InvalidOperationException">
    /// The configuration does not describe a usable system. The message lists every problem found, each
    /// naming the configuration key it is about.
    /// </exception>
    public static PvSystemInfo Resolve(IConfiguration configuration)
    {
        var pv = configuration.GetSection(PvSystemOptions.SectionName).Get<PvSystemOptions>() ?? new PvSystemOptions();

        var problems = new List<string>();
        var site = PvSystemRules.Resolve(pv, ModbusClientKeys.Inverter, problems);

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "The PV system is not configured usably:" + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Select(problem => "  - " + problem)));
        }

        return site;
    }
}
