using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Configuration;

/// <summary>
/// What an external car provider may publish (#408), checked before the reading reaches
/// <see cref="VehicleStateHolder"/>. The reading is advisory, as every car reading is, but a percentage
/// target converts from its SOC, so one that cannot be right is refused whole. Each problem is a sentence
/// for the provider's log and a phrase for the web UI.
/// </summary>
public static class VehicleReadingValidator
{
    /// <summary>How far ahead of this clock a capture time may be: clock drift, not a reading from the future.</summary>
    public static readonly TimeSpan MaxAhead = TimeSpan.FromMinutes(5);

    /// <summary>The oldest capture time accepted. Older is a unit or clock error, not a parked car.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    /// <summary>The longest range any car could claim, in metres.</summary>
    public const double MaxRangeMetres = 2_000_000;

    /// <summary>The longest charge any car could still need.</summary>
    public static readonly TimeSpan MaxChargeTimeRemaining = TimeSpan.FromHours(48);

    /// <summary>The problems with <paramref name="reading"/>; empty when it can be stored.</summary>
    /// <param name="reading">As published, with its capture time's offset as sent.</param>
    /// <param name="held">The reading Gleanvolt holds, from any feed, or null.</param>
    /// <param name="now">When it was published.</param>
    public static ConfigurationProblems Check(VehicleState reading, VehicleState? held, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var problems = new ConfigurationProblems();

        if (reading.CapturedAt == default)
        {
            problems.Add("The reading has no capture time.", Phrase.Of("Vehicle.NoCaptureTime"));
        }
        else if (reading.CapturedAt > now + MaxAhead)
        {
            problems.Add(
                $"The reading was captured at {reading.CapturedAt:O}, more than {MaxAhead.TotalMinutes:0} minutes ahead of Gleanvolt's clock.",
                Phrase.Of("Vehicle.Future", new PhraseArg.Date(reading.CapturedAt), new PhraseArg.Clock(reading.CapturedAt), new PhraseArg.Duration(MaxAhead)));
        }
        else if (reading.CapturedAt < now - MaxAge)
        {
            problems.Add(
                $"The reading was captured at {reading.CapturedAt:O}, more than {MaxAge.TotalDays:0} days ago.",
                Phrase.Of("Vehicle.TooOld", new PhraseArg.Date(reading.CapturedAt), new PhraseArg.Duration(MaxAge)));
        }
        else if (held is not null && reading.CapturedAt < held.CapturedAt)
        {
            // The same capture time is accepted: a car that has not reported since is a valid answer.
            problems.Add(
                $"The reading is older than the reading Gleanvolt holds, captured {held.CapturedAt:O}.",
                Phrase.Of("Vehicle.OlderThanHeld", new PhraseArg.Date(held.CapturedAt), new PhraseArg.Clock(held.CapturedAt)));
        }

        if (reading.SocPercent is { } soc && !(double.IsFinite(soc) && soc is >= 0 and <= 100))
        {
            problems.Add($"SOC is {soc}; it is 0 to 100 %.", Phrase.Of("Vehicle.Soc", new PhraseArg.Text(Number(soc))));
        }

        if (reading.RangeMetres is { } range && !(double.IsFinite(range) && range is >= 0 and <= MaxRangeMetres))
        {
            problems.Add(
                $"The range is {range} m; it is 0 to {MaxRangeMetres / 1000:0} km, in metres.",
                Phrase.Of("Vehicle.Range", new PhraseArg.Text(Number(range))));
        }

        if (reading.ChargeTimeRemaining is { } remaining && (remaining < TimeSpan.Zero || remaining > MaxChargeTimeRemaining))
        {
            problems.Add(
                $"The time to full is {remaining}; it is 0 to {MaxChargeTimeRemaining.TotalHours:0} hours.",
                Phrase.Of("Vehicle.TimeRemaining", new PhraseArg.Text(remaining.ToString()), new PhraseArg.Duration(MaxChargeTimeRemaining)));
        }

        if (!Enum.IsDefined(reading.ChargeState))
        {
            problems.Add($"The charge state {(int)reading.ChargeState} is not one Gleanvolt knows.",
                Phrase.Of("Vehicle.UnknownChargeState", new PhraseArg.Number((int)reading.ChargeState)));
        }

        if (!Enum.IsDefined(reading.PlugState))
        {
            problems.Add($"The plug state {(int)reading.PlugState} is not one Gleanvolt knows.",
                Phrase.Of("Vehicle.UnknownPlugState", new PhraseArg.Number((int)reading.PlugState)));
        }

        // Unknown counts as absent: a reading that says nothing about the car is not a reading.
        if (reading.SocPercent is null && reading.RangeMetres is null
            && reading.ChargeState == VehicleChargeState.Unknown && reading.PlugState == VehiclePlugState.Unknown)
        {
            problems.Add("The reading says nothing: it needs at least one of SOC, range, charge state or plug state.",
                Phrase.Of("Vehicle.Empty"));
        }

        return problems;
    }

    private static string Number(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
