using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Strategies;

/// <summary>
/// Turns a stream of poll snapshots into fixed-length energy buckets.
///
/// <para>Stateful but framework-free, in the same spirit as <see cref="EnergyIntegrator"/> and
/// <see cref="SurplusMovingAverage"/>: it takes observations in and hands finished
/// <see cref="EnergyInterval"/>s back, and knows nothing about databases, hosting or logging. Every
/// rule that decides what a row means lives here, where it can be tested without a file.</para>
///
/// <para><b>Why not just reuse <see cref="EnergyIntegrator"/>.</b> That one accumulates until somebody
/// resets it, which cannot express a boundary: a snapshot at 10:14:58 followed by one at 10:15:03
/// straddles the quarter hour, and resetting on arrival of the second would post all five seconds to
/// whichever bucket happened to win the race. This splits at the boundary instead — the first two
/// seconds settle the closing bucket, the last three open the next — so the buckets sum to the day
/// exactly, and a poll cadence that shares no common factor with the interval cannot skew them.</para>
///
/// <para><b>Zero-order hold, and a gap that is honestly a gap.</b> Between two snapshots the last
/// power reading is held, which is how the poll loop observes the hardware in the first place. Beyond
/// <c>maxGap</c> the service was asleep or the inverter unreachable, and holding across it would
/// invent energy nobody measured: the open bucket is closed short, the gap is skipped, and a fresh
/// bucket opens at the snapshot that ended it. That is what leaves <see cref="EnergyInterval.Covered"/>
/// below the full interval, and why it is a column rather than an assumption.</para>
///
/// <para>Buckets align to the interval from the UTC epoch, not to local midnight. 15 minutes divides
/// every real UTC offset in use — including the 30- and 45-minute ones — so the two agree anyway, and
/// aligning on UTC keeps a daylight-saving change from producing a 45- or 75-minute bucket.</para>
/// </summary>
public sealed class EnergyIntervalTracker
{
    private readonly TimeSpan _interval;
    private readonly TimeSpan _maxGap;
    private readonly TimeProvider _timeProvider;

    // The window currently filling. _periodEnd is cached rather than recomputed so the boundary
    // arithmetic reads the same value the bucket was opened against.
    private DateTimeOffset _periodStart;
    private DateTimeOffset _periodEnd;

    // When the last snapshot arrived, and the readings held constant since. Null before the first
    // observation and after a Flush, which is what "no bucket is open" means.
    private DateTimeOffset? _lastAt;
    private double _solarWatts;
    private double _gridWatts;
    private double _evWatts;
    private double _batteryWatts;
    private double? _forecastWatts;
    private double _socPercent;

    // The open bucket's totals, in watt-hours. Converted to kWh once, at the point a row is built:
    // integrating in the unit everything else in the codebase uses keeps the arithmetic comparable
    // with the session store's, and the presentation unit is a property of the output, not the sum.
    private double _solarWh;
    private double _forecastSolarWh;
    private double _gridImportWh;
    private double _gridExportWh;
    private double _evWh;
    private double _batteryChargeWh;
    private double _batteryDischargeWh;
    private bool _forecastSeen;

    private double _socStart;
    private double _socEnd;
    private double _socMin;
    private double _socMax;
    private double _socSecondsSum;
    private double _coveredSeconds;
    private int _sampleCount;

