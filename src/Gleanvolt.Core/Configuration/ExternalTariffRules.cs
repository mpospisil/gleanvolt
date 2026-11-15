using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Configuration;

/// <summary>
/// What an external tariff provider may publish (#401), checked before anything is stored, so the
/// timeline it becomes keeps the promise every tariff strategy makes: windows in order, half-open,
/// without a gap, and no two neighbours on the same rate.
/// </summary>
public static class ExternalTariffRules
{
    /// <summary>The shortest window: shorter ones are a clock error, not a tariff.</summary>
    public static readonly TimeSpan MinWindow = TimeSpan.FromMinutes(5);

    /// <summary>How far ahead the data may reach.</summary>
    public static readonly TimeSpan MaxHorizon = TimeSpan.FromDays(7);

    /// <summary>The most windows one publish may carry: 7 days of quarter hours, and some.</summary>
    public const int MaxWindows = 1000;

    /// <summary>
    /// Checks <paramref name="windows"/> and joins neighbours on the same rate with the same price.
    /// Returns the joined windows when there is no problem, and the problems otherwise.
    /// </summary>
    /// <param name="windows">As published, each with its offset as sent.</param>
    /// <param name="currency">What the provider says the prices are in.</param>
    /// <param name="expectedCurrency"><c>Tariff:Currency</c>.</param>
    /// <param name="now">When it was published.</param>
    public static (IReadOnlyList<TariffWindow> Windows, IReadOnlyList<TariffProblem> Problems) Check(
        IReadOnlyList<TariffWindow> windows, string? currency, string expectedCurrency, DateTimeOffset now)
    {
        var problems = new List<TariffProblem>();

        if (!string.Equals(currency?.Trim(), expectedCurrency, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(Problem(null, $"The prices are in {currency}, and Gleanvolt's are in {expectedCurrency}.",
                Phrase.Of("ExternalTariff.Currency", new PhraseArg.Text(currency ?? string.Empty), new PhraseArg.Text(expectedCurrency))));
        }

        if (windows.Count == 0)
        {
            problems.Add(Problem(null, "There are no windows.", Phrase.Of("ExternalTariff.Empty")));
            return ([], problems);
        }

        if (windows.Count > MaxWindows)
        {
            problems.Add(Problem(null, $"There are {windows.Count} windows; at most {MaxWindows} are accepted.",
                Phrase.Of("ExternalTariff.TooMany", new PhraseArg.Number(windows.Count), new PhraseArg.Number(MaxWindows))));
            return ([], problems);
        }

        for (var i = 0; i < windows.Count; i++)
        {
            var window = windows[i].Window;

            if (window.Start.Offset != TimeSpan.Zero || window.End.Offset != TimeSpan.Zero)
            {
                problems.Add(Problem(i, $"Window {i + 1} is not in UTC.", Phrase.Of("ExternalTariff.NotUtc", new PhraseArg.Number(i + 1))));
            }

            if (window.End - window.Start < MinWindow)
            {
                problems.Add(Problem(i, $"Window {i + 1} is shorter than {MinWindow.TotalMinutes:0} minutes.",
                    Phrase.Of("ExternalTariff.TooShort", new PhraseArg.Number(i + 1), new PhraseArg.Duration(MinWindow))));
            }

            if (windows[i].PricePerKwh is { } price && Math.Abs(price) > 1_000_000m)
            {
                problems.Add(Problem(i, $"Window {i + 1} has a price that cannot be right.", Phrase.Of("ExternalTariff.Price", new PhraseArg.Number(i + 1))));
            }

            if (i > 0)
            {
                var before = windows[i - 1].Window;

                if (window.Start > before.End)
                {
                    problems.Add(Problem(i, $"There is a gap before window {i + 1}.", Phrase.Of("ExternalTariff.Gap", new PhraseArg.Number(i + 1))));
                }
                else if (window.Start < before.End)
                {
                    problems.Add(Problem(i, $"Window {i + 1} overlaps the one before, or is out of order.",
                        Phrase.Of("ExternalTariff.Overlap", new PhraseArg.Number(i + 1))));
                }
                else if (windows[i].Rate == windows[i - 1].Rate && windows[i].PricePerKwh != windows[i - 1].PricePerKwh)
                {
                    problems.Add(Problem(i, $"Window {i + 1} is on the same rate as the one before, at another price. Neighbours on one rate must share a price.",
                        Phrase.Of("ExternalTariff.SameRateOtherPrice", new PhraseArg.Number(i + 1))));
                }
            }
        }

        if (problems.Count == 0)
        {
            if (windows[0].Window.Start > now || windows[^1].Window.End <= now)
            {
                problems.Add(Problem(null, "The windows do not cover the moment they were published.", Phrase.Of("ExternalTariff.NotNow")));
            }

            if (windows[^1].Window.End > now + MaxHorizon)
            {
                problems.Add(Problem(null, $"The windows reach more than {MaxHorizon.TotalDays:0} days ahead.",
                    Phrase.Of("ExternalTariff.TooFar", new PhraseArg.Duration(MaxHorizon))));
            }
        }

        return problems.Count > 0 ? ([], problems) : (Join(windows), problems);
    }

    // Neighbours on one rate at one price are one window: what the timeline promises, and what lets a
    // provider send fixed slots.
    private static List<TariffWindow> Join(IReadOnlyList<TariffWindow> windows)
    {
        var joined = new List<TariffWindow>();

        foreach (var window in windows)
        {
            var utc = window with { Window = new TimeWindow(window.Window.Start.ToUniversalTime(), window.Window.End.ToUniversalTime()) };

            if (joined.Count > 0 && joined[^1].Rate == utc.Rate && joined[^1].PricePerKwh == utc.PricePerKwh)
            {
                joined[^1] = joined[^1] with { Window = joined[^1].Window with { End = utc.Window.End } };
            }
            else
            {
                joined.Add(utc);
            }
        }

        return joined;
    }

    private static TariffProblem Problem(int? row, string message, Phrase phrase) =>
        new(TariffFields.Windows, row, message, Phrase: phrase);
}
