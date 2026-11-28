using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Devices;

namespace Gleanvolt.Api.Contracts;

/// <summary>
/// What the controller is doing right now: one poll's worth of the whole site, as the control loop
/// last read it.
///
/// <para>Every power is instantaneous and in watts, signed the way the hardware reports it, and every
/// energy is in watt-hours. <c>timestamp</c> is when the poll behind these figures completed — check
/// it before drawing conclusions from anything else here.</para>
/// </summary>
/// <param name="Timestamp">When the poll behind this snapshot completed.</param>
/// <param name="Mode">The charge-control mode currently selected.</param>
/// <param name="State">Coarse state of charge control, for display.</param>
/// <param name="DryRun">Whether charge control is deciding and logging but not writing to the charger.</param>
/// <param name="HoldingControl">Whether the controller is actually driving the charger right now.</param>
/// <param name="SolarPowerWatts">What the roof is producing.</param>
/// <param name="ForecastSolarPowerWatts">
/// What the forecast expected the roof to be making at this instant. 0 when no forecast covers it —
/// none fetched, the provider is down, or it is past the horizon — which is not the same as darkness.
/// </param>
/// <param name="GridPowerWatts">Grid meter power: positive is importing, negative is exporting.</param>
/// <param name="BatteryPowerWatts">Home battery power: positive is charging, negative is discharging.</param>
/// <param name="BatterySocPercent">Home battery state of charge. Nothing to do with the car.</param>
/// <param name="EvChargerPowerWatts">What the EV charger is drawing.</param>
/// <param name="EvChargingCurrentAmps">Charging current derived from the charger's power, phase-aware.</param>
/// <param name="TargetCurrentAmps">The current the controller wants, or null when it is not charging.</param>
/// <param name="ActiveCurrentAmps">The charger's active setpoint as read back, or null when unknown.</param>
/// <param name="SurplusWatts">The averaged solar surplus being decided on, or null when not solar-charging.</param>
/// <param name="ChargerStatus">The charger's own status.</param>
/// <param name="CarConnected">Whether a vehicle is plugged in, as the charger sees it.</param>
/// <param name="SessionEnergyWh">Energy delivered to the car in the session now running.</param>
/// <param name="LoanPowerWatts">How much of the current charge the home battery is lending.</param>
/// <param name="LoanedTodayWh">Energy lent out of the home battery today.</param>
/// <param name="TomorrowForecastWh">Tomorrow's forecast production. Informational.</param>
/// <param name="BatteryHold">The battery discharge hold: configured, requested and armed are three different things.</param>
/// <param name="DayPlan">The forecast-driven day plan, when a mode is using one. Null otherwise.</param>
/// <param name="TargetedPlan">
/// The energy-by-departure plan, populated only while <c>Targeted</c> is the mode driving the charger,
/// so a different mode cannot leave a stale target on display.
/// </param>
/// <param name="FastCharge">
/// The fast charge's limit and how much of it has been delivered, populated only while
/// <c>FastNoBattery</c> is the mode driving the charger — the same rule. Null also when that mode is
/// running with no limit, which means the car decides when to stop.
/// </param>
/// <param name="LowTariff">
/// What <i>low tariff only</i> is doing for the mode driving (issue #274): waiting through high tariff
/// until when, or set with no tariff to wait for. Null while that mode has the option off, or has none.
/// </param>
/// <param name="UntilFull">
/// A <c>forecasted</c> session running <i>until full</i> (issue #279): since when, and which day of it this
/// is. Null while another mode drives, or the option is off.
/// </param>
/// <param name="Forecast">
/// Where the solar forecast behind the forecast figures came from and when (issue #388): Solcast, or an
/// external provider by name. Null while no forecast is held, and in the status a control action
/// answers with: read it from GET /status.
/// </param>
/// <param name="VehicleSource">
/// The feed the car's readings come from (issue #408): its source (<c>external</c> for an external
/// provider) and its name. Null when no feed reads the car, and in the status a control action answers
/// with: read it from GET /status.
/// </param>
/// <param name="Settings">
/// Minimum battery SOC, SOC resume margin and min solar surplus as the controller is using them now
/// (issue #392) — the same object as GET /charging/settings, read when the status is served rather than
/// at the last poll, so a change made elsewhere shows at once. Null in the status a control action
/// answers with: read it from GET /status.
/// </param>
/// <param name="ChargerAvailable">
/// Whether Gleanvolt can control the charger (issue #441). False after it has been unavailable for longer than
/// <c>ChargeControl:ChargerUnavailableAfter</c> (2 minutes): no charger plugin connected, a plugin refused or
/// reporting an error, or a charger that can't be read. Meanwhile no mode can be started (409), a running one is
/// suspended, and the charger follows its own settings.
/// </param>
/// <param name="ChargerState">Which: <c>available</c>, <c>noPlugin</c>, <c>needsOwner</c> or <c>error</c>.</param>
/// <param name="ChargerReason">Why it is unavailable, in English; null while it is available.</param>
/// <param name="SuspendedSince">
/// When the running mode was suspended for the charger (issue #441); null while it isn't. The mode stays selected
/// and resumes by itself when the charger is back.
/// </param>
public sealed record StatusResponse(
    DateTimeOffset Timestamp,
    ChargeControlMode Mode,
    ChargeControlState State,
    bool DryRun,
    bool HoldingControl,
    double SolarPowerWatts,
    double ForecastSolarPowerWatts,
    double GridPowerWatts,
    double BatteryPowerWatts,
    double BatterySocPercent,
    double EvChargerPowerWatts,
    int EvChargingCurrentAmps,
    int? TargetCurrentAmps,
    int? ActiveCurrentAmps,
    double? SurplusWatts,
    EvChargerStatus ChargerStatus,
    bool CarConnected,
    double SessionEnergyWh,
    double LoanPowerWatts,
    double LoanedTodayWh,
    double? TomorrowForecastWh,
    BatteryHoldResponse BatteryHold,
    SolarDayPlanResponse? DayPlan,
    TargetedPlanResponse? TargetedPlan,
    FastChargeResponse? FastCharge = null,
    LowTariffStatusResponse? LowTariff = null,
    UntilFullStatusResponse? UntilFull = null,
    ForecastOriginResponse? Forecast = null,
    VehicleSourceResponse? VehicleSource = null,
    ChargingSettingsResponse? Settings = null,
    bool ChargerAvailable = true,
    ChargerAvailabilityState ChargerState = ChargerAvailabilityState.Available,
    string? ChargerReason = null,
    DateTimeOffset? SuspendedSince = null)
{
    internal static StatusResponse From(
        ChargeControlStatus status,
        SolarForecastSnapshot? forecast = null,
        IVehicleUpdateService? vehicleFeed = null,
        ChargingSettingsResponse? settings = null) => new(
        status.Timestamp,
        status.Mode,
        status.State,
        status.DryRun,
        status.HoldingControl,
        status.SolarPowerWatts,
        status.ForecastSolarPowerWatts,
        status.GridPowerWatts,
        status.BatteryPowerWatts,
        status.BatterySocPercent,
        status.EvChargerPowerWatts,
        status.EvChargingCurrentAmps,
        status.TargetCurrentAmps,
        status.ActiveCurrentAmps,
        status.SurplusWatts,
        status.ChargerStatus,
        status.CarConnected,
        status.SessionEnergyWh,
        status.LoanPowerWatts,
        status.LoanedTodayWh,
        status.TomorrowForecastWh,
        new BatteryHoldResponse(
            status.BatteryHoldEnabled,
            status.BatteryHoldRequested,
            status.BatteryHoldActive,
            status.BatteryHoldTargetWatts),
        status.Plan is { } plan ? SolarDayPlanResponse.From(plan) : null,
        status.TargetedPlan is { } targeted ? TargetedPlanResponse.From(targeted) : null,
        status.FastCharge is { } fast ? FastChargeResponse.From(fast) : null,
        status.LowTariff is { } low ? new LowTariffStatusResponse(low.Waiting, low.Until, low.Ignored) : null,
        status.UntilFull is { } untilFull ? new UntilFullStatusResponse(untilFull.Since, untilFull.Day) : null,
        forecast is { } held ? new ForecastOriginResponse(held.Origin.SourceId, held.Origin.Name, held.Forecast.RetrievedAt, held.ReceivedAt) : null,
        vehicleFeed is { } feed ? new VehicleSourceResponse(feed.Manufacturer, feed.DisplayName, feed.Health.State) : null,
        settings,
        status.Charger?.Available ?? true,
        status.Charger?.State ?? ChargerAvailabilityState.Available,
        status.Charger?.Reason,
        status.SuspendedSince);
}

