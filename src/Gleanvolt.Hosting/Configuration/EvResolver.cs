using Microsoft.Extensions.Configuration;
using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Hosting.Configuration;

/// <summary>
/// Turns the <c>Ev</c> section into the one <see cref="EvInfo"/> the process runs on (issue #124).
///
/// <para><see cref="PvSystemResolver"/>'s arrangement, applied to the car: one section describes it,
/// validation happens here rather than at first use, and every problem is collected and reported
/// together — fixing configuration one restart per mistake is a miserable way to spend an evening.</para>
///
/// <para><b>An absent section is not a problem.</b> It resolves to <see cref="EvInfo.Unknown"/>, which
/// narrows nothing: an installation that has never described its car behaves exactly as it did before
/// this existed. That is what makes the feature safe to land ahead of anybody configuring it.</para>
///
/// <para>The rules live in <see cref="EvRules"/>, in <c>Gleanvolt.Core</c>, so that the portal refuses
/// at save time exactly what this refuses at startup. See <see cref="PvSystemResolver"/> for why.</para>
/// </summary>
public static class EvResolver
{
    /// <summary>How many vehicles the control logic can actually drive. See <see cref="EvOptions.Vehicles"/>.</summary>
    public const int SupportedVehicleCount = EvRules.SupportedVehicleCount;

    /// <summary>Reads the <c>Ev</c> section into one validated vehicle.</summary>
    /// <param name="configuration">The application's configuration.</param>
    /// <param name="chargerMinAmps">The charger's floor, for the band check.</param>
    /// <param name="chargerMaxAmps">The installation's ceiling, for the band check.</param>
    /// <exception cref="InvalidOperationException">
    /// The section does not describe a usable vehicle. The message lists every problem found, each
    /// naming the configuration key it is about.
    /// </exception>
    public static EvInfo Resolve(IConfiguration configuration, int chargerMinAmps, int chargerMaxAmps)
    {
        var ev = configuration.GetSection(EvOptions.SectionName).Get<EvOptions>() ?? new EvOptions();

        var problems = new List<string>();
        var info = EvRules.Resolve(ev, chargerMinAmps, chargerMaxAmps, problems);

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "The vehicle is not configured usably:" + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Select(problem => "  - " + problem)));
        }

        return info;
    }
}
