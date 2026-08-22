using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Strategies;

/// <summary>
/// The day's forecast as it was, not just as it will be.
///
/// <para><b>Why this exists.</b> A forecast provider answers "what is still to come": each refresh
/// returns the periods ahead of it and nothing behind. So the live cache at four in the afternoon can
/// no longer say what the morning was predicted to bring — the very number a finished session has to
/// be read against. This keeps every period a refresh has ever carried, so the whole day can be
/// reconstructed after the fact.</para>
///
/// <para><b>Which prediction is kept.</b> The most recent one for each period. A 10:00 period is
/// re-forecast by every refresh up to 10:00 and then never mentioned again, so what survives here is
/// the last thing said about it before it happened — the closest to a nowcast the provider ever gave,
/// and the fairest thing to judge the roof against.</para>
///
/// <para><b>Thread-safe</b>, unlike the other strategies here, because it is written by the refresh
/// worker and read from the poll loop. The lock guards a few hundred entries and is never held across
/// anything that can block.</para>
/// </summary>
public sealed class SolarForecastHistory
{
    /// <summary>Kept long enough that a session recorded today can still be read tomorrow.</summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(7);

    private readonly TimeSpan _retention;
    private readonly Lock _gate = new();

    // Keyed by period end: that is the provider's own identity for a period, so a refresh replaces the
    // estimate for a window rather than appending a second opinion about it.
    private readonly SortedDictionary<DateTimeOffset, SolarForecastPeriod> _periods = [];

    private DateTimeOffset? _retrievedAt;

    /// <param name="retention">How far back to keep periods. Defaults to <see cref="DefaultRetention"/>.</param>
    public SolarForecastHistory(TimeSpan? retention = null) =>
        _retention = retention is { } value && value > TimeSpan.Zero ? value : DefaultRetention;

    /// <summary>How many periods are retained. Diagnostic.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _periods.Count;
            }
        }
    }

    /// <summary>
    /// Folds a freshly-fetched forecast in: its periods win wherever they overlap what is held, and
    /// anything older than the retention window is dropped.
    /// </summary>
    public void Merge(SolarForecast forecast)
    {
        ArgumentNullException.ThrowIfNull(forecast);

        lock (_gate)
        {
            foreach (var period in forecast.Periods)
            {
                _periods[period.PeriodEnd] = period;
            }

            _retrievedAt = forecast.RetrievedAt;
            Prune(forecast.RetrievedAt - _retention);
        }
    }

    /// <summary>
    /// Everything retained for one local calendar day, elapsed periods included, or <c>null</c> when
    /// nothing is held for it — an honest "we don't know", never an empty curve that would read as a
    /// day the sun didn't come up.
    /// </summary>
    /// <remarks>
    /// A period belongs to the day its <see cref="SolarForecastPeriod.PeriodEnd"/> falls on in
    /// <paramref name="timeZone"/>, matching <see cref="SolarForecast.ForDate"/>.
    /// </remarks>
    public SolarForecast? ForDate(DateOnly localDate, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        SolarForecast snapshot;
        lock (_gate)
        {
            if (_retrievedAt is not { } retrievedAt)
            {
                return null;
            }

            snapshot = new SolarForecast(retrievedAt, [.. _periods.Values]);
        }

        var day = snapshot.ForDate(localDate, timeZone);
        return day.Periods.Count > 0 ? day : null;
    }

    private void Prune(DateTimeOffset cutoff)
    {
        // The keys are period ends and the dictionary is sorted by them, so the expired entries are a
        // prefix -- taken in one pass rather than by scanning the whole day every half hour.
        var expired = _periods.Keys.TakeWhile(end => end < cutoff).ToList();
        foreach (var key in expired)
        {
            _periods.Remove(key);
        }
    }
}
