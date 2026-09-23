using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Api.Contracts;

/// <summary>
/// The solar forecast for the site, as the controller is currently working from it.
///
/// <para>Cached, not fetched per call: the same forecast the poll loop is deciding on, refreshed on
/// its own schedule. <c>retrievedAt</c> says how current it is.</para>
///
/// <para><b>One source, so the response agrees with itself</b> (issue #221). Every figure here comes
/// from the same two local days of retained forecast, which means <c>sum(periods on a date)</c> is
/// <c>days[date].expectedWh</c> and <c>peakPowerWatts</c> is the peak of the periods returned. It did
/// not used to be: the periods came from the live cache, which holds only what is still to come, while
/// the day totals came from the retained history, which holds the whole day — so a caller summing the
/// periods for today got a smaller number than the response's own total beside it.</para>
/// </summary>
/// <param name="RetrievedAt">When the forecast behind this response was fetched from the provider.</param>
/// <param name="Days">
/// One entry per local day covered, today first — the day's own figures, and whether the whole of it is
/// held. This is where the p10 and p90 <i>day</i> totals live; the top-level <c>today…</c> fields carry
/// only the median.
/// </param>
/// <param name="TodayExpectedWh">Expected production over the whole of today, local.</param>
/// <param name="TodayRemainingWh">
/// Expected production from now to the end of today. Deliberately the one figure that is not a whole
/// day: "what is left" is a different question from "what the day was expected to bring".
/// </param>
/// <param name="TomorrowExpectedWh">Expected production over the whole of tomorrow.</param>
/// <param name="PeakPowerWatts">
/// The highest power any period in the returned window expects — the strongest half hour of the two
/// days, which no longer shrinks as the day goes on.
/// </param>
/// <param name="Periods">
/// The periods themselves, oldest first, covering the whole of today and tomorrow — <b>elapsed periods
/// included</b>. Each carries the median estimate and, when the provider supplies them, the p10 and p90
/// bands. Today's can begin after midnight; see <see cref="ForecastDayResponse.Complete"/>.
/// </param>
/// <param name="Weather">The current conditions, or null when no weather provider is configured or the fetch failed.</param>
public sealed record ForecastResponse(
    DateTimeOffset? RetrievedAt,
    IReadOnlyList<ForecastDayResponse> Days,
    double? TodayExpectedWh,
    double? TodayRemainingWh,
    double? TomorrowExpectedWh,
    double PeakPowerWatts,
    IReadOnlyList<ForecastPeriodResponse> Periods,
    WeatherResponse? Weather);

/// <summary>
/// One local calendar day of forecast, reduced to the figures a caller would otherwise have to sum
/// forty-eight periods for — and to whether summing them would even be right.
///
/// <para>The four figures are the same ones the dashboard's forecast table and the <c>/forecast</c>
/// page show, read from the summary the source worked out when its forecast landed rather than
/// recomputed per request.</para>
/// </summary>
/// <param name="Date">The local calendar date, as the site's own time zone reckons it.</param>
/// <param name="ExpectedWh">The median estimate for the whole day, in watt-hours.</param>
/// <param name="LowWh">
/// The p10 estimate for the day: a figure it should beat about nine times in ten. A period the provider
/// sent no p10 for counts at its median, as <see cref="SolarForecastPeriod.PowerWatts"/> does, so a
/// partial response cannot read as darkness.
/// </param>
/// <param name="HighWh">The p90 estimate, with the same fallback. The gap to <paramref name="LowWh"/> is how sure the sky is.</param>
/// <param name="PeakWatts">The strongest half hour the median expects, in watts.</param>
/// <param name="Complete">
/// Whether the whole day is held. <b>False is ordinary, not a fault</b>: a provider answers only what
/// is still to come, so a controller started at 10:44 has nothing for today before then. A caller that
/// reads an incomplete day's periods as the whole day will under-report production and be wrong about
/// the day — which is why this is a field rather than something to be inferred from the first period.
/// </param>
/// <param name="HeldFrom">
/// Where the held periods for this day actually begin, or null when the whole day is held. Always the
/// start of a period, never "now".
/// </param>
public sealed record ForecastDayResponse(
    DateOnly Date,
    double ExpectedWh,
    double LowWh,
    double HighWh,
    double PeakWatts,
    bool Complete,
    DateTimeOffset? HeldFrom)
{
    /// <summary>
    /// One day, from the summary taken when the forecast landed and the periods actually retained for
    /// it. <paramref name="dayStart"/> is the day's local midnight, which is what decides whether the
    /// morning is there.
    /// </summary>
    internal static ForecastDayResponse From(
        DateOnly date,
        SolarDayForecastSummary summary,
        SolarForecast day,
        DateTimeOffset dayStart)
    {
        // The earliest period held, not the earliest one that matters: a day whose first period starts
        // before its own midnight is the whole day, because the period ending at midnight belongs to it.
        var earliest = day.Periods.Count == 0 ? (DateTimeOffset?)null : day.Periods.Min(p => p.PeriodStart);
        var complete = earliest is not null && earliest <= dayStart;

        return new ForecastDayResponse(
            date,
            summary.ExpectedWh,
            summary.LowWh,
            summary.HighWh,
            summary.PeakWatts,
            complete,
            complete ? null : earliest);
    }
}

