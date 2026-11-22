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
    /// <summary>How far ahead of this clock a reading may be: clock drift, not a reading from the future.</summary>
    public static readonly TimeSpan MaxAhead = TimeSpan.FromMinutes(5);

    /// <summary>The oldest reading accepted. A charger is controlled on what it is doing now.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(1);

    /// <summary>The most power accepted, W: above any wallbox's 22 kW, well below a value sent in mW.</summary>
    public const double MaxPowerWatts = 100_000;

    /// <summary>The highest setpoint accepted, A.</summary>
    public const int MaxCurrentAmps = 80;

    /// <summary>The lowest ceiling a charger may report, A: IEC 61851's minimum.</summary>
    public const int MinCeilingAmps = 6;

    /// <summary>The longest mode name, in characters.</summary>
    public const int MaxModeLength = 50;

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
