using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
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
/// </summary>
public static partial class EvResolver
{
    /// <summary>How many vehicles the control logic can actually drive. See <see cref="EvOptions.Vehicles"/>.</summary>
    public const int SupportedVehicleCount = 1;

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,47}$")]
    private static partial Regex Slug();

    /// <summary>Reads the <c>Ev</c> section into one validated vehicle.</summary>
    /// <param name="configuration">The application's configuration.</param>
    /// <param name="chargerMinAmps">The charger's floor, for the band check below.</param>
    /// <param name="chargerMaxAmps">The installation's ceiling, for the band check below.</param>
    /// <exception cref="InvalidOperationException">
    /// The section does not describe a usable vehicle. The message lists every problem found, each
    /// naming the configuration key it is about.
    /// </exception>
    public static EvInfo Resolve(IConfiguration configuration, int chargerMinAmps, int chargerMaxAmps)
    {
        var ev = configuration.GetSection(EvOptions.SectionName).Get<EvOptions>() ?? new EvOptions();

        if (ev.Vehicles.Length == 0)
        {
            return EvInfo.Unknown;
        }

        var problems = new List<string>();

        if (ev.Vehicles.Length > SupportedVehicleCount)
        {
            // Refused rather than ignored. Silently driving the first of three cars is worse than not
            // starting: the other two are configured, visible in the file, and doing nothing.
            problems.Add(
                $"Ev:Vehicles has {ev.Vehicles.Length} entries and exactly {SupportedVehicleCount} is "
                + "supported. The list is a shape for later, not a feature yet.");
        }

        var vehicle = ev.Vehicles[0];
        var id = ResolveId(vehicle, problems);

        var info = new EvInfo(
            Id: id,
            Name: string.IsNullOrWhiteSpace(vehicle.Name) ? id : vehicle.Name.Trim(),
            Make: vehicle.Make.Trim(),
            Model: vehicle.Model.Trim(),
            BatteryCapacityKWh: Positive(vehicle.BatteryCapacityKWh, "Ev:Vehicles:0:BatteryCapacityKWh", problems),
            ChargeEfficiency: Efficiency(vehicle.ChargeEfficiency, problems),
            Phases: Phases(vehicle.Phases, problems),
            MinChargingCurrentAmps: Amps(vehicle.MinChargingCurrentAmps, "Ev:Vehicles:0:MinChargingCurrentAmps", problems),
            MaxChargingCurrentAmps: Amps(vehicle.MaxChargingCurrentAmps, "Ev:Vehicles:0:MaxChargingCurrentAmps", problems),
            TelemetryTopic: vehicle.Telemetry?.Topic.Trim() ?? string.Empty);

        ValidateBand(info, chargerMinAmps, chargerMaxAmps, problems);

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "The vehicle is not configured usably:" + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Select(problem => "  - " + problem)));
        }

        return info;
    }

    private static string ResolveId(EvVehicleOptions vehicle, List<string> problems)
    {
        var id = vehicle.Id.Trim();

        if (id.Length == 0)
        {
            return string.Empty;
        }

        if (!Slug().IsMatch(id))
        {
            problems.Add(
                $"Ev:Vehicles:0:Id ('{id}') must be a slug: lower-case letters, digits, '-' and '_', "
                + "starting with a letter or digit, at most 48 characters.");
        }

        return id;
    }

    /// <summary>
    /// The check worth having: a car whose floor is above the installation's ceiling can never charge
    /// at all, and every symptom of that is silence. Caught here, naming both keys, rather than
    /// discovered one evening as "I pressed the button and nothing happened".
    /// </summary>
    private static void ValidateBand(EvInfo ev, int chargerMinAmps, int chargerMaxAmps, List<string> problems)
    {
        if (ev.MinChargingCurrentAmps is { } evMin && ev.MaxChargingCurrentAmps is { } evMax && evMin > evMax)
        {
            problems.Add(
                $"Ev:Vehicles:0:MinChargingCurrentAmps ({evMin}A) is above "
                + $"Ev:Vehicles:0:MaxChargingCurrentAmps ({evMax}A); no current satisfies both.");
            return;
        }

        var limits = ChargingLimits.Intersect(chargerMinAmps, chargerMaxAmps, 1, ev);

        if (limits.IsEmpty)
        {
            problems.Add(
                $"The car and the charger have no current in common: the car needs at least "
                + $"{limits.MinAmps}A (Ev:Vehicles:0:MinChargingCurrentAmps) and the installation allows "
                + $"at most {limits.MaxAmps}A (ChargeControl:MaxChargingCurrentAmps). It could never charge.");
        }
    }

    private static double Positive(double value, string key, List<string> problems)
    {
        if (value < 0)
        {
            problems.Add($"{key} ({value}) cannot be negative.");
            return 0;
        }

        return value;
    }

    private static double Efficiency(double value, List<string> problems)
    {
        if (value is <= 0 or > 1)
        {
            problems.Add(
                $"Ev:Vehicles:0:ChargeEfficiency ({value}) must be greater than 0 and at most 1 — it is "
                + "the fraction of metered energy that reaches the cells.");
            return 0.9;
        }

        return value;
    }

    private static int? Phases(int? value, List<string> problems)
    {
        if (value is null)
        {
            return null;
        }

        if (value is not (1 or 2 or 3))
        {
            problems.Add($"Ev:Vehicles:0:Phases ({value}) must be 1, 2 or 3.");
            return null;
        }

        return value;
    }

    private static int? Amps(int? value, string key, List<string> problems)
    {
        if (value is null)
        {
            return null;
        }

        if (value <= 0)
        {
            problems.Add($"{key} ({value}) must be greater than zero.");
            return null;
        }

        return value;
    }
}
