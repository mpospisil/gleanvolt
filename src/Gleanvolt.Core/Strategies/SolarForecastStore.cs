using System.Collections.Frozen;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Strategies;

/// <summary>
/// The solar forecast the controller holds (issue #387), whichever source it came from. Every reader of
/// <see cref="ISolarForecastService"/> is served from here, and the refresh worker reads
/// <see cref="ISolarForecastRefresh"/> from here to pace its requests; a source only
/// <see cref="Publish"/>es.
///
/// <para><b>One immutable state, replaced whole.</b> The forecast, its origin, the retained history and
/// the day summaries taken from it are one value behind one volatile field. A publish builds the next
/// value and swaps it in, so readers — the poll loop, the planner, every dashboard render — never take a
/// lock and never see a forecast from one publish beside summaries from another. Writers serialise on a
/// lock of their own, which no reader touches.</para>
///
/// <para><b>The history outlives the forecast.</b> <see cref="Forget"/> drops the forecast, so charge
/// control stops planning on one that will not be refreshed, but keeps what was expected for the days
/// behind: that is still what was expected, and a finished session is read against it.</para>
/// </summary>
public sealed class SolarForecastStore : ISolarForecastService, ISolarForecastRefresh
{
    private readonly TimeProvider _timeProvider;

    // Written only under _write. A provider answers only what is still to come, so this keeps what the
    // forecast no longer can: the periods that have already gone by. See SolarForecastHistory.
    private readonly SolarForecastHistory _history = new();
    private readonly Lock _write = new();

    private volatile State _state = State.Empty;

    public SolarForecastStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised after every <see cref="Publish"/> and <see cref="Forget"/>, outside any lock.</summary>
    public event Action? Changed;

    /// <summary>The forecast held, where it came from and when it arrived; null before the first publish and after <see cref="Forget"/>.</summary>
    public SolarForecastSnapshot? Current => _state.Current;

    /// <summary>How many periods the history retains. Diagnostic, for the refresh log line.</summary>
    public int RetainedPeriodCount => _state.RetainedCount;

    /// <summary>
    /// Replaces the forecast with <paramref name="forecast"/>, folds it into the history and takes the
    /// day summaries from that, then publishes all of it as one state.
    /// </summary>
    public void Publish(SolarForecast forecast, SolarForecastOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(forecast);
        ArgumentNullException.ThrowIfNull(origin);

        lock (_write)
        {
            _history.Merge(forecast);
            var zone = _timeProvider.LocalTimeZone;

            _state = new State(
                new SolarForecastSnapshot(forecast, origin, _timeProvider.GetUtcNow()),
                _history.Retained(),
                _history.DailySummaries(zone),
                _history.Count);
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Drops the held forecast, for when nothing will refresh it any more: charge control then plans as
    /// it does without one. The history of what was expected stays.
    /// </summary>
    public void Forget()
    {
        lock (_write)
        {
            _state = _state with { Current = null };
        }

        Changed?.Invoke();
    }

    public SolarForecast? GetForecastForToday()
    {
        if (_state.Current is not { } current)
        {
            return null;
        }

        var localNow = _timeProvider.GetLocalNow();
        return current.Forecast.ForDate(DateOnly.FromDateTime(localNow.DateTime), _timeProvider.LocalTimeZone);
    }

    public SolarForecast? GetForecast(DateTimeOffset from, DateTimeOffset to) =>
        _state.Current?.Forecast.ForPeriod(from, to);

    public SolarForecast? GetDayForecast(DateOnly localDate)
    {
        if (_state.Retained is not { } retained)
        {
            return null;
        }

        var day = retained.ForDate(localDate, _timeProvider.LocalTimeZone);
        return day.Periods.Count > 0 ? day : null;
    }

    /// <summary>A lookup into the summaries taken at the last publish; no periods are summed here.</summary>
    public double? GetDayEnergyWattHours(DateOnly localDate) => GetDaySummary(localDate)?.ExpectedWh;

    /// <summary>A lookup into the summaries taken at the last publish; no periods are summed here.</summary>
    public SolarDayForecastSummary? GetDaySummary(DateOnly localDate) =>
        _state.DaySummaries.TryGetValue(localDate, out var day) ? day : null;

    public DateTimeOffset? NextDaylightStart(DateTimeOffset after, double thresholdWatts) =>
        _state.Current?.Forecast.NextPeriodStartAbove(after, thresholdWatts);

    public double? ExpectedPowerWattsNow(DateTimeOffset instant) =>
        _state.Current?.Forecast.ExpectedPowerWattsAt(instant);

    private sealed record State(
        SolarForecastSnapshot? Current,
        SolarForecast? Retained,
        IReadOnlyDictionary<DateOnly, SolarDayForecastSummary> DaySummaries,
        int RetainedCount)
    {
        public static State Empty { get; } =
            new(null, null, FrozenDictionary<DateOnly, SolarDayForecastSummary>.Empty, 0);
    }
}