/// <summary>The feed the car is read from.</summary>
/// <param name="Source">
/// Which feed: <c>external</c>, a car plugin. The built-in manufacturer feeds' ids (<c>vw-website</c>,
/// <c>skoda</c>, <c>vw-group</c>) went with them in #477. For display and monitoring; it may grow.
/// </param>
/// <param name="Name">What to show for it: the car plugin's name.</param>
/// <param name="Health">Whether it can currently produce a reading.</param>
public sealed record VehicleSourceResponse(string Source, string Name, VehicleSourceState Health);

/// <summary>Where the solar forecast came from.</summary>
/// <param name="Source">The forecast source: <c>solcast</c> or <c>external</c>, as <c>SolarForecast:Source</c> names them.</param>
/// <param name="Name">What to show for it: "Solcast", or the external provider's name.</param>
/// <param name="IssuedAt">When the forecast was made: Solcast's fetch, or the time the provider stamped on it.</param>
/// <param name="ReceivedAt">When Gleanvolt received it.</param>
public sealed record ForecastOriginResponse(string Source, string Name, DateTimeOffset IssuedAt, DateTimeOffset ReceivedAt);

/// <summary>What <i>low tariff only</i> is doing for the mode driving.</summary>
/// <param name="Waiting">True while the mode holds the car back through high tariff.</param>
/// <param name="Until">When low tariff starts, while waiting; null when that is beyond what the tariff knows.</param>
/// <param name="Ignored">True when the option is set but there is no tariff to wait for, so the mode charges whatever the hour.</param>
public sealed record LowTariffStatusResponse(bool Waiting, DateTimeOffset? Until, bool Ignored);

