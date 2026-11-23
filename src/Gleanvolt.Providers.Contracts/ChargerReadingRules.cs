using System.Globalization;

namespace Gleanvolt.Providers.Contracts.Charger;

/// <summary>
/// The rules a <see cref="ChargerReading"/> must meet to be accepted (#435), in one place for Gleanvolt and for a
/// plugin's tests (#439). Gleanvolt words its own refusals in the viewer's language from these same limits.
/// </summary>
public static class ChargerReadingRules
{
    /// <summary>How far ahead of the host's clock <see cref="ChargerReading.ReadAt"/> may be.</summary>
    public static readonly TimeSpan MaxAhead = TimeSpan.FromMinutes(5);

    /// <summary>How old <see cref="ChargerReading.ReadAt"/> may be: a charger is controlled on what it does now.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(1);

    /// <summary>The highest <see cref="ChargerReading.PowerWatts"/>.</summary>
    public const double MaxPowerWatts = 100_000;

    /// <summary>The highest setpoint, and the highest ceiling, in amps.</summary>
    public const int MaxCurrentAmps = 80;

    /// <summary>The lowest <see cref="ChargerReading.MaxCurrentAmps"/>: no charger takes less than 6 A.</summary>
    public const int MinCeilingAmps = 6;

    /// <summary>The longest <see cref="ChargerReading.Mode"/>.</summary>
    public const int MaxModeLength = 50;

    /// <summary>Every rule <paramref name="reading"/> breaks at <paramref name="now"/>, in English; empty when it is accepted.</summary>
    public static IReadOnlyList<string> Check(ChargerReading reading, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var problems = new List<string>();
        var readAt = reading.ReadAt;

        if (readAt == default)
        {
            problems.Add("The reading has no time.");
        }
        else if (readAt > now + MaxAhead)
        {
            problems.Add(Invariant($"The charger was read at {readAt:O}, more than {MaxAhead.TotalMinutes:0} minutes ahead of Gleanvolt's clock."));
        }
        else if (readAt < now - MaxAge)
        {
            problems.Add(Invariant($"The charger was read at {readAt:O}, more than {MaxAge.TotalSeconds:0} seconds ago."));
        }

        if (!Enum.IsDefined(reading.Status))
        {
            problems.Add(Invariant($"The status {(int)reading.Status} is not one Gleanvolt knows."));
        }

        if (!(double.IsFinite(reading.PowerWatts) && reading.PowerWatts is >= 0 and <= MaxPowerWatts))
        {
            problems.Add(Invariant($"The power is {reading.PowerWatts:0.###} W; it is 0 to {MaxPowerWatts:0} W."));
        }

        if (reading.CurrentAmps is { } amps && amps is < 0 or > MaxCurrentAmps)
        {
            problems.Add(Invariant($"The setpoint is {amps} A; it is 0 to {MaxCurrentAmps} A."));
        }

        if (reading.MaxCurrentAmps is { } ceiling && ceiling is < MinCeilingAmps or > MaxCurrentAmps)
        {
            problems.Add(Invariant($"The charger's maximum is {ceiling} A; it is {MinCeilingAmps} to {MaxCurrentAmps} A."));
        }

        if (reading.Mode is { Length: > MaxModeLength })
        {
            problems.Add(Invariant($"The mode is longer than {MaxModeLength} characters."));
        }

        return problems;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
