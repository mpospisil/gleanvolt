using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Web.Components;

/// <summary>
/// Today and tomorrow of forecast, shaped for the chart on <c>/forecast</c> (#219) — the same periods
/// the table beneath it prints, turned into the series uPlot wants.
///
/// <para>A record rather than arithmetic in the markup, for the same reason
/// <see cref="EnergyChartSeries"/> is one: the things easiest to get quietly wrong here are the
/// things a picture cannot be checked for by eye. <b>A day the history only half covers must start
/// where the history starts</b>, not at the left edge; <b>a stretch with no periods at all must be a
/// hole</b>, not a line drawn across it; and <b>a period the provider sent no band for must not read
/// as a certain one</b>.</para>
///
/// <para>Everything on the power axis is in <b>watts</b> — the provider's own figure for a period is
/// an average power, and the energy the table prints is that power over the period's length.</para>
/// </summary>
/// <param name="Timestamps">Unix seconds for every plotted point, ascending.</param>
/// <param name="Expected">The median estimate, W; null across a stretch with no periods.</param>
/// <param name="Low">
/// The p10 estimate, W, falling back to the median exactly as
/// <see cref="SolarForecastPeriod.PowerWatts"/> does — so the band collapses onto the line for a
/// period the provider sent no band for, rather than dropping to zero. <see cref="HasBand"/> is what
/// says whether that happened anywhere.
/// </param>
/// <param name="High">The p90 estimate, W, with the same fallback.</param>
/// <param name="WindowStart">Local midnight at the start of today, unix seconds — the chart's left edge.</param>
/// <param name="WindowEnd">Local midnight at the end of tomorrow, unix seconds — its right edge.</param>
/// <param name="Midnight">
/// Local midnight between the two days, unix seconds. Two days on one axis are otherwise one long
/// smear, and this is the rule that separates them.
/// </param>
/// <param name="Now">
/// The moment the page was rendered, unix seconds, so "the rest of today" is readable without
/// arithmetic. Outside the window when neither day is today, and then not drawn.
/// </param>
/// <param name="TimeZoneId">
/// The zone the axis is labelled in, as an IANA id, so the chart and the table agree even when the
/// browser is elsewhere — see <see cref="ChartTimeZone"/>.
/// </param>
/// <param name="HasBand">
/// Whether every plotted period carried a p10 <i>and</i> a p90 of its own. False means the band is
/// the median somewhere, which the page has to say rather than let read as confidence.
/// </param>
internal sealed record ForecastChartSeries(
    long[] Timestamps,
    double?[] Expected,
    double?[] Low,
    double?[] High,
    long WindowStart,
    long WindowEnd,
    long Midnight,
    long Now,
    string TimeZoneId,
    bool HasBand)
{
    /// <summary>Periods this close together are contiguous; anything further apart is a gap.</summary>
    private static readonly TimeSpan Adjacent = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Builds the two days' series from whatever periods are held for them. The periods need not be
    /// contiguous and need not start at midnight — a controller started at 10:44 has nothing for the
    /// morning, and the chart saying so is the point (the page says it in words too).
    /// </summary>
    /// <param name="periods">Every period to plot, in any order; both days together.</param>
    /// <param name="windowStart">Local midnight at the start of today.</param>
    /// <param name="midnight">Local midnight between the two days.</param>
    /// <param name="windowEnd">Local midnight at the end of tomorrow.</param>
    /// <param name="now">The moment being rendered at.</param>
    /// <param name="zone">The site's zone, which the axis is labelled in.</param>
    public static ForecastChartSeries From(
        IReadOnlyList<SolarForecastPeriod> periods,
        DateTimeOffset windowStart,
        DateTimeOffset midnight,
        DateTimeOffset windowEnd,
        DateTimeOffset now,
        TimeZoneInfo zone)
    {
        var ordered = periods.OrderBy(p => p.PeriodEnd).ToList();

        var timestamps = new List<long>(ordered.Count * 2);
        var expected = new List<double?>(timestamps.Capacity);
        var low = new List<double?>(timestamps.Capacity);
        var high = new List<double?>(timestamps.Capacity);

        void Append(long seconds, SolarForecastPeriod? period)
        {
            timestamps.Add(seconds);
            expected.Add(period?.EstimatedPowerWatts);
            low.Add(period?.PowerWatts(ForecastConfidence.P10));
            high.Add(period?.PowerWatts(ForecastConfidence.P90));
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            var period = ordered[i];
            Append(period.PeriodStart.ToUnixTimeSeconds(), period);

            var last = i == ordered.Count - 1;
            if (!last && ordered[i + 1].PeriodStart - period.PeriodEnd <= Adjacent)
            {
                // The next period starts where this one ends, so the step to its value draws this
                // one's full width. Nothing to close.
                continue;
            }

            // A step holds its value until the next point, so the period before a gap -- and the last
            // period held -- would otherwise be drawn as a line of no width at all.
            Append(period.PeriodEnd.ToUnixTimeSeconds(), period);

            if (!last)
            {
                // ...and then nothing, a second later. A stretch nothing is held for is a break in the
                // line, never a line drawn across it: the history is what a refresh actually carried,
                // and a missed refresh is not a forecast of darkness.
                Append(period.PeriodEnd.ToUnixTimeSeconds() + 1, null);
            }
        }

        return new ForecastChartSeries(
            [.. timestamps],
            [.. expected],
            [.. low],
            [.. high],
            windowStart.ToUnixTimeSeconds(),
            windowEnd.ToUnixTimeSeconds(),
            midnight.ToUnixTimeSeconds(),
            now.ToUnixTimeSeconds(),
            ChartTimeZone.IanaId(zone),
            // Every period, not any: one half hour the provider sent no range for is one half hour
            // where the band is the median, and a page that only mentions the all-or-nothing case
            // would let that one read as a sure thing.
            ordered.Count > 0 && ordered.All(p => p is { EstimatedPowerWattsP10: not null, EstimatedPowerWattsP90: not null }));
    }
}