/// <summary>A <c>forecasted</c> session running <i>until full</i>: one session over several days.</summary>
/// <param name="Since">When the session started. A restart resumes it and keeps this.</param>
/// <param name="Day">Which local day of the session this is, from 1 on the day it was started.</param>
public sealed record UntilFullStatusResponse(DateTimeOffset Since, int Day);

/// <summary>
/// The battery discharge hold, which stops the home battery serving house load so the car charges
/// from PV and grid while the pack still charges from surplus.
/// </summary>
/// <param name="Enabled">Whether the feature is configured on at all. False means the other two can never be true.</param>
/// <param name="Requested">Whether it is switched on — somebody's intent, not the hardware's state.</param>
/// <param name="Active">
/// Whether a hold command is currently armed on the inverter. This is what was last written, not a
/// read-back: the command register cannot be read. An inverter that ignores the command still
/// reports true here, so judge a hold by <c>batteryPowerWatts</c>, never by this flag.
/// </param>
/// <param name="TargetWatts">The active-power target currently commanded, or null when nothing is held.</param>
public sealed record BatteryHoldResponse(bool Enabled, bool Requested, bool Active, double? TargetWatts);

/// <summary>
/// The forecast-driven day plan: how much of today's remaining sun the car may have, so the home
/// battery still reaches 100% by its evening deadline.
/// </summary>
/// <param name="RemainingPvWh">Forecast PV still to come today.</param>
/// <param name="ShoulderEnergyWh">Forecast energy in periods too weak for the charger's floor — the house's and the battery's.</param>
/// <param name="PlateauEnergyWh">Forecast energy in periods strong enough to run the charger.</param>
/// <param name="PlateauClaimedByBatteryWh">The part of the plateau the home battery has already booked.</param>
/// <param name="ExpectedHouseWh">What the house is expected to consume over the rest of the day.</param>
/// <param name="BatteryToFullWh">What the home battery still needs to reach 100%, charge losses included.</param>
/// <param name="EvBudgetWh">What the car may have from the roof today, after the house and the pack.</param>
/// <param name="FeasibleEvEnergyWh">Of that budget, what the charger can physically deliver in the time left.</param>
/// <param name="RequiredSocFloorPercent">The SOC the home battery must not be drawn below.</param>
/// <param name="TrajectorySocFloorPercent">The same floor as a trajectory over the day rather than a single number.</param>
/// <param name="SpillWh">
/// Remaining surplus the home battery has no room for: what is coming less what it can still absorb.
/// At 100% SOC that is all of it, and every watt of it leaves the house unless the car takes it — which
/// is what makes the battery loan free, and what opens the loan's thin-surplus rules.
/// </param>
/// <param name="LoanHeadroomWh">
/// How much the pack may lend right now for free: the room between SOC and the floor, capped by the
/// spill. Zero either when SOC sits on the floor or when every remaining watt has a home in the pack.
/// </param>
/// <param name="ShortfallWh">How far the day falls short of the house plus filling the battery by its deadline.</param>
/// <param name="BiasFactor">The learned correction applied to the raw forecast: 1.0 is "believe it as given".</param>
/// <param name="Deadline">When the home battery is expected to be full by.</param>
/// <param name="ForecastAsOf">When the forecast behind this plan was fetched, or null when there was none.</param>
/// <param name="IsUsable">
/// Whether a usable forecast went into it. False means the controller has degraded to plain live-solar
/// behaviour, and the figures here are not being acted on.
/// </param>
/// <param name="Reason">A short human-readable summary of what the plan concluded.</param>
public sealed record SolarDayPlanResponse(
    double RemainingPvWh,
    double ShoulderEnergyWh,
    double PlateauEnergyWh,
    double PlateauClaimedByBatteryWh,
    double ExpectedHouseWh,
    double BatteryToFullWh,
    double EvBudgetWh,
    double FeasibleEvEnergyWh,
    double RequiredSocFloorPercent,
    double TrajectorySocFloorPercent,
    double SpillWh,
    double LoanHeadroomWh,
    double ShortfallWh,
    double BiasFactor,
    DateTimeOffset Deadline,
    DateTimeOffset? ForecastAsOf,
    bool IsUsable,
    string Reason)
{
    internal static SolarDayPlanResponse From(SolarDayPlan plan) => new(
        plan.RemainingPvWh,
        plan.ShoulderEnergyWh,
        plan.PlateauEnergyWh,
        plan.PlateauClaimedByBatteryWh,
        plan.ExpectedHouseWh,
        plan.BatteryToFullWh,
        plan.EvBudgetWh,
        plan.FeasibleEvEnergyWh,
        plan.RequiredSocFloorPercent,
        plan.TrajectorySocFloorPercent,
        plan.SpillWh,
        plan.LoanHeadroomWh,
        plan.ShortfallWh,
        plan.BiasFactor,
        plan.Deadline,
        plan.ForecastAsOf,
        plan.IsUsable,
        plan.Reason);
}