/// <summary>One forecast window: what the roof is expected to make, and how sure the provider is.</summary>
/// <param name="PeriodStart">Start of the window.</param>
/// <param name="PeriodEnd">End of the window.</param>
/// <param name="EstimatedPowerWatts">The median estimate — the average power expected across the window.</param>
/// <param name="EstimatedPowerWattsP10">
/// The "almost certainly at least this much" estimate, or null when the provider did not supply one.
/// The day plan's guarantees are built on this band, not the median.
/// </param>
/// <param name="EstimatedPowerWattsP90">The optimistic estimate, or null when not supplied.</param>
/// <param name="EnergyWh">What the median estimate amounts to over the window.</param>
public sealed record ForecastPeriodResponse(
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    double EstimatedPowerWatts,
    double? EstimatedPowerWattsP10,
    double? EstimatedPowerWattsP90,
    double EnergyWh)
{
    internal static ForecastPeriodResponse From(SolarForecastPeriod period) => new(
        period.PeriodStart,
        period.PeriodEnd,
        period.EstimatedPowerWatts,
        period.EstimatedPowerWattsP10,
        period.EstimatedPowerWattsP90,
        period.EnergyWattHours);
}

/// <summary>Conditions at the site, from the configured weather provider.</summary>
/// <param name="ObservedAt">When the provider made the observation.</param>
/// <param name="TemperatureCelsius">Air temperature.</param>
/// <param name="PressureHpa">Barometric pressure.</param>
/// <param name="HumidityPercent">Relative humidity.</param>
/// <param name="CloudsPercent">Cloud cover — the figure that matters to the roof.</param>
/// <param name="VisibilityMetres">Visibility, or null when the provider omitted it.</param>
/// <param name="Condition">The provider's short condition code, e.g. "Clouds".</param>
/// <param name="ConditionDescription">The provider's longer description, e.g. "broken clouds".</param>
public sealed record WeatherResponse(
    DateTimeOffset ObservedAt,
    double TemperatureCelsius,
    double PressureHpa,
    double HumidityPercent,
    double CloudsPercent,
    double? VisibilityMetres,
    string Condition,
    string ConditionDescription)
{
    internal static WeatherResponse From(WeatherObservation observation) => new(
        observation.ObservedAt,
        observation.TemperatureCelsius,
        observation.PressureHpa,
        observation.HumidityPercent,
        observation.CloudsPercent,
        observation.VisibilityMetres,
        observation.Condition,
        observation.ConditionDescription);
}

