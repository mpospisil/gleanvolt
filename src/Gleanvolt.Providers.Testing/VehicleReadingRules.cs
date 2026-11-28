using Gleanvolt.Providers.Contracts.Vehicle;

namespace Gleanvolt.Providers.Testing;

/// <summary>
/// Gleanvolt's checks on a car's reading (VehicleReadingValidator, in a package this one can't use), in English
/// only. A test compares the two, so they don't drift apart.
/// </summary>
internal static class VehicleReadingRules
{
    private static readonly TimeSpan MaxAhead = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    private const double MaxRangeMetres = 2_000_000;
    private static readonly TimeSpan MaxChargeTimeRemaining = TimeSpan.FromHours(48);

    public static IReadOnlyList<string> Check(VehicleReading reading, VehicleReading? held, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var problems = new List<string>();

        if (reading.CapturedAt == default)
        {
            problems.Add("The reading has no capture time.");
        }
        else if (reading.CapturedAt > now + MaxAhead)
        {
            problems.Add($"The reading was captured at {reading.CapturedAt:O}, more than {MaxAhead.TotalMinutes:0} minutes ahead of Gleanvolt's clock.");
        }
        else if (reading.CapturedAt < now - MaxAge)
        {
            problems.Add($"The reading was captured at {reading.CapturedAt:O}, more than {MaxAge.TotalDays:0} days ago.");
        }
        else if (held is not null && reading.CapturedAt < held.CapturedAt)
        {
            // The same capture time is accepted: a car that has not reported since is a valid answer.
            problems.Add($"The reading is older than the reading Gleanvolt holds, captured {held.CapturedAt:O}.");
        }

        if (reading.SocPercent is { } soc && !(double.IsFinite(soc) && soc is >= 0 and <= 100))
        {
            problems.Add($"SOC is {soc}; it is 0 to 100 %.");
        }

        if (reading.RangeMetres is { } range && !(double.IsFinite(range) && range is >= 0 and <= MaxRangeMetres))
        {
            problems.Add($"The range is {range} m; it is 0 to {MaxRangeMetres / 1000:0} km, in metres.");
        }

        if (reading.ChargeTimeRemaining is { } remaining && (remaining < TimeSpan.Zero || remaining > MaxChargeTimeRemaining))
        {
            problems.Add($"The time to full is {remaining}; it is 0 to {MaxChargeTimeRemaining.TotalHours:0} hours.");
        }

        if (reading.ChargeState is { } charge && !Enum.IsDefined(charge))
        {
            problems.Add($"The charge state {(int)charge} is not one Gleanvolt knows.");
        }

        if (reading.PlugState is { } plug && !Enum.IsDefined(plug))
        {
            problems.Add($"The plug state {(int)plug} is not one Gleanvolt knows.");
        }

        // Unknown counts as absent: a reading that says nothing about the car is not a reading.
        if (reading.SocPercent is null && reading.RangeMetres is null
            && reading.ChargeState is null or CarChargeState.Unknown && reading.PlugState is null or CarPlugState.Unknown)
        {
            problems.Add("The reading says nothing: it needs at least one of SOC, range, charge state or plug state.");
        }

        return problems;
    }
}
