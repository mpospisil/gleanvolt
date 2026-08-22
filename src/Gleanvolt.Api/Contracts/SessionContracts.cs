using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Api.Contracts;

/// <summary>
/// One controlled charging session: when it ran, what drove it, and where the energy came from.
///
/// <para>The three source figures sum to <c>energyDeliveredWh</c>. They are an attribution made from
/// the instantaneous power flows at each poll, not four separate meters — the split is as good as the
/// polling interval, and no better.</para>
/// </summary>
/// <param name="Id">The session's identifier, as used by the detail endpoint.</param>
/// <param name="StartedAt">When the controller took charge of the session.</param>
/// <param name="EndedAt">When it ended, or null while it is still running.</param>
/// <param name="TimeZoneId">The site's zone at the time, so the local day is recoverable.</param>
/// <param name="StartMode">The mode that started it.</param>
/// <param name="EndMode">The mode in force when it ended.</param>
/// <param name="EndReason">Why it ended, or null while it is still running.</param>
/// <param name="Controlled">Whether the controller actually drove the charger, as against merely watching a session it never took.</param>
/// <param name="EnergyDeliveredWh">Total energy into the car, measured at the charger.</param>
/// <param name="FromSolarWh">The part attributed to surplus PV.</param>
/// <param name="FromGridWh">The part attributed to imported energy.</param>
/// <param name="FromBatteryWh">The part attributed to the home battery.</param>
/// <param name="LoanedWh">Energy the home battery lent to the charge, expecting the forecast to repay it.</param>
/// <param name="SolarFraction">The solar share of the delivery, 0 to 1, or null when nothing was delivered.</param>
/// <param name="PeakChargingPowerWatts">The highest charging power reached.</param>
/// <param name="StartBatterySocPercent">Home battery SOC when the session opened.</param>
/// <param name="EndBatterySocPercent">Home battery SOC when it closed, or null while it is open.</param>
/// <param name="ForecastRemainingAtStartWh">What the forecast still expected from the roof when it opened.</param>
/// <param name="Sunrise">Sunrise on the day it started, when weather was recorded.</param>
/// <param name="Sunset">Sunset on the day it started, when weather was recorded.</param>
/// <param name="WeatherAtStart">The conditions when it opened, or null when no weather provider is configured.</param>
/// <param name="WeatherAtEnd">The conditions when it closed.</param>
public sealed record SessionResponse(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string TimeZoneId,
    ChargeControlMode StartMode,
    ChargeControlMode EndMode,
    ChargingSessionEndReason? EndReason,
    bool Controlled,
    double EnergyDeliveredWh,
    double FromSolarWh,
    double FromGridWh,
    double FromBatteryWh,
    double LoanedWh,
    double? SolarFraction,
    double PeakChargingPowerWatts,
    double StartBatterySocPercent,
    double? EndBatterySocPercent,
    double? ForecastRemainingAtStartWh,
    DateTimeOffset? Sunrise,
    DateTimeOffset? Sunset,
    WeatherResponse? WeatherAtStart,
    WeatherResponse? WeatherAtEnd)
{
    internal static SessionResponse From(ChargingSession session) => new(
        session.Id,
        session.StartedAt,
        session.EndedAt,
        session.TimeZoneId,
        session.StartMode,
        session.EndMode,
        session.EndReason,
        session.Controlled,
        session.EnergyDeliveredWh,
        session.FromSolarWh,
        session.FromGridWh,
        session.FromBatteryWh,
        session.LoanedWh,
        session.SolarFraction,
        session.PeakChargingPowerWatts,
        session.StartBatterySocPercent,
        session.EndBatterySocPercent,
        session.ForecastRemainingAtStartWh,
        session.Sunrise,
        session.Sunset,
        session.WeatherAtStart is { } start ? WeatherResponse.From(start) : null,
        session.WeatherAtEnd is { } end ? WeatherResponse.From(end) : null);
}

/// <summary>Sessions that started inside a requested range, newest first.</summary>
/// <param name="From">The start of the range that was queried, inclusive.</param>
/// <param name="To">The end of the range that was queried, exclusive.</param>
/// <param name="Count">How many sessions came back.</param>
/// <param name="Truncated">Whether the range held more sessions than the limit allowed back.</param>
/// <param name="Sessions">The sessions themselves.</param>
public sealed record SessionListResponse(
    DateTimeOffset From,
    DateTimeOffset To,
    int Count,
    bool Truncated,
    IReadOnlyList<SessionResponse> Sessions);

/// <summary>
/// A session in full: the summary, every poll recorded against it, and the notable moments.
///
/// <para>An open session is returned too — a caller may want to watch one in progress — but only a
/// closed one will never change again.</para>
/// </summary>
/// <param name="SchemaVersion">The version of the recorded document these samples were written under.</param>
/// <param name="Session">The session summary, identical to the one the listing returns.</param>
/// <param name="Samples">Every poll recorded against the session, oldest first.</param>
/// <param name="Events">The notable moments: mode changes, holds armed, the car going quiet.</param>
public sealed record SessionDetailResponse(
    int SchemaVersion,
    SessionResponse Session,
    IReadOnlyList<SessionSampleResponse> Samples,
    IReadOnlyList<SessionEventResponse> Events)
{
    internal static SessionDetailResponse From(ChargingSessionDocument document) => new(
        document.SchemaVersion,
        SessionResponse.From(document.Session),
        [.. document.Samples.Select(SessionSampleResponse.From)],
        [.. document.Events.Select(SessionEventResponse.From)]);
}

