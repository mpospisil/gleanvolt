using Gleanvolt.Core.Models;

namespace Gleanvolt.Web.Components;

/// <summary>
/// One recorded day, shaped for the chart on <c>/energy</c> (#173) — the same rows the table beneath
/// it prints, turned into the series uPlot wants.
///
/// <para>A record rather than arithmetic in the markup, because the two things that make this
/// honest are the two things easiest to get quietly wrong: <b>dividing by the observed part of a
/// window rather than by its nominal length</b>, and <b>refusing to draw across a stretch nobody
/// watched</b>. Both are tested here rather than eyeballed in a picture.</para>
///
/// <para>Everything on the power axis is in <b>watts</b>: the store keeps kilowatt-hours per bucket
/// because it is the analytics surface, but a chart against time is read as power, and the portal
/// this one is modelled on says W too.</para>
///
/// <para>The pairs the store deliberately keeps apart — import and export, charge and discharge —
/// are folded into one signed series each. That is the one place the netting is an improvement
/// rather than a loss: up and down carry on the chart exactly what the two column names carry in the
/// table, and the table is still right there for the quarter hour that did both.</para>
/// </summary>
/// <param name="Timestamps">Unix seconds for every plotted point, ascending.</param>
/// <param name="Solar">PV production, W.</param>
/// <param name="Forecast">What the forecast expected of the roof, W; null where none covered the window.</param>
/// <param name="Grid">Import positive, export negative, W.</param>
/// <param name="Battery">Charging positive, discharging negative, W.</param>
/// <param name="Ev">Delivered to the car at the charger, W.</param>
/// <param name="House">Everything the house drew, the car included, W.</param>
/// <param name="Soc">Home battery SOC, %.</param>
/// <param name="PartialStarts">Start of each stretch of partly observed windows, unix seconds.</param>
/// <param name="PartialEnds">End of each such stretch, unix seconds. Same length as <paramref name="PartialStarts"/>.</param>
/// <param name="DayStart">Local midnight, unix seconds — the chart's left edge whatever the data does.</param>
/// <param name="DayEnd">The next local midnight, unix seconds.</param>
/// <param name="TimeZoneId">
/// The zone the axis is labelled in, as an IANA id, so the chart and the table agree even when the
/// browser is elsewhere. IANA specifically: the browser reads it through <c>Intl</c>, which has never
/// heard of "Central Europe Standard Time".
/// </param>
/// <param name="HasForecast">Whether any window of the day had a forecast at all.</param>
internal sealed record EnergyChartSeries(
    long[] Timestamps,
    double?[] Solar,
    double?[] Forecast,
    double?[] Grid,
    double?[] Battery,
    double?[] Ev,
    double?[] House,
    double?[] Soc,
    long[] PartialStarts,
    long[] PartialEnds,
    long DayStart,
    long DayEnd,
    string TimeZoneId,
    bool HasForecast)
{
    /// <summary>
    /// Below this fraction of a window observed, the power figures are not drawn at all. A bucket the
    /// service saw a few seconds of carries a few seconds of energy, and dividing that by a few
    /// seconds turns measurement noise into a spike taller than the roof can produce.
    /// </summary>
    private const double MinimumCoverage = 0.1;

    /// <summary>
    /// The threshold the table marks a row partial at, kept the same here on purpose: the marker band
    /// under the chart and the italic rows below it must never disagree about which windows were short.
    /// </summary>
    private const double FullCoverage = 0.995;

    /// <summary>Buckets this close together are contiguous; anything further apart is a gap.</summary>
    private static readonly TimeSpan Adjacent = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Builds the day's series from the rows it actually has. The rows need not be contiguous — a day
    /// the service was restarted in the middle of has a hole, and drawing it is the whole point.
    /// </summary>
    public static EnergyChartSeries From(
        IReadOnlyList<EnergyInterval> intervals,
        DateTimeOffset dayStart,
        DateTimeOffset dayEnd,
        TimeZoneInfo zone)
    {
        var ordered = intervals.OrderBy(i => i.PeriodStart).ToList();

        var timestamps = new List<long>(ordered.Count + 2);
        var solar = new List<double?>(timestamps.Capacity);
        var forecast = new List<double?>(timestamps.Capacity);
        var grid = new List<double?>(timestamps.Capacity);
        var battery = new List<double?>(timestamps.Capacity);
        var ev = new List<double?>(timestamps.Capacity);
        var house = new List<double?>(timestamps.Capacity);
        var soc = new List<double?>(timestamps.Capacity);

        void Append(long seconds, EnergyInterval? interval)
        {
            timestamps.Add(seconds);
            solar.Add(interval is null ? null : Watts(interval.SolarKwh, interval));
            forecast.Add(interval?.ForecastSolarKwh is { } expected ? Watts(expected, interval) : null);
            grid.Add(interval is null ? null : Watts(interval.GridImportKwh - interval.GridExportKwh, interval));
            battery.Add(interval is null ? null : Watts(interval.BatteryChargeKwh - interval.BatteryDischargeKwh, interval));
            ev.Add(interval is null ? null : Watts(interval.EvKwh, interval));
            house.Add(interval is null ? null : Watts(interval.HouseLoadKwh, interval));

            // The time-weighted mean, not the two endpoints the table shows: a bucket is a stretch of
            // time here, and it is drawn as one. Kept even for a window too thin to give a power
            // figure -- a reading that stood for ten seconds is still where the pack was.
            soc.Add(interval?.SocMeanPercent);
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            var interval = ordered[i];
            Append(interval.PeriodStart.ToUnixTimeSeconds(), interval);

            var last = i == ordered.Count - 1;
            if (!last && ordered[i + 1].PeriodStart - interval.PeriodEnd <= Adjacent)
            {
                // The next bucket starts where this one ends, so the step to its value draws this
                // one's full width. Nothing to close.
                continue;
            }

            // A step holds its value until the next point, so the bucket before a hole -- and the last
            // bucket of the day -- would otherwise be drawn as a line of no width at all.
            Append(interval.PeriodEnd.ToUnixTimeSeconds(), interval);

            if (!last)
            {
                // ...and then nothing, a second later. A stretch the service did not see is a gap in
                // the line, never a line drawn straight across it: a restart at 09:07 is not the sun
                // going out for seven minutes, and it must not look like one either.
                Append(interval.PeriodEnd.ToUnixTimeSeconds() + 1, null);
            }
        }

        var (partialStarts, partialEnds) = PartialBands(ordered);

        return new EnergyChartSeries(
            [.. timestamps],
            [.. solar],
            [.. forecast],
            [.. grid],
            [.. battery],
            [.. ev],
            [.. house],
            [.. soc],
            partialStarts,
            partialEnds,
            dayStart.ToUnixTimeSeconds(),
            dayEnd.ToUnixTimeSeconds(),
            IanaId(zone),
            // Null rather than zero everywhere, so the legend does not offer a "Forecast" line that is
            // nothing but a gap: no forecast covered the day is a different fact from a flat zero.
            ordered.Any(i => i.ForecastSolarKwh is not null));
    }

    /// <summary>
    /// The zone as the browser can read it. On Linux — the Pi, the container — <c>Id</c> is already
    /// IANA and this is a no-op; on Windows it is something like "Central Europe Standard Time",
    /// which <c>Intl</c> rejects outright. Where no mapping exists the id is passed through and the
    /// chart falls back to the browser's own zone rather than refusing to draw.
    /// </summary>
    private static string IanaId(TimeZoneInfo zone) =>
        zone.HasIanaId || !TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? zone.Id : iana;

    /// <summary>
    /// Average power over the part of the window that was actually observed. Dividing by the nominal
    /// quarter hour instead would draw every restart as a dip the sun never took.
    /// </summary>
    private static double? Watts(double kwh, EnergyInterval interval) =>
        interval.Coverage < MinimumCoverage ? null : kwh / interval.Covered.TotalHours * 1000;

    /// <summary>
    /// The stretches of the day that were only partly observed, contiguous windows merged into one
    /// band each — ninety-six separate marks under a chart of a day the service missed entirely would
    /// be a hatched axis, which says less than one band saying "here".
    /// </summary>
    private static (long[] Starts, long[] Ends) PartialBands(IReadOnlyList<EnergyInterval> ordered)
    {
        var starts = new List<long>();
        var ends = new List<long>();
        DateTimeOffset? open = null;
        DateTimeOffset previousEnd = default;

        foreach (var interval in ordered)
        {
            if (interval.Coverage >= FullCoverage)
            {
                continue;
            }

            if (open is not null && interval.PeriodStart - previousEnd > Adjacent)
            {
                starts.Add(open.Value.ToUnixTimeSeconds());
                ends.Add(previousEnd.ToUnixTimeSeconds());
                open = null;
            }

            open ??= interval.PeriodStart;
            previousEnd = interval.PeriodEnd;
        }

        if (open is not null)
        {
            starts.Add(open.Value.ToUnixTimeSeconds());
            ends.Add(previousEnd.ToUnixTimeSeconds());
        }

        return ([.. starts], [.. ends]);
    }
}
