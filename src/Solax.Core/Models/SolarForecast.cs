namespace Solax.Core.Models;

/// <summary>
/// A snapshot of a solar-energy forecast: an ordered set of <see cref="SolarForecastPeriod"/>
/// estimates, plus pure query helpers for slicing them by day/window and aggregating them.
/// This type carries no I/O -- it is produced by a forecast service after fetching, and is fully
/// unit-testable on its own.
/// </summary>
public sealed class SolarForecast
{
    public SolarForecast(DateTimeOffset retrievedAt, IReadOnlyList<SolarForecastPeriod> periods)
    {
        RetrievedAt = retrievedAt;
        Periods = periods;
    }

    /// <summary>When this forecast was fetched from the provider.</summary>
    public DateTimeOffset RetrievedAt { get; }

    /// <summary>The forecast periods, ordered by <see cref="SolarForecastPeriod.PeriodEnd"/>.</summary>
    public IReadOnlyList<SolarForecastPeriod> Periods { get; }

    /// <summary>Total expected energy across all contained periods, in watt-hours.</summary>
    public double ExpectedEnergyWattHours => Periods.Sum(p => p.EnergyWattHours);

    /// <summary>
    /// Total expected energy at the given confidence band, in watt-hours. Periods whose provider
    /// omitted that band fall back to the median, so a partial response can't read as darkness.
    /// </summary>
    public double EnergyWattHoursAt(Enums.ForecastConfidence confidence) =>
        Periods.Sum(p => p.EnergyWattHoursAt(confidence));

    /// <summary>
    /// The start of the next period after <paramref name="after"/> whose median estimate exceeds
    /// <paramref name="thresholdWatts"/> — i.e. when the sun next comes up far enough to be worth
    /// refreshing for. Null when no such period is within the forecast horizon.
    /// </summary>
    public DateTimeOffset? NextPeriodStartAbove(DateTimeOffset after, double thresholdWatts)
    {
        foreach (var period in Periods.OrderBy(p => p.PeriodEnd))
        {
            if (period.PeriodEnd > after && period.EstimatedPowerWatts > thresholdWatts)
            {
                return period.PeriodStart > after ? period.PeriodStart : after;
            }
        }

        return null;
    }

    /// <summary>The highest expected power across all contained periods, in watts (0 when empty).</summary>
    public double PeakPowerWatts => Periods.Count == 0 ? 0 : Periods.Max(p => p.EstimatedPowerWatts);

    /// <summary>
    /// The expected PV power at a given instant, in watts, taken from the period whose window
    /// covers it -- or <c>null</c> if no period covers the instant (e.g. it falls outside the
    /// forecast horizon).
    /// </summary>
    public double? ExpectedPowerWattsAt(DateTimeOffset instant)
    {
        foreach (var period in Periods)
        {
            if (period.Covers(instant))
            {
                return period.EstimatedPowerWatts;
            }
        }

        return null;
    }

    /// <summary>
    /// The periods falling on the given calendar date, evaluated in <paramref name="timeZone"/>.
    /// A period is attributed to the date of its <see cref="SolarForecastPeriod.PeriodEnd"/> in
    /// that zone; overnight periods (which carry ~0W anyway) may land on the adjacent day.
    /// </summary>
    public SolarForecast ForDate(DateOnly date, TimeZoneInfo timeZone)
    {
        var periods = Periods
            .Where(p => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(p.PeriodEnd, timeZone).DateTime) == date)
            .ToList();

        return new SolarForecast(RetrievedAt, periods);
    }

    /// <summary>
    /// The periods whose windows overlap the half-open range <c>(from, to]</c>. Intended for
    /// intra-day windows; the range is not required to align with period boundaries.
    /// </summary>
    public SolarForecast ForPeriod(DateTimeOffset from, DateTimeOffset to)
    {
        if (to < from)
        {
            throw new ArgumentException($"'{nameof(to)}' ({to:o}) must not be earlier than '{nameof(from)}' ({from:o}).", nameof(to));
        }

        var periods = Periods
            .Where(p => p.PeriodEnd > from && p.PeriodStart < to)
            .ToList();

        return new SolarForecast(RetrievedAt, periods);
    }
}