    /// <param name="interval">The bucket length. Must divide 24 hours evenly; see <see cref="IsValidInterval"/>.</param>
    /// <param name="maxGap">
    /// How long a silence may be integrated across before it is treated as lost time rather than as a
    /// steady reading. Defaults to five minutes, matching <see cref="EnergyIntegrator"/>.
    /// </param>
    /// <param name="timeProvider">Supplies the local zone stamped on each row. Defaults to the system's.</param>
    public EnergyIntervalTracker(TimeSpan interval, TimeSpan? maxGap = null, TimeProvider? timeProvider = null)
    {
        _interval = IsValidInterval(interval) ? interval : DefaultInterval;
        _maxGap = maxGap is { } gap && gap > TimeSpan.Zero ? gap : TimeSpan.FromMinutes(5);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The interval used when none is configured, or when the configured one is unusable.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Whether an interval can tile a day. Anything that does not divide 24 hours evenly produces a
    /// short bucket at midnight whose figures are not comparable with any other — a 7-minute interval
    /// leaves a 4-minute stub — so such a value is rejected rather than quietly tolerated.
    /// </summary>
    public static bool IsValidInterval(TimeSpan interval) =>
        interval > TimeSpan.Zero
        && interval <= TimeSpan.FromDays(1)
        && TimeSpan.FromDays(1).Ticks % interval.Ticks == 0;

    /// <summary>The bucket length actually in use, after validation.</summary>
    public TimeSpan Interval => _interval;

    /// <summary>The window currently filling, or null when none is open.</summary>
    public DateTimeOffset? OpenPeriodStart => _lastAt is null ? null : _periodStart;

    /// <summary>
    /// Takes one poll snapshot and returns every bucket it completed — normally none, one when the
    /// snapshot crossed a boundary, and more than one only when a gap shorter than <c>maxGap</c>
    /// spanned several (possible when the interval is configured shorter than the gap allowance).
    /// </summary>
    /// <param name="status">The snapshot, as published to every other observer of the poll loop.</param>
    /// <param name="forecastPowerWatts">
    /// What the forecast expected the roof to make at this instant, or null when no forecast covers
    /// it. Taken as a parameter rather than read off <paramref name="status"/>, whose own forecast
    /// field reports 0 for "no forecast" and so cannot be told apart from a genuine night-time zero.
    /// </param>
    public IReadOnlyList<EnergyInterval> Observe(ChargeControlStatus status, double? forecastPowerWatts)
    {
        var now = status.Timestamp;

        if (_lastAt is not { } last)
        {
            // Nothing to integrate from yet: adopt the readings, open the bucket this instant falls
            // in, and start counting at the next snapshot.
            _socPercent = status.BatterySocPercent;
            StartPeriod(AlignDown(now));
            Record(status, forecastPowerWatts, now);
            return [];
        }

        var elapsed = now - last;
        if (elapsed <= TimeSpan.Zero)
        {
            // Out of order or a repeat of the same instant. Refresh the held readings — the newer
            // snapshot is the better description of now — but integrate nothing.
            Record(status, forecastPowerWatts, last);
            return [];
        }

        if (elapsed > _maxGap)
        {
            var interrupted = Close();
            _socPercent = status.BatterySocPercent;
            StartPeriod(AlignDown(now));
            Record(status, forecastPowerWatts, now);
            return interrupted;
        }

        var completed = new List<EnergyInterval>();
        AdvanceTo(now, completed);
        Record(status, forecastPowerWatts, now);
        return completed;
    }

    /// <summary>
    /// Closes the open bucket at <paramref name="now"/> and returns it along with any the elapsed time
    /// completed. Called on shutdown, so a planned restart writes the partial window it collected
    /// rather than dropping it — the store merges the two halves back together when the service
    /// returns.
    /// </summary>
    public IReadOnlyList<EnergyInterval> Flush(DateTimeOffset now)
    {
        if (_lastAt is not { } last)
        {
            return [];
        }

        var completed = new List<EnergyInterval>();
        var elapsed = now - last;
        if (elapsed > TimeSpan.Zero && elapsed <= _maxGap)
        {
            AdvanceTo(now, completed);
        }

        completed.AddRange(Close());
        return completed;
    }

    // Integrates from the last snapshot up to `now`, closing a bucket at every boundary crossed on the
    // way. The held readings are what fills each slice, so the split is exact rather than apportioned.
    private void AdvanceTo(DateTimeOffset now, List<EnergyInterval> completed)
    {
        var cursor = _lastAt!.Value;

        while (now >= _periodEnd)
        {
            var boundary = _periodEnd;
            Accumulate(boundary - cursor);

            if (_coveredSeconds > 0)
            {
                completed.Add(BuildInterval());
            }

            cursor = boundary;

            // The next bucket opens at the SOC that was standing at the boundary, not at whatever the
            // next snapshot reports: consecutive buckets then join up, and SocEnd of one equals
            // SocStart of the next unless time was actually lost between them.
            StartPeriod(boundary);
        }

        Accumulate(now - cursor);
    }

    // Adds `span` of the held readings to the open bucket. Zero-order hold: the last reading stood for
    // the whole span, which is exactly what the poll loop observed.
    private void Accumulate(TimeSpan span)
    {
        if (span <= TimeSpan.Zero)
        {
            return;
        }

        var hours = span.TotalHours;

        _solarWh += Math.Max(0, _solarWatts) * hours;
        _evWh += Math.Max(0, _evWatts) * hours;

        // Positive grid power is import in this model, positive battery power is charging. Each sign
        // goes to its own column; neither is netted against the other (see EnergyInterval).
        if (_gridWatts >= 0)
        {
            _gridImportWh += _gridWatts * hours;
        }
        else
        {
            _gridExportWh += -_gridWatts * hours;
        }

        if (_batteryWatts >= 0)
        {
            _batteryChargeWh += _batteryWatts * hours;
        }
        else
        {
            _batteryDischargeWh += -_batteryWatts * hours;
        }

        // Only counted for the time a forecast was actually in hand: an hour half-covered reports the
        // half, and an hour with none at all reports null rather than zero.
        if (_forecastWatts is { } forecast)
        {
            _forecastSolarWh += Math.Max(0, forecast) * hours;
            _forecastSeen = true;
        }

        // SOC is a level, so it averages over time rather than summing. Weighted by the span the
        // reading stood for, for the reason given on EnergyInterval.SocMeanPercent.
        _socSecondsSum += _socPercent * span.TotalSeconds;
        _coveredSeconds += span.TotalSeconds;
    }

    private void Record(ChargeControlStatus status, double? forecastPowerWatts, DateTimeOffset at)
    {
        _socPercent = status.BatterySocPercent;
        _socEnd = _socPercent;
        _socMin = Math.Min(_socMin, _socPercent);
        _socMax = Math.Max(_socMax, _socPercent);

        _solarWatts = status.SolarPowerWatts;
        _gridWatts = status.GridPowerWatts;
        _evWatts = status.EvChargerPowerWatts;
        _batteryWatts = status.BatteryPowerWatts;
        _forecastWatts = forecastPowerWatts;

        _sampleCount++;
        _lastAt = at;
    }

    // Ends the open bucket without integrating any further, and leaves the tracker with none open.
    private List<EnergyInterval> Close()
    {
        var closed = _coveredSeconds > 0 ? new List<EnergyInterval> { BuildInterval() } : [];
        _lastAt = null;
        return closed;
    }

    private void StartPeriod(DateTimeOffset start)
    {
        _periodStart = start;
        _periodEnd = start + _interval;

        _solarWh = 0;
        _forecastSolarWh = 0;
        _gridImportWh = 0;
        _gridExportWh = 0;
        _evWh = 0;
        _batteryChargeWh = 0;
        _batteryDischargeWh = 0;
        _forecastSeen = false;

        _socStart = _socPercent;
        _socEnd = _socPercent;
        _socMin = _socPercent;
        _socMax = _socPercent;
        _socSecondsSum = 0;
        _coveredSeconds = 0;
        _sampleCount = 0;
    }

    private EnergyInterval BuildInterval()
    {
        var zone = _timeProvider.LocalTimeZone;
        var local = TimeZoneInfo.ConvertTime(_periodStart, zone);

        return new EnergyInterval(
            PeriodStart: _periodStart,
            PeriodEnd: _periodEnd,
            TimeZoneId: zone.Id,
            LocalDate: DateOnly.FromDateTime(local.DateTime),
            SolarKwh: _solarWh / 1000,
            ForecastSolarKwh: _forecastSeen ? _forecastSolarWh / 1000 : null,
            GridImportKwh: _gridImportWh / 1000,
            GridExportKwh: _gridExportWh / 1000,
            EvKwh: _evWh / 1000,
            BatteryChargeKwh: _batteryChargeWh / 1000,
            BatteryDischargeKwh: _batteryDischargeWh / 1000,
            SocStartPercent: _socStart,
            SocEndPercent: _socEnd,
            SocMinPercent: _socMin,
            SocMaxPercent: _socMax,
            SocMeanPercent: _coveredSeconds > 0 ? _socSecondsSum / _coveredSeconds : _socStart,
            Covered: TimeSpan.FromSeconds(_coveredSeconds),
            SampleCount: _sampleCount);
    }

    // Floors an instant to the interval grid measured from the UTC epoch, so every run of the service
    // — and every other installation — agrees on where a bucket begins.
    private DateTimeOffset AlignDown(DateTimeOffset instant)
    {
        var ticks = instant.UtcTicks;
        return new DateTimeOffset(ticks - (ticks % _interval.Ticks), TimeSpan.Zero);
    }
}
