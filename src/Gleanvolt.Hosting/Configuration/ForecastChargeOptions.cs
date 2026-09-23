using Gleanvolt.Core.Enums;

namespace Gleanvolt.Hosting.Configuration;

/// <summary>
/// Configuration for the forecast-driven charge mode (issue #22). Bound from the
/// <c>"ChargeControl:Forecast"</c> section, i.e. nested inside <see cref="ChargeControlOptions"/>'s
/// section, because it refines that feature rather than being a separate one.
///
/// <para>Nothing here writes to hardware by itself: the mode is selected at runtime and still goes
/// through the same current-only control path as <c>Solar</c>. What it changes is <em>when</em> the
/// car is allowed to charge.</para>
/// </summary>
public sealed class ForecastChargeOptions
{
    public const string SectionName = "ChargeControl:Forecast";

    /// <summary>
    /// The evening deadline: the local time of day by which the home battery must be at 100%. The
    /// whole strategy is organised around this — the required SOC floor rises to meet it, and the
    /// battery's share of the forecast is booked backwards from it.
    /// </summary>
    public TimeSpan FullByTime { get; init; } = TimeSpan.FromHours(19);

    /// <summary>
    /// <b>Usable</b> capacity of the home battery, in kWh — not its nameplate. Every SOC-to-energy
    /// conversion scales off it, so a wrong value makes the plan wrong in a way nothing else can catch.
    ///
    /// <para>The inverter reports SOC across the range the pack will actually cycle, so 0–100 % spans
    /// the usable energy. The default is the reference install: a SolaX T-BAT H 2.5 stack of 10 kWh
    /// nominal, i.e. ~9 kWh at the ~90 % depth of discharge these packs allow. Set it to your own, and
    /// if in doubt round <em>up</em> — an overstated capacity books more of the forecast for the
    /// battery and raises the SOC floor, which are both the safe direction.</para>
    /// </summary>
    public double BatteryCapacityKWh { get; init; } = 9.0;

    /// <summary>PV → battery charge efficiency (0..1], used to size what the sun must actually deliver.</summary>
    public double ChargeEfficiency { get; init; } = 0.95;

    /// <summary>
    /// Household load excluding the EV, used until the rolling estimator has enough samples of its
    /// own. Also the value the estimator is seeded from after a restart.
    /// </summary>
    public double BaselineHouseLoadWatts { get; init; } = 350;

    /// <summary>Which forecast band to plan on. P10 by default; see <see cref="ForecastConfidence"/>.</summary>
    public ForecastConfidence ForecastConfidence { get; init; } = ForecastConfidence.P10;

    /// <summary>The hard SOC floor the computed trajectory is clamped to, whatever the forecast says.</summary>
    public double MinBatterySocFloorPercent { get; init; } = 50;

    /// <summary>
    /// How far above the floor the SOC must have recovered before a paused session may restart.
    /// Charging continues down to the floor itself; only coming back costs the margin.
    ///
    /// <para>Without it the gate turns on a single percent of SOC — the inverter reports whole
    /// percent — so a morning that starts just above the floor stops and restarts the car every few
    /// minutes. Keep it above <see cref="HoldReleaseMarginPercent"/>: the battery hold must have let
    /// go before the car comes back to compete for the surplus, or SOC simply pins to the floor with
    /// the grid covering every dip. On a 9 kWh pack 5% is ~450 Wh, roughly nine minutes of a 3 kW
    /// surplus going into the battery.</para>
    /// </summary>
    public double FloorResumeMarginPercent { get; init; } = 5;

    /// <summary>
    /// Surplus withheld from the car while SOC is inside the guard band — the same
    /// <see cref="FloorResumeMarginPercent"/> width, measured up from the plan's floor. 0 disables it.
    ///
    /// <para>The resume margin stops the car flapping across the floor; this stops it hovering on it.
    /// A car that takes every watt the sun makes holds the pack exactly at the floor for the whole
    /// morning — no start/stop churn, but no recovery either, and every dip covered by the grid
    /// because the hold is armed. Withholding a few hundred watts inside the band walks the battery
    /// back out of it: on the 9 kWh default, 750 W clears a 5% band in about forty minutes and costs a
    /// three-phase session roughly one amp. Raise it to get out of the band faster, lower it if
    /// marginal mornings stop reaching the charger's 6 A floor at all.</para>
    /// </summary>
    public double FloorGuardReserveWatts { get; init; } = 750;

    /// <summary>
    /// Ceiling on energy per charging session, in kWh. 0 = unlimited. A ceiling, not a request — an
    /// owner asking for an amount by a time wants the Targeted mode.
    /// </summary>
    public double SessionEnergyTargetKWh { get; init; }

