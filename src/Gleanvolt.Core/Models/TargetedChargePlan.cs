using Gleanvolt.Core.Enums;

namespace Gleanvolt.Core.Models;

/// <summary>
/// How the requested energy is going to reach the car by the requested time — the output of
/// <see cref="Strategies.TargetedChargePlanner"/>, rebuilt from a refreshed forecast and the
/// <em>measured</em> delivery on every poll and never committed to.
///
/// <para>Pure data: the controller decides from it, the worker logs it, the web page puts it into
/// words and Home Assistant reports it.</para>
/// </summary>
/// <param name="Strategy">Which of the four cases this plan is (see <see cref="TargetedChargeStrategy"/>).</param>
/// <param name="Now">The instant the plan was built for — the telemetry reading it is anchored to.</param>
/// <param name="DepartBy">The departure the owner asked for.</param>
/// <param name="Deadline">
/// The finish line the plan actually works to: <paramref name="DepartBy"/> less the configured safety
/// margin.
/// </param>
/// <param name="RequiredEnergyWh">The energy the owner asked for.</param>
/// <param name="DeliveredEnergyWh">What the charger has measurably delivered since the request was activated.</param>
/// <param name="RemainingEnergyWh">What is still needed: the request less what has arrived, floored at zero.</param>
/// <param name="SolarEnergyWh">
/// Of what is still needed, the part forecast surplus is expected to cover <em>as a solar block</em> —
/// the car charging on the sun alone. Zero does not mean "no sun": see
/// <paramref name="ForecastSurplusWh"/>.
/// </param>
/// <param name="ForecastSurplusWh">
/// All the surplus the window is forecast to hold, after the house and the home battery's booking —
/// whether or not the car can charge on it unaided.
///
/// <para>The gap between this and <paramref name="SolarEnergyWh"/> is the whole weak-sun story. On
/// three phases the charger's floor is ~4.14 kW, so a window forecast to hold 6 kWh of surplus in
/// half-hours that never clear that line contributes <b>nothing</b> as a solar block: this figure is
/// 6 kWh and <paramref name="SolarEnergyWh"/> is zero. Reporting only the latter says "no sun" about
/// a sunny day, which is both wrong and the opposite of what the plan then does — it places the
/// import on exactly those hours so the roof pays for part of it.</para>
/// </param>
/// <param name="GridEnergyWh">
/// The part planned to come from the grid. An <b>upper bound</b> where the import is placed over
/// sub-floor surplus: the charger runs at its maximum there and whatever the roof supplies at that
/// moment comes off the meter, which this figure does not model.
/// </param>
/// <param name="CeilingEnergyWh">
/// The physical ceiling: the charger at maximum power for every remaining minute. The comparison
/// everything turns on — a need above this cannot be met however the plan is arranged.
/// </param>
/// <param name="ExpectedEnergyWh">
/// What will actually reach the car by <paramref name="Deadline"/> — the sum of the two shares above,
/// which equals <paramref name="RemainingEnergyWh"/> unless there isn't time.
/// </param>
/// <param name="ShortfallWh">How far <paramref name="ExpectedEnergyWh"/> falls short of what was asked. Zero unless <see cref="TargetedChargeStrategy.Maximum"/>.</param>
/// <param name="GridStart">
/// When the earliest grid block begins, or null when none is planned. The import is placed over the
/// sunniest part of the window, so it runs alongside whatever the roof is actually producing then;
/// with no forecast surplus in the window to run alongside, it starts at once.
/// </param>
/// <param name="FeasibleDeparture">
/// The departure time that <em>would</em> have covered the request, when this one cannot. Null when
/// the plan meets the target as asked.
/// </param>
/// <param name="SocFloorPercent">
/// The SOC the home battery must not be drawn below while the car takes solar — the same trajectory
/// floor the forecast-driven mode uses, clamped to the owner's configured minimum.
/// </param>
/// <param name="BatteryToFullWh">
/// What the home battery still needs to reach 100%, charge losses included. Reserved out of the
/// forecast before the car sees any of it: the pack keeps its priority in this mode too.
/// </param>
/// <param name="Blocks">
/// The plan as a timeline, chronological by start. Solar and grid blocks may overlap — see
/// <see cref="TargetedChargeBlock"/>. Consumed as-is by the timeline chart.
/// </param>
/// <param name="ForecastAsOf">When the forecast underlying this plan was fetched, or null when there was none.</param>
/// <param name="IsUsable">
/// Whether a usable forecast went into this plan. <b>False does not mean the plan is invalid</b> —
/// unlike <see cref="SolarDayPlan"/>, which degrades towards conservatism, this mode degrades towards
/// keeping its promise: every solar term goes to zero, the plan becomes grid-only, and the target is
/// still met. It is a caveat to report, not a fallback to take.
/// </param>
/// <param name="Reason">A short human-readable summary for logging and Home Assistant.</param>
public sealed record TargetedChargePlan(
    TargetedChargeStrategy Strategy,
    DateTimeOffset Now,
    DateTimeOffset DepartBy,
    DateTimeOffset Deadline,
    double RequiredEnergyWh,
    double DeliveredEnergyWh,
    double RemainingEnergyWh,
    double SolarEnergyWh,
    double ForecastSurplusWh,
    double GridEnergyWh,
    double CeilingEnergyWh,
    double ExpectedEnergyWh,
    double ShortfallWh,
    DateTimeOffset? GridStart,
    DateTimeOffset? FeasibleDeparture,
    double SocFloorPercent,
    double BatteryToFullWh,
    IReadOnlyList<TargetedChargeBlock> Blocks,
    DateTimeOffset? ForecastAsOf,
    bool IsUsable,
    string Reason)
{
    /// <summary>Whether the target has been met and the session can end.</summary>
    public bool IsComplete => Strategy == TargetedChargeStrategy.Complete;

    /// <summary>Whether the request cannot be met in the time left.</summary>
    public bool HasShortfall => ShortfallWh > 0;

    /// <summary>Whether any grid import is planned at all.</summary>
    public bool ImportsFromGrid => GridEnergyWh > 0 && GridStart is not null;

    /// <summary>
    /// Whether the window holds real surplus that the car cannot charge on unaided — sun the forecast
    /// sees, in half-hours too weak to clear the charger's floor. The case the import is placed over,
    /// and the one that must never be described as "no sun".
    /// </summary>
    public bool HasUnusableSurplus => ForecastSurplusWh > 0 && SolarEnergyWh <= 0;

    /// <summary>How long is left until the plan's finish line. Zero once it has passed.</summary>
    public TimeSpan TimeRemaining => Deadline > Now ? Deadline - Now : TimeSpan.Zero;

    /// <summary>The start of the first planned solar block, or null when none is planned.</summary>
    public DateTimeOffset? SolarStart => Blocks
        .Where(b => b.Source == TargetedChargeSource.Solar)
        .Select(b => (DateTimeOffset?)b.Start)
        .Min();

    /// <summary>The end of the last planned solar block, or null when none is planned.</summary>
    public DateTimeOffset? SolarEnd => Blocks
        .Where(b => b.Source == TargetedChargeSource.Solar)
        .Select(b => (DateTimeOffset?)b.End)
        .Max();

    /// <summary>
    /// Whether <paramref name="instant"/> falls inside the planned grid block. This is what arms the
    /// battery discharge hold: the pack must stay out of a charge the grid is paying for.
    /// </summary>
    public bool IsInGridBlock(DateTimeOffset instant) =>
        Blocks.Any(b => b.Source == TargetedChargeSource.Grid && b.Covers(instant));

    /// <summary>Whether <paramref name="instant"/> falls inside a planned solar block.</summary>
    public bool IsInSolarBlock(DateTimeOffset instant) =>
        Blocks.Any(b => b.Source == TargetedChargeSource.Solar && b.Covers(instant));
}