/// <summary>
/// One poll inside a session: the instantaneous flows, the running totals, and what the plan and the
/// car looked like at that moment.
/// </summary>
/// <param name="Timestamp">When the poll completed.</param>
/// <param name="Mode">The mode in force.</param>
/// <param name="State">Coarse state of charge control.</param>
/// <param name="ChargerStatus">The charger's own status.</param>
/// <param name="BatterySocPercent">Home battery state of charge.</param>
/// <param name="SolarPowerWatts">PV production at that instant.</param>
/// <param name="GridPowerWatts">Grid meter power: positive is importing.</param>
/// <param name="BatteryPowerWatts">Home battery power: positive is charging.</param>
/// <param name="EvChargerPowerWatts">What the charger was drawing.</param>
/// <param name="EvChargingCurrentAmps">Charging current derived from that power.</param>
/// <param name="ActiveCurrentAmps">The charger's active setpoint as read back.</param>
/// <param name="TargetCurrentAmps">The setpoint the controller wanted.</param>
/// <param name="FromSolarWatts">The part of the charge attributed to PV at that instant.</param>
/// <param name="FromGridWatts">The part attributed to import.</param>
/// <param name="FromBatteryWatts">The part attributed to the home battery.</param>
/// <param name="EnergyDeliveredWh">Running total into the car.</param>
/// <param name="FromSolarWh">Running total attributed to PV.</param>
/// <param name="FromGridWh">Running total attributed to import.</param>
/// <param name="FromBatteryWh">Running total attributed to the home battery.</param>
/// <param name="LoanedWh">Running total lent by the home battery.</param>
/// <param name="SolarWh">Running total the roof produced during the session.</param>
/// <param name="ForecastSolarWh">What the forecast expected the roof to produce over the same stretch.</param>
/// <param name="GridImportWh">Running total imported during the session, the car included.</param>
/// <param name="VehicleSocPercent">What the car reported, when there is a vehicle feed.</param>
/// <param name="VehicleSocCapturedAt">When the car reported it — routinely hours before this poll.</param>
/// <param name="SurplusWatts">The averaged surplus being decided on.</param>
/// <param name="LoanPowerWatts">What the home battery was lending at that instant.</param>
/// <param name="BatteryHoldActive">Whether a hold was armed. What was written, not a read-back.</param>
/// <param name="ForecastPowerWatts">What the forecast expected the roof to be making at that instant.</param>
public sealed record SessionSampleResponse(
    DateTimeOffset Timestamp,
    ChargeControlMode Mode,
    ChargeControlState State,
    EvChargerStatus ChargerStatus,
    double BatterySocPercent,
    double SolarPowerWatts,
    double GridPowerWatts,
    double BatteryPowerWatts,
    double EvChargerPowerWatts,
    int EvChargingCurrentAmps,
    int? ActiveCurrentAmps,
    int? TargetCurrentAmps,
    double FromSolarWatts,
    double FromGridWatts,
    double FromBatteryWatts,
    double EnergyDeliveredWh,
    double FromSolarWh,
    double FromGridWh,
    double FromBatteryWh,
    double LoanedWh,
    double SolarWh,
    double? ForecastSolarWh,
    double GridImportWh,
    double? VehicleSocPercent,
    DateTimeOffset? VehicleSocCapturedAt,
    double? SurplusWatts,
    double LoanPowerWatts,
    bool BatteryHoldActive,
    double? ForecastPowerWatts)
{
    internal static SessionSampleResponse From(ChargingSessionSample sample) => new(
        sample.Timestamp,
        sample.Mode,
        sample.State,
        sample.ChargerStatus,
        sample.BatterySocPercent,
        sample.SolarPowerWatts,
        sample.GridPowerWatts,
        sample.BatteryPowerWatts,
        sample.EvChargerPowerWatts,
        sample.EvChargingCurrentAmps,
        sample.ActiveCurrentAmps,
        sample.TargetCurrentAmps,
        sample.FromSolarWatts,
        sample.FromGridWatts,
        sample.FromBatteryWatts,
        sample.EnergyDeliveredWh,
        sample.FromSolarWh,
        sample.FromGridWh,
        sample.FromBatteryWh,
        sample.LoanedWh,
        sample.SolarWh,
        sample.ForecastSolarWh,
        sample.GridImportWh,
        sample.VehicleSocPercent,
        sample.VehicleSocCapturedAt,
        sample.SurplusWatts,
        sample.LoanPowerWatts,
        sample.BatteryHoldActive,
        sample.ForecastPowerWatts);
}

/// <summary>A notable moment inside a session.</summary>
/// <param name="Timestamp">When it happened.</param>
/// <param name="Kind">What kind of moment it was.</param>
/// <param name="Detail">A short human-readable description.</param>
public sealed record SessionEventResponse(DateTimeOffset Timestamp, ChargingSessionEventKind Kind, string Detail)
{
    internal static SessionEventResponse From(ChargingSessionEvent sessionEvent) =>
        new(sessionEvent.Timestamp, sessionEvent.Kind, sessionEvent.Detail);
}
