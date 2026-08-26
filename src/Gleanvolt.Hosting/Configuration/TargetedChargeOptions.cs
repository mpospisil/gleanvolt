using Gleanvolt.Core.Enums;

namespace Gleanvolt.Hosting.Configuration;

/// <summary>
/// Configuration for the targeted charge mode (issue #80). Bound from the
/// <c>"ChargeControl:Targeted"</c> section — nested inside <see cref="ChargeControlOptions"/>'s
/// section, because it refines that feature rather than being a separate one.
///
/// <para>Everything the <em>owner</em> supplies for this mode — the energy and the departure time —
/// is a runtime request rather than configuration, and is deliberately not settable here: it belongs
/// to one trip, not to the installation.</para>
/// </summary>
public sealed class TargetedChargeOptions
{
    public const string SectionName = "ChargeControl:Targeted";

    /// <summary>
    /// How far before the stated departure the plan finishes. "Ready at 07:00" must not mean "still
    /// charging at 07:00": the owner needs to be able to unplug and go, and a car that is still
    /// drawing when they get to it is not ready.
    /// </summary>
    public TimeSpan SafetyMargin { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How far ahead a departure may be set. Solcast's cached forecast runs days ahead, but a target
    /// four days out is a promise the forecast cannot keep — and the request does not survive a
    /// restart, so a multi-day one is a promise this service cannot keep either.
    /// </summary>
    public TimeSpan MaxHorizon { get; init; } = TimeSpan.FromHours(36);

    /// <summary>
    /// Whether the grid may bridge a real-but-insufficient surplus up to the charger's floor while a
    /// target is running.
    ///
    /// <para>On three phases that floor is ~4.14 kW, so a perfectly good 3.5 kW surplus charges nothing
    /// and is exported — and on a plan that already needs the grid, the same kilowatt-hours are then
    /// bought back after dark. Bridging pays <c>floor − surplus</c> now instead of the whole floor
    /// later, and shrinks the planned grid block watt for watt.</para>
    ///
    /// <para>It never funds a session on its own: it is refused on a plan the sun covers outright,
    /// below the plan's SOC floor, and on a surplus under
    /// <c>ChargeControl:Forecast:MinBridgeSurplusWatts</c> — the same "is this surplus real?" line the
    /// battery loan uses. Turn it off to keep the mode's imports strictly
    /// inside the blocks the plan drew.</para>
    /// </summary>
    public bool GridBridge { get; init; } = true;

    /// <summary>
    /// The <see cref="Core.Enums.TargetedChargePriority.JustInTime"/> knobs. Two of them, and
    /// deliberately no more — see <see cref="JustInTimeOptions"/>.
    /// </summary>
    public JustInTimeOptions JustInTime { get; init; } = new();

    /// <summary>
    /// Which forecast band the <b>pace</b> is computed on. P50 — the likely day — by default, unlike the
    /// forecast-driven mode's pessimistic P10.
    ///
    /// <para>The bands answer different questions. P10 is right for a promise you must not break; this
    /// mode's promise does not rest on the forecast at all — feasibility is
    /// <c>P_max × (deadline − now)</c>, pure arithmetic about the charger and the clock. The only thing
    /// the band decides here is <em>how much to buy</em>, and for that P10 is systematically wrong in
    /// the expensive direction.</para>
    ///
    /// <para>Measured on 2026-08-27: the plan called 3.4 kWh of solar for the window and the roof
    /// delivered 11.98 kWh — a 3.5× under-call. The pace is <c>(need − sun ahead) ÷ time left</c>, so
    /// under-calling the sun overstates the deficit and sets the pace too high (6.7 kW here); the car
    /// then imports hard from the first minute. The plan does self-correct as real sun arrives — the
    /// grid share fell 26.6 → 17.8 → 11.2 kWh — but everything bought before the correction was bought
    /// for nothing.</para>
    ///
    /// <para>The other direction fails safely and visibly: an over-called forecast leaves the pace to
    /// climb on later polls, capped by the charger's ceiling, and the plan reports a shortfall honestly
    /// if it ever cannot make the deadline. Set this to <c>P10</c> to restore the old behaviour.</para>
    /// </summary>
    public ForecastConfidence PaceConfidence { get; init; } = ForecastConfidence.P50;
}

/// <summary>
/// What a just-in-time hold needs to know, bound from <c>"ChargeControl:Targeted:JustInTime"</c>.
///
/// <para>Two settings, because the design turns on doing <em>less</em> here rather than more. There is
/// no taper factor, no charge curve and no charge-limit setting: the plan is rebuilt every poll from
/// measured delivery, so a tail that runs slow raises its own pace, and a car that stops on its own
/// limit is reported by the controller's completion path. Predicting the car in advance would add ways
/// to be wrong without adding a way to be right.</para>
/// </summary>
public sealed class JustInTimeOptions
{
    /// <summary>
    /// Where the car waits before the last stretch is released, as a state of charge. The default
    /// offered by the web UI; the owner can move it per request, and it means nothing at all under
    /// <see cref="Core.Enums.TargetedChargePriority.Cheapest"/>.
    ///
    /// <para>80% is the conventional answer, and the one every manufacturer's own guidance lands on: it
    /// is high enough that the held tail is small, and low enough that the pack is not sitting in the
    /// voltage band that ages it.</para>
    /// </summary>
    public double RestSocPercent { get; init; } = 80;

    /// <summary>
    /// How much earlier than strictly necessary the held tail is released.
    ///
    /// <para>The release point is <c>deadline − tail ÷ P_max − this</c>. It is the whole allowance for
    /// a tail that cannot run at the charger's rated maximum — which it usually cannot, because a car
    /// near full tapers — and it is deliberately a plain margin rather than a model of the taper.
    /// Thirty minutes covers a normal 80 → 100 stretch on a domestic AC charger; raise it if recorded
    /// sessions show the tail arriving late.</para>
    /// </summary>
    public TimeSpan ReleaseSlack { get; init; } = TimeSpan.FromMinutes(30);
}
