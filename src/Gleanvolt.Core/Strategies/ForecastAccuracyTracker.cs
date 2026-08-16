using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Strategies;

/// <summary>
/// Continuously checks the solar forecast against what the panels actually produced, and turns the
/// discrepancy into something the day plan can act on.
///
/// <para>Three outputs:</para>
/// <list type="number">
/// <item><description><see cref="BiasFactor"/> — today's realised <c>actual ÷ forecast</c>, clamped and
/// applied to the <em>remaining</em> forecast. The clamp is asymmetric on purpose: a day that is
/// under-producing scales the rest of the day down (raising the SOC floor and throttling the car
/// early, the conservative direction), while a sunny morning is not allowed to talk the planner into
/// over-committing the afternoon.</description></item>
/// <item><description>A <see cref="ForecastPeriodComparison"/> each time a forecast period closes, for
/// the reconciliation log.</description></item>
/// <item><description><see cref="TrustBreached"/> — a forecast this wrong is not a basis for lending
/// the battery out, so the caller falls back to live-solar behaviour.</description></item>
/// </list>
///
/// <para>Comparisons are made against the provider's <b>median</b> band regardless of which band the
/// plan is built on: the median is the unbiased expectation, so it is the only fair yardstick for
/// "was the forecast right?". Stateful, reset at local midnight, framework-free.</para>
/// </summary>
public sealed class ForecastAccuracyTracker
{
    // Periods this dark contribute nothing but noise to a ratio, so they neither count as samples nor
    // enter the cumulative totals. Roughly "the sun is up".
    private const double DaylightPeriodThresholdWh = 50;

    private readonly int _minPeriods;
    private readonly double _clampMin;
    private readonly double _clampMax;
    private readonly double _trustMin;
    private readonly double _trustMax;
    private readonly int _trustBreachPeriods;

    private DateOnly _day;
    private DateTimeOffset? _lastSample;
    private double _lastActualWatts;
    private double _lastForecastWatts;

    private DateTimeOffset? _currentPeriodEnd;
    private DateTimeOffset _currentPeriodStart;
    private double _periodActualWh;
    private double _periodForecastWh;

    private int _daylightPeriods;
    private int _consecutiveBreaches;
    private ForecastPeriodComparison? _pendingComparison;

    /// <param name="minPeriods">Closed daylight periods required before the bias is trusted at all.</param>
    /// <param name="clampMin">Lower clamp on the bias (protects against a dead-morning over-correction).</param>
    /// <param name="clampMax">Upper clamp on the bias (stops optimism compounding).</param>
    /// <param name="trustMin">Below this cumulative bias the forecast is considered untrustworthy.</param>
    /// <param name="trustMax">Above this cumulative bias the forecast is considered untrustworthy.</param>
    /// <param name="trustBreachPeriods">Consecutive out-of-band periods before trust is withdrawn.</param>
    public ForecastAccuracyTracker(
        int minPeriods = 4,
        double clampMin = 0.5,
        double clampMax = 1.2,
        double trustMin = 0.6,
        double trustMax = 1.4,
        int trustBreachPeriods = 3)
    {
        _minPeriods = Math.Max(1, minPeriods);
        _clampMin = clampMin;
        _clampMax = clampMax;
        _trustMin = trustMin;
        _trustMax = trustMax;
        _trustBreachPeriods = Math.Max(1, trustBreachPeriods);
    }

    /// <summary>Actual PV energy produced so far today, in watt-hours.</summary>
    public double ActualEnergyWhToday { get; private set; }

    /// <summary>Forecast PV energy over the same elapsed window, in watt-hours.</summary>
    public double ForecastEnergyWhToday { get; private set; }

    /// <summary>
    /// The factor to scale the remaining forecast by. Exactly 1.0 until <c>minPeriods</c> daylight
    /// periods have closed — an hour of cloud at dawn must not rewrite the whole day.
    /// </summary>
    public double BiasFactor => _daylightPeriods >= _minPeriods && ForecastEnergyWhToday > DaylightPeriodThresholdWh
        ? Math.Clamp(ActualEnergyWhToday / ForecastEnergyWhToday, _clampMin, _clampMax)
        : 1.0;

