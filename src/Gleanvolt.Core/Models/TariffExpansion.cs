using Gleanvolt.Core.Configuration;

namespace Gleanvolt.Core.Models;

/// <summary>
/// Turns a <see cref="TariffTable"/>'s wall-clock periods into a <see cref="TariffTimeline"/> of
/// instants (issue #265).
///
/// <para><b>Through the zone's rules, never by adding hours.</b> Each edge is a local date and time
/// converted on its own, so a day is 23 or 25 hours long when the clocks say so. On the spring-forward
/// night 02:00–03:00 does not exist, and an edge inside it moves to 03:00, the first minute that does.
/// In autumn 02:00–03:00 happens twice, and an edge inside it is the first time round, so a day's
/// edges stay in order.</para>
///
/// <para><b>Joined across midnight.</b> 24:00 one day is 00:00 the next, and a low window that ends
/// there and one that starts there are one window. That holds from Friday into Saturday and Sunday into
/// Monday too, where the two rows come from different lists.</para>
/// </summary>
public static class TariffExpansion
{
    /// <summary>
    /// The timeline from the start of <paramref name="today"/> to the end of the day after it, or null
    /// when the table's provider is <see cref="TariffProvider.None"/>.
    /// </summary>
    /// <param name="table">The owner's table; assumed to have passed <see cref="TariffRules.Check"/>.</param>
    /// <param name="zone">The zone the table's times are in.</param>
    /// <param name="today">The local date the timeline starts on.</param>
    /// <param name="currency">What the prices are in.</param>
    public static TariffTimeline? Expand(TariffTable table, TimeZoneInfo zone, DateOnly today, string currency)
    {
        if (table.Provider == TariffProvider.None)
        {
            return null;
        }

        var start = ToInstant(today, 0, zone);
        var end = ToInstant(today.AddDays(2), 0, zone);

        var low = new List<TimeWindow>();

        // One day further on than the horizon, so a window that runs over it is cut at the horizon
        // rather than ended at a midnight the next day would have joined.

        for (var date = today; date <= today.AddDays(2); date = date.AddDays(1))
        {
            foreach (var (from, to) in TariffRules.Minutes(table.PeriodsFor(TariffTable.DaysOf(date))))
            {
                var window = new TimeWindow(ToInstant(date, from, zone), ToInstant(date, to, zone));

                if (window.Duration <= TimeSpan.Zero)
                {
                    // An edge inside the skipped hour can collapse a period that lay wholly within it.
                    continue;
                }

                if (low.Count > 0 && low[^1].End >= window.Start)
                {
                    low[^1] = low[^1] with { End = window.End > low[^1].End ? window.End : low[^1].End };
                }
                else
                {
                    low.Add(window);
                }
            }
        }

        var windows = new List<TariffWindow>();
        var cursor = start;

        foreach (var window in low)
        {
            if (window.End <= start || window.Start >= end)
            {
                continue;
            }

            var from = window.Start > start ? window.Start : start;
            var to = window.End < end ? window.End : end;

            if (from > cursor)
            {
                windows.Add(new TariffWindow(new TimeWindow(cursor, from), TariffRate.High));
            }

            windows.Add(new TariffWindow(new TimeWindow(from, to), TariffRate.Low));
            cursor = to;
        }

        if (cursor < end)
        {
            windows.Add(new TariffWindow(new TimeWindow(cursor, end), TariffRate.High));
        }

        return new TariffTimeline(windows, table.PricesIn(currency));
    }

    /// <summary>
    /// <paramref name="minutes"/> after local midnight on <paramref name="date"/>, as an instant: the
    /// first valid minute when it falls in a spring-forward gap, the first occurrence when it falls in
    /// an autumn overlap. 1440 is the next day's midnight.
    /// </summary>
    public static DateTimeOffset ToInstant(DateOnly date, int minutes, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified).AddMinutes(minutes);

        // No zone skips more than a few hours, so this ends well inside a day.
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        // The first time round is the one with the larger offset: the summer time the clocks are about
        // to leave.
        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);

        return new DateTimeOffset(local, offset);
    }
}