    /// <summary>Whether the home battery may bridge a sub-minimum surplus up to the charger's floor.</summary>
    public bool EnableBatteryLoan { get; init; } = true;

    /// <summary>
    /// Ceiling on that bridge, and the cap on how fast the battery is asked to discharge. It must be
    /// able to reach the minimum charge power from the thinnest surplus a loan is granted for, or it
    /// quietly reimposes a floor of its own: at 2500 W against a 4.14 kW minimum nothing under 1.64 kW
    /// could ever charge, whatever <see cref="SpillBridgeSurplusWatts"/> said (issue #223). The default
    /// covers the whole gap on three phases; on one phase the minimum is ~1.4 kW and far less is needed.
    /// Set it to the inverter's discharge limit if that is the binding figure on your install.
    /// </summary>
    public double MaxLoanPowerWatts { get; init; } = 3800;

    /// <summary>
    /// Minimum live surplus before any loan is granted <b>on a day the pack can still absorb its own
    /// surplus</b>. The loan tops up a genuine surplus that just falls short of the charger's floor; it
    /// never funds a session on its own, which would be a battery-to-car transfer paying a round trip
    /// and a cycle on both packs for nothing.
    /// </summary>
    public double MinBridgeSurplusWatts { get; init; } = 2000;

    /// <summary>
    /// The same floor on a day whose remaining surplus the pack has <b>no room for</b> — the plan's
    /// spill above zero, which at 100% SOC is the whole of it. The reasoning above fails there: the
    /// energy lent is not energy the pack would have kept, it is energy about to leave the house, so the
    /// round trip buys it back rather than paying for nothing. Low, but deliberately not zero: cycling
    /// the pack to chase a few hundred watts of noise is wear bought for nothing either way.
    /// </summary>
    public double SpillBridgeSurplusWatts { get; init; } = 400;

    /// <summary>
    /// Ceiling on <b>outstanding</b> lending in one day, in kWh — lent minus what the pack has charged
    /// back — reset at local midnight. A backstop against wear, not the binding constraint: what the
    /// pack is still down is the wear, and a pack refilled to 100% by lunchtime is where it started.
    /// </summary>
    public double MaxDailyLoanKWh { get; init; } = 4;

    /// <summary>How far above the required floor the SOC must sit before the battery will lend at all.</summary>
    public double LoanSocMarginPercent { get; init; } = 2;

    /// <summary>The shortest stretch of chargeable weather worth starting a session for.</summary>
    public TimeSpan MinViableWindow { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Once charging, the shortest run before a soft reason (a surplus dip) may stop it.</summary>
    public TimeSpan MinRunTime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Once paused, the shortest wait before charging may restart.</summary>
    public TimeSpan MinPauseTime { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long before the deadline the car is paused outright while the battery is under 100%. The
    /// SOC trajectory should already have converged; this catches an afternoon the forecast got wrong.
    /// </summary>
    public TimeSpan FinalGuardBefore { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How old a forecast may be before the plan is treated as unusable and the mode degrades to
    /// live-solar behaviour. Never assume optimistic headroom from a stale forecast.
    /// </summary>
    public TimeSpan StaleForecastAfter { get; init; } = TimeSpan.FromHours(4);

    /// <summary>
    /// Whether the battery discharge hold (issue #20) is armed automatically at the trajectory floor,
    /// so an estimate error physically cannot dig below it. Requires <c>BatteryHold:Enabled</c>; a
    /// manual hold from Home Assistant always wins.
    /// </summary>
    public bool AutoArmBatteryHoldAtFloor { get; init; } = true;

    /// <summary>How far SOC must recover above the floor before an auto-armed hold is released again.</summary>
    public double HoldReleaseMarginPercent { get; init; } = 2;

    /// <summary>Closed daylight forecast periods required before the realised bias is trusted.</summary>
    public int BiasMinPeriods { get; init; } = 4;

    /// <summary>Lower clamp on the realised bias applied to the remaining forecast.</summary>
    public double BiasClampMin { get; init; } = 0.5;

    /// <summary>Upper clamp on the realised bias — stops a sunny morning over-committing the afternoon.</summary>
    public double BiasClampMax { get; init; } = 1.2;

    /// <summary>Below this cumulative bias the forecast is considered untrustworthy.</summary>
    public double TrustBandMin { get; init; } = 0.6;

    /// <summary>Above this cumulative bias the forecast is considered untrustworthy.</summary>
    public double TrustBandMax { get; init; } = 1.4;

    /// <summary>Consecutive out-of-band periods before the plan is abandoned for the day.</summary>
    public int TrustBreachPeriods { get; init; } = 3;
}
