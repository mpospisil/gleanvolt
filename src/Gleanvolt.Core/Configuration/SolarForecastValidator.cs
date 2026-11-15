using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Configuration;

/// <summary>
/// What an external solar forecast provider may publish (#388), checked before anything reaches the
/// store: the planner charges the car by this forecast, so one that cannot be right is refused whole
/// rather than half-used. Each problem is a sentence for the provider's log and a phrase for Settings.
/// </summary>
public static class SolarForecastValidator
{
    /// <summary>The shortest period: shorter is a clock or unit error, not a forecast.</summary>
    public static readonly TimeSpan MinPeriod = TimeSpan.FromMinutes(5);

    /// <summary>The longest period: longer is too coarse to plan a charge by.</summary>
    public static readonly TimeSpan MaxPeriod = TimeSpan.FromMinutes(60);

    /// <summary>How far ahead the forecast may reach.</summary>
    public static readonly TimeSpan MaxHorizon = TimeSpan.FromDays(14);

    /// <summary>How far above the installed peak the median may go: cloud edges and cold panels, no more.</summary>
    public const double PeakAllowance = 1.2;

    /// <summary>The most periods one publish may carry: 14 days of 5-minute periods, and some.</summary>
    public const int MaxPeriods = 5000;

    /// <summary>How many problems with single periods are listed before the rest are only counted.</summary>
    public const int MaxListedPeriodProblems = 10;

    /// <summary>
    /// The problems with <paramref name="periods"/>; empty when it can be stored.
    /// </summary>
    /// <param name="periods">As published, each with its offset as sent.</param>
    /// <param name="installedPeakWatts">The array's kWp in W, or null when not configured: then the peak is not checked.</param>
    /// <param name="now">When it was published.</param>
    public static ConfigurationProblems Check(IReadOnlyList<SolarForecastPeriod> periods, double? installedPeakWatts, DateTimeOffset now)
    {
        var problems = new ConfigurationProblems();

        if (periods.Count == 0)
        {
            problems.Add("There are no periods.", Phrase.Of("SolarForecast.Empty"));
            return problems;
        }

        if (periods.Count > MaxPeriods)
        {
            problems.Add($"There are {periods.Count} periods; at most {MaxPeriods} are accepted.",
                Phrase.Of("SolarForecast.TooMany", new PhraseArg.Number(periods.Count), new PhraseArg.Number(MaxPeriods)));
            return problems;
        }

        var unlisted = 0;

        void PeriodProblem(string english, Phrase phrase)
        {
            if (problems.Count < MaxListedPeriodProblems)
            {
                problems.Add(english, phrase);
            }
            else
            {
                unlisted++;
            }
        }

        for (var i = 0; i < periods.Count; i++)
        {
            var period = periods[i];
            var number = new PhraseArg.Number(i + 1);

            if (period.PeriodEnd.Offset != TimeSpan.Zero)
            {
                PeriodProblem($"Period {i + 1} is not in UTC.", Phrase.Of("SolarForecast.NotUtc", number));
            }

            if (period.Period < MinPeriod || period.Period > MaxPeriod)
            {
                PeriodProblem(
                    $"Period {i + 1} is {period.Period.TotalMinutes:0.#} minutes long; a period is {MinPeriod.TotalMinutes:0} to {MaxPeriod.TotalMinutes:0} minutes.",
                    Phrase.Of("SolarForecast.Length", number, new PhraseArg.Duration(MinPeriod), new PhraseArg.Duration(MaxPeriod)));
            }

            if (!IsPower(period.EstimatedPowerWatts) || period.EstimatedPowerWattsP10 is { } p10 && !IsPower(p10)
                || period.EstimatedPowerWattsP90 is { } p90 && !IsPower(p90))
            {
                PeriodProblem($"Period {i + 1} has a power below zero, or one that is not a number.",
                    Phrase.Of("SolarForecast.Negative", number));
            }
            else if (period.EstimatedPowerWattsP10 > period.EstimatedPowerWatts || period.EstimatedPowerWattsP90 < period.EstimatedPowerWatts)
            {
                PeriodProblem($"In period {i + 1}, P10, P50 and P90 are not in order: P10 must be at most P50, and P90 at least P50.",
                    Phrase.Of("SolarForecast.Bands", number));
            }

            if (i > 0 && period.PeriodStart < periods[i - 1].PeriodEnd)
            {
                PeriodProblem($"Period {i + 1} overlaps the one before, or is out of order.", Phrase.Of("SolarForecast.Overlap", number));
            }
        }

        if (unlisted > 0)
        {
            problems.Add($"{unlisted} more problems with single periods are not listed.",
                Phrase.Of("SolarForecast.More", new PhraseArg.Number(unlisted)));
        }

        if (problems.Count > 0)
        {
            return problems;
        }

        if (periods[^1].PeriodEnd <= now)
        {
            problems.Add("Every period has already ended: the forecast has nothing still to come.", Phrase.Of("SolarForecast.Past"));
        }

        if (periods[^1].PeriodEnd > now + MaxHorizon)
        {
            problems.Add($"The forecast reaches more than {MaxHorizon.TotalDays:0} days ahead.",
                Phrase.Of("SolarForecast.TooFar", new PhraseArg.Duration(MaxHorizon)));
        }

        if (installedPeakWatts is > 0 and var installed)
        {
            var peak = periods.Max(period => period.EstimatedPowerWatts);

            if (peak > installed * PeakAllowance)
            {
                problems.Add(
                    $"The forecast peaks at {peak / 1000:0.##} kW, more than {PeakAllowance:0.#} times the installed {installed / 1000:0.##} kWp.",
                    Phrase.Of("SolarForecast.Peak", new PhraseArg.Power(peak), new PhraseArg.Power(installed)));
            }
        }

        return problems;
    }

    private static bool IsPower(double watts) => double.IsFinite(watts) && watts >= 0;
}
