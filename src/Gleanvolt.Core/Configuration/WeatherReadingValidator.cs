using System.Globalization;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Configuration;

/// <summary>
/// What an external weather provider may publish (#424), checked before the reading is held for the next
/// charging session. The weather is decoration on a session record, and nothing in charge control reads
/// it, but a session keeps what it was given for good: a reading in the wrong units (Fahrenheit, hPa) is
/// refused whole rather than stored. Each problem is a sentence for the provider's log and a phrase for
/// the web UI.
/// </summary>
public static class WeatherReadingValidator
{
    /// <summary>How far ahead of this clock an observation may be: clock drift, not a reading from the future.</summary>
    public static readonly TimeSpan MaxAhead = TimeSpan.FromMinutes(5);

    /// <summary>The oldest observation accepted. A session is recorded against the weather it ran in, not yesterday's.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(3);

    /// <summary>The coldest air temperature accepted, °C: below the coldest ever measured.</summary>
    public const double MinTemperatureCelsius = -90;

    /// <summary>The warmest, °C: above the hottest ever measured.</summary>
    public const double MaxTemperatureCelsius = 60;

    /// <summary>The lowest pressure accepted, Pa: a high mountain site. A value in hPa is far below it.</summary>
    public const double MinPressurePascals = 85_000;

    /// <summary>The highest, Pa.</summary>
    public const double MaxPressurePascals = 110_000;

    /// <summary>The longest visibility accepted, m.</summary>
    public const double MaxVisibilityMetres = 100_000;

    /// <summary>The longest condition, in characters.</summary>
    public const int MaxConditionLength = 100;

    /// <summary>The longest condition description, in characters.</summary>
    public const int MaxDescriptionLength = 200;

    /// <summary>The problems with <paramref name="reading"/>; empty when it can be held.</summary>
    /// <param name="reading">As published, with its observation time's offset as sent.</param>
    /// <param name="held">The reading Gleanvolt holds from the provider, or null.</param>
    /// <param name="now">When it was published.</param>
    public static ConfigurationProblems Check(WeatherReading reading, WeatherReading? held, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var problems = new ConfigurationProblems();
        var observation = reading.Observation;
        var observedAt = observation.ObservedAt;

        if (observedAt == default)
        {
            problems.Add("The reading has no observation time.", Phrase.Of("Weather.NoObservedAt"));
        }
        else if (observedAt > now + MaxAhead)
        {
            problems.Add(
                $"The weather was observed at {observedAt:O}, more than {MaxAhead.TotalMinutes:0} minutes ahead of Gleanvolt's clock.",
                Phrase.Of("Weather.Future", new PhraseArg.Date(observedAt), new PhraseArg.Clock(observedAt), new PhraseArg.Duration(MaxAhead)));
        }
        else if (observedAt < now - MaxAge)
        {
            problems.Add(
                $"The weather was observed at {observedAt:O}, more than {MaxAge.TotalHours:0} hours ago.",
                Phrase.Of("Weather.TooOld", new PhraseArg.Date(observedAt), new PhraseArg.Clock(observedAt), new PhraseArg.Duration(MaxAge)));
        }
        else if (held is not null && observedAt < held.Observation.ObservedAt)
        {
            // The same time is accepted: a service that has not observed again since is a valid answer.
            var heldAt = held.Observation.ObservedAt;
            problems.Add(
                $"The reading is older than the reading Gleanvolt holds, observed {heldAt:O}.",
                Phrase.Of("Weather.OlderThanHeld", new PhraseArg.Date(heldAt), new PhraseArg.Clock(heldAt)));
        }

        Range(problems, observation.TemperatureCelsius, MinTemperatureCelsius, MaxTemperatureCelsius,
            value => $"The temperature is {value} °C; it is {MinTemperatureCelsius:0} to {MaxTemperatureCelsius:0} °C.",
            "Weather.Temperature");

        Range(problems, observation.PressurePascals, MinPressurePascals, MaxPressurePascals,
            value => $"The pressure is {value} Pa; it is {MinPressurePascals:0} to {MaxPressurePascals:0} Pa (hPa × 100).",
            "Weather.Pressure");

        Range(problems, observation.HumidityPercent, 0, 100,
            value => $"The humidity is {value}; it is 0 to 100 %.",
            "Weather.Humidity");

        Range(problems, observation.CloudsPercent, 0, 100,
            value => $"The cloud cover is {value}; it is 0 to 100 %.",
            "Weather.Clouds");

        Range(problems, observation.VisibilityMetres, 0, MaxVisibilityMetres,
            value => $"The visibility is {value} m; it is 0 to {MaxVisibilityMetres / 1000:0} km, in metres.",
            "Weather.Visibility");

        if (observation.Condition.Length > MaxConditionLength)
        {
            problems.Add($"The condition is longer than {MaxConditionLength} characters.",
                Phrase.Of("Weather.ConditionTooLong", new PhraseArg.Number(MaxConditionLength)));
        }

        if (observation.ConditionDescription.Length > MaxDescriptionLength)
        {
            problems.Add($"The condition description is longer than {MaxDescriptionLength} characters.",
                Phrase.Of("Weather.DescriptionTooLong", new PhraseArg.Number(MaxDescriptionLength)));
        }

        if (reading.Sunrise is { } sunrise && reading.Sunset is { } sunset && sunset <= sunrise)
        {
            problems.Add($"Sunset ({sunset:O}) is not after sunrise ({sunrise:O}).", Phrase.Of("Weather.SunsetBeforeSunrise"));
        }

        if (observation.TemperatureCelsius is null && string.IsNullOrWhiteSpace(observation.Condition)
            && string.IsNullOrWhiteSpace(observation.ConditionDescription))
        {
            problems.Add("The reading says nothing: it needs at least a temperature or a condition.", Phrase.Of("Weather.Empty"));
        }

        return problems;
    }

    private static void Range(ConfigurationProblems problems, double? value, double min, double max, Func<string, string> english, string phraseKey)
    {
        if (value is { } number && !(double.IsFinite(number) && number >= min && number <= max))
        {
            var text = number.ToString("0.###", CultureInfo.InvariantCulture);
            problems.Add(english(text), Phrase.Of(phraseKey, new PhraseArg.Text(text)));
        }
    }
}
