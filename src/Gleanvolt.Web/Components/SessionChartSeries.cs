using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Web.Components;

/// <summary>
/// One recorded session, shaped for the chart on <c>/sessions/{id}</c> (#175) — every meter the
/// samples carry, over the window the session actually ran for.
///
/// <para>The day chart on <c>/energy</c> is the vocabulary this one speaks: signed grid and battery,
/// watts on the left, per cent on the right, the forecast dashed over the solar it was a forecast of.
/// What it must <b>not</b> copy is that chart's arithmetic. There the store keeps kilowatt-hours per
/// bucket and every figure is an energy divided by the part of a window that was observed; here the
/// samples are instantaneous readings, already in watts and already signed the same way, so the
/// conversion is no conversion at all.</para>
///
/// <para>Three things a session can say that a calendar day cannot, and they are why this is a record
/// with tests rather than four <c>Select</c> calls in the markup:</para>
/// <list type="number">
/// <item><b>A stretch nobody sampled is a hole.</b> Samples land at <c>SampleInterval</c> plus
/// whenever something changes, so rows minutes apart mean the service was not running — and a line
/// sloped straight across a restart is a claim about power nobody measured.</item>
/// <item><b>The car's SOC belongs at its capture time.</b> The vehicle feed routinely lags by hours;
/// plotted against the sample's own timestamp it would draw the car at 43 % at 15:00 when 43 % is
/// what it said at noon.</item>
/// <item><b>The hold is drawn, not asserted.</b> Whether the battery hold actually held is judged per
/// run, from what the pack did while it was armed — which is a band on this chart with the battery
/// line inside it.</item>
/// </list>
///
/// <para>No house series: the samples record no house load, and deriving one by netting the other
/// four would publish a figure the store has never checked, off by whatever the meter does not see.
/// The energy page is where the house is answered for.</para>
/// </summary>
/// <param name="Timestamps">Unix seconds for every plotted point, ascending.</param>
/// <param name="Solar">PV production, W.</param>
/// <param name="Forecast">What the forecast expected of the roof, W; null where none was available.</param>
/// <param name="Grid">Import positive, export negative, W — the sample's own sign.</param>
/// <param name="Battery">Charging positive, discharging negative, W — the sample's own sign.</param>
/// <param name="Ev">Measured power at the charger, W.</param>
/// <param name="Soc">Home battery SOC, %.</param>
/// <param name="VehicleSoc">The car's own SOC, %, stepped at the time the <em>car</em> captured it.</param>
/// <param name="HoldStarts">Start of each stretch the battery discharge hold was armed for, unix seconds.</param>
/// <param name="HoldEnds">End of each such stretch. Same length as <paramref name="HoldStarts"/>.</param>
/// <param name="MarkTimes">The moments worth a rule on the chart, unix seconds.</param>
/// <param name="MarkLabels">What happened at each of them. Same length as <paramref name="MarkTimes"/>.</param>
/// <param name="WindowStart">The session's start, unix seconds — the chart's left edge.</param>
/// <param name="WindowEnd">Its end, or now while it is still running.</param>
/// <param name="TimeZoneId">The zone the axis is labelled in, as an IANA id, so the chart agrees with the facts above it.</param>
/// <param name="HasForecast">Whether any sample carried a forecast at all.</param>
/// <param name="HasVehicleSoc">Whether the car ever reported its SOC.</param>
internal sealed record SessionChartSeries(
    long[] Timestamps,
    double?[] Solar,
    double?[] Forecast,
    double?[] Grid,
    double?[] Battery,
    double?[] Ev,
    double?[] Soc,
    double?[] VehicleSoc,
    long[] HoldStarts,
    long[] HoldEnds,
    long[] MarkTimes,
    string[] MarkLabels,
    long WindowStart,
    long WindowEnd,
    string TimeZoneId,
    bool HasForecast,
    bool HasVehicleSoc)
{
    /// <summary>
    /// Samples further apart than this were not a quiet stretch, they were an absence: ten times the
    /// default cadence, and well past the "plus whenever something changes" that fills the gaps in
    /// between. The page cannot see <c>SessionStore:SampleInterval</c> from here, so a constant is the
    /// honest version — generous enough that a slow poll or a missed tick is still one line.
    /// </summary>
    private static readonly TimeSpan MaxSampleGap = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The kinds that get a rule. Not every event: <see cref="ChargingSessionEventKind.TargetCurrentChanged"/>
    /// fires whenever the setpoint moves, which on a sunny afternoon is most cycles, and a chart ruled
    /// once a minute is a hatched chart. The hold's own two kinds are left out because the band
    /// already draws them, and the session's start and end because they are the chart's own edges.
    /// What remains is the set that answers "why did it drop at 14:20".
    /// </summary>
    private static readonly HashSet<ChargingSessionEventKind> MarkedKinds =
    [
        ChargingSessionEventKind.ModeChanged,
        ChargingSessionEventKind.ChargingStarted,
        ChargingSessionEventKind.ChargingPaused,
        ChargingSessionEventKind.PlanUnusable,
        ChargingSessionEventKind.PlanUsable,
    ];

    /// <summary>
    /// Builds the session's series from the rows it actually has. The samples need not be evenly
    /// spaced, and they need not cover the session: a charge the service was restarted in the middle
    /// of has a hole, and drawing it is the point.
    /// </summary>
    public static SessionChartSeries From(ChargingSessionDocument document, TimeZoneInfo zone, DateTimeOffset now)
    {
        var samples = document.Samples.OrderBy(s => s.Timestamp).ToList();
        var captures = VehicleCaptures(samples);

        var timestamps = new List<long>(samples.Count + 8);
        var solar = new List<double?>(timestamps.Capacity);
        var forecast = new List<double?>(timestamps.Capacity);
        var grid = new List<double?>(timestamps.Capacity);
        var battery = new List<double?>(timestamps.Capacity);
        var ev = new List<double?>(timestamps.Capacity);
        var soc = new List<double?>(timestamps.Capacity);
        var vehicleSoc = new List<double?>(timestamps.Capacity);

        var capture = 0;

        void Append(long seconds, ChargingSessionSample? sample)
        {
            timestamps.Add(seconds);
            solar.Add(sample?.SolarPowerWatts);
            forecast.Add(sample?.ForecastPowerWatts);
            grid.Add(sample?.GridPowerWatts);
            battery.Add(sample?.BatteryPowerWatts);
            ev.Add(sample?.EvChargerPowerWatts);
            soc.Add(sample?.BatterySocPercent);

            // The car's line is stepped over the same x values, so it holds the last reading the car
            // had captured by this instant -- not the one the sample happened to be carrying, which is
            // the same reading for however many hours the feed stayed quiet. Null before the first
            // capture: a car that has not spoken yet has not said 0 %.
            //
            // It is also the one series that carries on across a hole, deliberately: the meters have
            // nothing to say about a stretch nobody sampled, but the car timestamps its own readings,
            // and one taken before an outage is still where the car was during it.
            while (capture < captures.Count && captures[capture].At.ToUnixTimeSeconds() <= seconds)
            {
                capture++;
            }

            vehicleSoc.Add(capture == 0 ? null : captures[capture - 1].Percent);
        }

        for (var i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            Append(sample.Timestamp.ToUnixTimeSeconds(), sample);

            if (i < samples.Count - 1 && samples[i + 1].Timestamp - sample.Timestamp > MaxSampleGap)
            {
                // A null a second later, and then nothing until the samples resume. uPlot draws that
                // as a hole, which is what it was: the readings on either side are both true, and the
                // straight line between them would be a measurement nobody took.
                Append(sample.Timestamp.ToUnixTimeSeconds() + 1, null);
            }
        }

        var (holdStarts, holdEnds) = HoldBands(samples);
        var (markTimes, markLabels) = Marks(document.Events);

        // While the session is still open the window runs to now, so a live chart grows rather than
        // redrawing its axis around each new sample. A finished one stops where it stopped -- and
        // never before its last sample, which a clock skewed against the store would otherwise cut off.
        var end = document.Session.EndedAt ?? now;
        if (samples.Count > 0 && samples[^1].Timestamp > end)
        {
            end = samples[^1].Timestamp;
        }

        return new SessionChartSeries(
            [.. timestamps],
            [.. solar],
            [.. forecast],
            [.. grid],
            [.. battery],
            [.. ev],
            [.. soc],
            [.. vehicleSoc],
            holdStarts,
            holdEnds,
            markTimes,
            markLabels,
            document.Session.StartedAt.ToUnixTimeSeconds(),
            end.ToUnixTimeSeconds(),
            ChartTimeZone.IanaId(zone),
            // Null rather than zero everywhere, so the legend does not offer a "Forecast" line that is
            // nothing but a gap: no forecast for this session is a different fact from a flat zero.
            samples.Any(s => s.ForecastPowerWatts is not null),
            captures.Count > 0);
    }

    /// <summary>
    /// The distinct readings the car produced, in the order the <em>car</em> produced them, keyed on
    /// its own capture time. Every sample repeats the last one it heard, so a three-hour charge with
    /// two reports is two points and not four hundred — and a report that arrives late still lands
    /// where it was measured, which is usually before the sample that first carried it.
    /// </summary>
    private static List<(DateTimeOffset At, double Percent)> VehicleCaptures(IReadOnlyList<ChargingSessionSample> samples)
    {
        var captures = new List<(DateTimeOffset At, double Percent)>();

        foreach (var sample in samples)
        {
            if (sample is not { VehicleSocPercent: { } percent, VehicleSocCapturedAt: { } at })
            {
                continue;
            }

            if (captures.Count > 0 && captures[^1].At >= at)
            {
                continue;
            }

            captures.Add((at, percent));
        }

        return captures;
    }

    /// <summary>
    /// The stretches the discharge hold was armed for, contiguous samples merged into one band each.
    /// Broken at a recording gap on purpose: the hold may well have stayed armed across a restart, but
    /// nothing observed it doing so, and a band is a claim about what the pack was doing underneath it.
    /// </summary>
    private static (long[] Starts, long[] Ends) HoldBands(IReadOnlyList<ChargingSessionSample> samples)
    {
        var starts = new List<long>();
        var ends = new List<long>();
        DateTimeOffset? open = null;
        DateTimeOffset previous = default;

        void Close()
        {
            if (open is null)
            {
                return;
            }

            starts.Add(open.Value.ToUnixTimeSeconds());
            ends.Add(previous.ToUnixTimeSeconds());
            open = null;
        }

        foreach (var sample in samples)
        {
            if (!sample.BatteryHoldActive)
            {
                Close();
                continue;
            }

            if (open is not null && sample.Timestamp - previous > MaxSampleGap)
            {
                Close();
            }

            open ??= sample.Timestamp;
            previous = sample.Timestamp;
        }

        Close();

        return ([.. starts], [.. ends]);
    }

    /// <summary>
    /// The events that get a rule, with the words that go beside the chart. Kept in one place so the
    /// list under the picture and the marks on it can never disagree about what is marked.
    /// </summary>
    private static (long[] Times, string[] Labels) Marks(IReadOnlyList<ChargingSessionEvent> events)
    {
        var marked = events
            .Where(e => MarkedKinds.Contains(e.Kind))
            .OrderBy(e => e.Timestamp)
            .ToList();

        return (
            [.. marked.Select(e => e.Timestamp.ToUnixTimeSeconds())],
            [.. marked.Select(e => string.IsNullOrWhiteSpace(e.Detail) ? Describe(e.Kind) : e.Detail)]);
    }

    /// <summary>The kind in words, for the events whose detail line is empty.</summary>
    private static string Describe(ChargingSessionEventKind kind) => kind switch
    {
        ChargingSessionEventKind.ModeChanged => "Mode changed",
        ChargingSessionEventKind.ChargingStarted => "Charging started",
        ChargingSessionEventKind.ChargingPaused => "Charging paused",
        ChargingSessionEventKind.PlanUnusable => "Plan unusable",
        ChargingSessionEventKind.PlanUsable => "Plan usable again",
        _ => kind.ToString(),
    };
}
