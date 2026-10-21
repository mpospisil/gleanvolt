using System.Globalization;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Configuration;

/// <summary>
/// What makes tariff settings savable (issue #265), stated once for the places that check them: the
/// Settings form, as the owner types, and the service that writes the file, which the API and a second
/// browser tab reach without going through the form. The price rules apply whichever strategy is
/// selected; the table rules are the <see cref="TariffProviderIds.Table"/> strategy's.
/// </summary>
public static class TariffRules
{
    /// <summary>Minutes in a day, which is also what an end of <c>24:00</c> parses to.</summary>
    public const int MinutesPerDay = 24 * 60;

    /// <summary>
    /// <paramref name="text"/> as minutes after midnight, or null when it is not <c>H:mm</c> or
    /// <c>HH:mm</c>. <c>24:00</c> is accepted only when <paramref name="isEnd"/>: the end of the day is a
    /// place a period can stop, never one it can start.
    /// </summary>
    public static int? ParseMinutes(string? text, bool isEnd)
    {
        var parts = (text ?? string.Empty).Trim().Split(':');

        if (parts.Length != 2
            || parts[0].Length is < 1 or > 2
            || parts[1].Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || minutes > 59)
        {
            return null;
        }

        var total = hours * 60 + minutes;
        return total < MinutesPerDay || (isEnd && total == MinutesPerDay) ? total : null;
    }

    /// <summary>Minutes after midnight as <c>HH:mm</c>, with the end of the day as <c>24:00</c>.</summary>
    public static string FormatMinutes(int minutes) =>
        string.Create(CultureInfo.InvariantCulture, $"{minutes / 60:00}:{minutes % 60:00}");

    /// <summary>The shared prices' problems, which apply whichever strategy is selected; empty when there are none.</summary>
    public static IReadOnlyList<TariffProblem> CheckPrices(decimal? low, decimal? high)
    {
        var problems = new List<TariffProblem>();

        if (low < 0)
        {
            problems.Add(new TariffProblem(TariffFields.LowPrice, null, "A price can't be negative."));
        }

        if (high < 0)
        {
            problems.Add(new TariffProblem(TariffFields.HighPrice, null, "A price can't be negative."));
        }

        // A warning, not a block: nothing in the controller acts on the prices yet, the times are what
        // matter, and a tariff this odd could exist. Wrong prices cost a wrong-looking figure.
        if (low is { } l && high is { } h && l >= 0 && l > h)
        {
            problems.Add(new TariffProblem(TariffFields.LowPrice, null, "Low tariff costs more than high.", IsWarning: true));
        }

        return problems;
    }

    /// <summary>Every problem with <paramref name="table"/>, errors and warnings together; empty when there are none.</summary>
    public static IReadOnlyList<TariffProblem> CheckTable(TariffTable table)
    {
        var problems = new List<TariffProblem>();

        CheckPeriods(table.Weekday, TariffFields.TableWeekday, problems);
        CheckPeriods(table.Weekend, TariffFields.TableWeekend, problems);

        if (table.Weekday.Count == 0 && table.Weekend.Count == 0)
        {
            problems.Add(new TariffProblem(
                TariffFields.Table, null, "No low-tariff periods: everything would be high tariff.", IsWarning: true));
        }

        return problems;
    }

    /// <summary>
    /// <paramref name="table"/> as it is stored: each time as <c>HH:mm</c> and each list sorted by start,
    /// whatever order the rows were added in. Only meaningful for a table <see cref="CheckTable"/> passed.
    /// </summary>
    public static TariffTable Normalize(TariffTable table) => new(Normalize(table.Weekday), Normalize(table.Weekend));

    /// <summary>
    /// The parsed periods of a list as minute ranges sorted by start. Rows that do not parse are
    /// skipped, so a caller drawing a half-typed table draws what it can.
    /// </summary>
    public static IReadOnlyList<(int Start, int End)> Minutes(IReadOnlyList<TariffPeriod> periods) =>
    [
        .. periods
            .Select(period => (Start: ParseMinutes(period.Start, isEnd: false), End: ParseMinutes(period.End, isEnd: true)))
            .Where(period => period.Start is { } start && period.End is { } end && start < end)
            .Select(period => (period.Start!.Value, period.End!.Value))
            .OrderBy(period => period.Item1),
    ];

    private static List<TariffPeriod> Normalize(IReadOnlyList<TariffPeriod> periods) =>
        [.. Minutes(periods).Select(period => new TariffPeriod(FormatMinutes(period.Start), FormatMinutes(period.End)))];

    private static void CheckPeriods(IReadOnlyList<TariffPeriod> periods, string field, List<TariffProblem> problems)
    {
        var parsed = new (int Start, int End)?[periods.Count];

        for (var row = 0; row < periods.Count; row++)
        {
            var start = ParseMinutes(periods[row].Start, isEnd: false);
            var end = ParseMinutes(periods[row].End, isEnd: true);

            if (start is null || end is null)
            {
                problems.Add(new TariffProblem(field, row, "Not a time."));
            }
            else if (start >= end)
            {
                problems.Add(new TariffProblem(
                    field, row, "Ends before it starts. Split a period over midnight into two rows."));
            }
            else
            {
                parsed[row] = (start.Value, end.Value);
            }
        }

        // Half-open, so 06:00–08:00 and 08:00–10:00 meet without overlapping. Each row names the first
        // row it collides with, so both ends of an overlap point at each other.
        for (var row = 0; row < periods.Count; row++)
        {
            if (parsed[row] is not { } mine)
            {
                continue;
            }

            for (var other = 0; other < periods.Count; other++)
            {
                if (other != row && parsed[other] is { } theirs && mine.Start < theirs.End && theirs.Start < mine.End)
                {
                    problems.Add(new TariffProblem(
                        field, row, $"Overlaps {FormatMinutes(theirs.Start)}–{FormatMinutes(theirs.End)}."));
                    break;
                }
            }
        }
    }
}