    /// <summary>
    /// Whether the forecast has been out of the trust band for long enough that the plan should not be
    /// believed. The caller falls back to live-solar behaviour for the rest of the day.
    /// </summary>
    public bool TrustBreached => _consecutiveBreaches >= _trustBreachPeriods;

    /// <summary>
    /// Feeds one telemetry sample. <paramref name="forecast"/> is today's forecast; when it doesn't
    /// cover the instant (before the first fetch, or past the horizon) the sample is ignored rather
    /// than counted as a forecast of zero.
    /// </summary>
    public void Add(DateTimeOffset timestamp, double actualPvWatts, SolarForecast? forecast)
    {
        var day = DateOnly.FromDateTime(timestamp.LocalDateTime);
        if (day != _day)
        {
            Reset();
            _day = day;
        }

        var period = forecast?.Periods.FirstOrDefault(p => p.Covers(timestamp));
        if (period is null)
        {
            // Nothing to compare against; hold the sample so integration resumes cleanly next time.
            _lastSample = null;
            return;
        }

        if (_currentPeriodEnd is { } openEnd && period.PeriodEnd != openEnd)
        {
            ClosePeriod();
        }

        if (_currentPeriodEnd is null)
        {
            _currentPeriodEnd = period.PeriodEnd;
            _currentPeriodStart = period.PeriodStart;
        }

        if (_lastSample is { } previous)
        {
            var elapsed = (timestamp - previous).TotalHours;
            if (elapsed > 0 && elapsed <= 0.25)
            {
                _periodActualWh += _lastActualWatts * elapsed;
                _periodForecastWh += _lastForecastWatts * elapsed;
            }
        }

        _lastSample = timestamp;
        _lastActualWatts = Math.Max(0, actualPvWatts);
        _lastForecastWatts = Math.Max(0, period.EstimatedPowerWatts);
    }

    /// <summary>
    /// Takes the most recently closed period comparison, if one is waiting, and clears it — so the
    /// caller logs each period exactly once. Returns null when nothing has closed since the last call.
    /// </summary>
    public ForecastPeriodComparison? TakeClosedPeriod()
    {
        var comparison = _pendingComparison;
        _pendingComparison = null;
        return comparison;
    }

    /// <summary>Clears all of today's accumulated state (new day, or a deliberate restart of tracking).</summary>
    public void Reset()
    {
        ActualEnergyWhToday = 0;
        ForecastEnergyWhToday = 0;
        _lastSample = null;
        _lastActualWatts = 0;
        _lastForecastWatts = 0;
        _currentPeriodEnd = null;
        _periodActualWh = 0;
        _periodForecastWh = 0;
        _daylightPeriods = 0;
        _consecutiveBreaches = 0;
        _pendingComparison = null;
    }

    private void ClosePeriod()
    {
        // Night periods carry no information about forecast quality, so they are dropped entirely
        // rather than dragging the ratio around near zero.
        if (_periodForecastWh >= DaylightPeriodThresholdWh)
        {
            ActualEnergyWhToday += _periodActualWh;
            ForecastEnergyWhToday += _periodForecastWh;
            _daylightPeriods++;

            var bias = BiasFactor;
            _consecutiveBreaches = _daylightPeriods >= _minPeriods && (bias < _trustMin || bias > _trustMax)
                ? _consecutiveBreaches + 1
                : 0;

            _pendingComparison = new ForecastPeriodComparison(
                _currentPeriodStart,
                _currentPeriodEnd!.Value,
                _periodForecastWh,
                _periodActualWh,
                ForecastEnergyWhToday,
                ActualEnergyWhToday,
                bias);
        }

        _currentPeriodEnd = null;
        _periodActualWh = 0;
        _periodForecastWh = 0;
    }
}