/// <summary>
/// What the car last said about itself, and how long ago it said it.
///
/// <para>The age is part of the reading. A cloud-reported state of charge arrives when the car
/// feels like it — routinely hours late — so a caller handed a bare percentage will treat a
/// six-hour-old number as current. Nothing about how the charger is driven depends on any of this: it
/// shapes what can be <em>asked</em> for, never how it is delivered.</para>
/// </summary>
/// <param name="Available">Whether any reading has arrived at all. False means every field below is null.</param>
/// <param name="CapturedAt">When the car reported this, as the source stated it.</param>
/// <param name="AgeSeconds">How old the reading is now. Negative is clamped to zero.</param>
/// <param name="Stale">Whether it is older than the configured maximum age — a dead feed, rather than merely an old number.</param>
/// <param name="SocPercent">The drive battery's state of charge, or null when the car does not report one.</param>
/// <param name="RangeKm">Electric range, or null when not reported. Display only — nothing plans on it.</param>
/// <param name="ChargeTimeRemainingMinutes">What the car says is left of its own charge, or null when not reported.</param>
/// <param name="ChargeState">What the car says it is doing.</param>
/// <param name="PlugState">Whether the car says a cable is connected. The charger's own view is <c>carConnected</c> on the status.</param>
/// <param name="SourceId">Which feed this came from, for diagnostics.</param>
/// <param name="Vehicle">
/// What the car <b>is</b>, as configured — as opposed to everything above, which is what it last
/// <em>said</em>. Null when no car has been described, which is a supported installation.
/// </param>
/// <param name="CanTargetSoc">
/// Whether a targeted charge may be asked for as a state of charge: a reported SOC and a configured
/// pack size (<c>Vehicle:BatteryCapacityKWh</c>). False means ask in kilowatt-hours.
/// </param>
public sealed record VehicleResponse(
    bool Available,
    DateTimeOffset? CapturedAt,
    double? AgeSeconds,
    bool Stale,
    double? SocPercent,
    double? RangeKm,
    double? ChargeTimeRemainingMinutes,
    VehicleChargeState ChargeState,
    VehiclePlugState PlugState,
    string? SourceId,
    bool CanTargetSoc,
    EvResponse? Vehicle = null)
{
    internal static VehicleResponse Unavailable(EvInfo? ev = null) => new(
        false, null, null, false, null, null, null,
        VehicleChargeState.Unknown, VehiclePlugState.Unknown, null, false,
        ev is { IsConfigured: true } ? EvResponse.From(ev) : null);

    internal static VehicleResponse From(
        VehicleState state, DateTimeOffset now, TimeSpan maxAge, bool capacityConfigured, EvInfo? ev = null)
    {
        var age = state.AgeAt(now);

        return new VehicleResponse(
            true,
            state.CapturedAt,
            age < TimeSpan.Zero ? 0 : age.TotalSeconds,
            state.IsStaleAt(now, maxAge),
            state.SocPercent,
            state.RangeKm,
            state.ChargeTimeRemaining?.TotalMinutes,
            state.ChargeState,
            state.PlugState,
            state.SourceId,
            capacityConfigured && state.SocPercent is not null,
            ev is { IsConfigured: true } ? EvResponse.From(ev) : null);
    }
}

/// <summary>
/// The car itself (issue #124): what it is and what it will accept, as distinct from the reading it
/// last sent. Configuration, so it changes only across a restart.
/// </summary>
/// <param name="Id">Its stable identity, or empty when unnamed.</param>
/// <param name="Name">What a human calls it.</param>
/// <param name="Make">The manufacturer. Reported, never acted on.</param>
/// <param name="Model">The model. Reported, never acted on.</param>
/// <param name="BatteryCapacityKWh">Usable capacity — the figure its SOC is a percentage of. Zero when unconfigured.</param>
/// <param name="ChargeEfficiency">Charger-meter → cells efficiency, applied to SOC-based targets.</param>
/// <param name="Phases">
/// Phases the <b>car</b> can charge on, or null when unstated. Where this differs from the charger's,
/// this is the one every power figure is computed from.
/// </param>
/// <param name="MinChargingCurrentAmps">The lowest current it will start on, or null when unstated.</param>
/// <param name="MaxChargingCurrentAmps">Its on-board ceiling, or null when unstated.</param>
public sealed record EvResponse(
    string Id,
    string Name,
    string Make,
    string Model,
    double BatteryCapacityKWh,
    double ChargeEfficiency,
    int? Phases,
    int? MinChargingCurrentAmps,
    int? MaxChargingCurrentAmps)
{
    internal static EvResponse From(EvInfo ev) => new(
        ev.Id,
        ev.Name,
        ev.Make,
        ev.Model,
        ev.BatteryCapacityKWh,
        ev.ChargeEfficiency,
        ev.Phases,
        ev.MinChargingCurrentAmps,
        ev.MaxChargingCurrentAmps);
}
