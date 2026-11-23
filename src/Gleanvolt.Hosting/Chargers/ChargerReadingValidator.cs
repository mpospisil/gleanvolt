using System.Globalization;
using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Models;
using Gleanvolt.Providers.Contracts.Charger;

namespace Gleanvolt.Hosting.Chargers;

/// <summary>
/// What a charger plugin may publish (#435), checked before the control loop sees it. The loop writes to the
/// charger on what it reads, so a reading in the wrong units (kW for W) or from a minute ago is refused whole,
/// and the poll then reports a charger it could not read. Each problem is a sentence for the plugin's log and a
/// phrase for the web UI.
/// </summary>
public static class ChargerReadingValidator
{
    // The limits are the contract's (#439), so a plugin's tests and Gleanvolt refuse the same readings; only
    // the wording, in the viewer's language, is Gleanvolt's own.
    public static readonly TimeSpan MaxAhead = ChargerReadingRules.MaxAhead;

    public static readonly TimeSpan MaxAge = ChargerReadingRules.MaxAge;

    public const double MaxPowerWatts = ChargerReadingRules.MaxPowerWatts;

    public const int MaxCurrentAmps = ChargerReadingRules.MaxCurrentAmps;

    public const int MinCeilingAmps = ChargerReadingRules.MinCeilingAmps;

    public const int MaxModeLength = ChargerReadingRules.MaxModeLength;

    /// <summary>The problems with <paramref name="reading"/>; empty when it can be held.</summary>
    public static ConfigurationProblems Check(ChargerReading reading, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var problems = new ConfigurationProblems();
        var readAt = reading.ReadAt;

        if (readAt == default)
        {
            problems.Add("The reading has no time.", Phrase.Of("Charger.NoReadAt"));
        }
        else if (readAt > now + MaxAhead)
        {
            problems.Add(
                $"The charger was read at {readAt:O}, more than {MaxAhead.TotalMinutes:0} minutes ahead of Gleanvolt's clock.",
                Phrase.Of("Charger.Future", new PhraseArg.Date(readAt), new PhraseArg.Clock(readAt), new PhraseArg.Duration(MaxAhead)));
        }
        else if (readAt < now - MaxAge)
        {
            problems.Add(
                $"The charger was read at {readAt:O}, more than {MaxAge.TotalSeconds:0} seconds ago.",
                Phrase.Of("Charger.TooOld", new PhraseArg.Date(readAt), new PhraseArg.Clock(readAt), new PhraseArg.Duration(MaxAge)));
        }

        if (!Enum.IsDefined(reading.Status))
        {
            var text = ((int)reading.Status).ToString(CultureInfo.InvariantCulture);
            problems.Add($"The status {text} is not one Gleanvolt knows.", Phrase.Of("Charger.Status", new PhraseArg.Text(text)));
        }

        if (!(double.IsFinite(reading.PowerWatts) && reading.PowerWatts is >= 0 and <= MaxPowerWatts))
        {
            var text = reading.PowerWatts.ToString("0.###", CultureInfo.InvariantCulture);
            problems.Add($"The power is {text} W; it is 0 to {MaxPowerWatts:0} W.",
                Phrase.Of("Charger.Power", new PhraseArg.Text(text), new PhraseArg.Number((long)MaxPowerWatts)));
        }

        if (reading.CurrentAmps is { } amps && amps is < 0 or > MaxCurrentAmps)
        {
            problems.Add($"The setpoint is {amps} A; it is 0 to {MaxCurrentAmps} A.",
                Phrase.Of("Charger.Current", new PhraseArg.Number(amps), new PhraseArg.Number(MaxCurrentAmps)));
        }

        if (reading.MaxCurrentAmps is { } ceiling && ceiling is < MinCeilingAmps or > MaxCurrentAmps)
        {
            problems.Add($"The charger's maximum is {ceiling} A; it is {MinCeilingAmps} to {MaxCurrentAmps} A.",
                Phrase.Of("Charger.MaxCurrent", new PhraseArg.Number(ceiling), new PhraseArg.Number(MinCeilingAmps), new PhraseArg.Number(MaxCurrentAmps)));
        }

        if (reading.Mode is { Length: > MaxModeLength })
        {
            problems.Add($"The mode is longer than {MaxModeLength} characters.",
                Phrase.Of("Charger.ModeTooLong", new PhraseArg.Number(MaxModeLength)));
        }

        return problems;
    }
}
