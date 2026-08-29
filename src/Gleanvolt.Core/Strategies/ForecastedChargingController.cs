using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Strategies;

/// <summary>
/// Decides the charging current from the forecast-driven day plan rather than from a fixed
/// battery-full gate: the car may charge as soon as the plan shows enough remaining sun for the home
/// battery to still reach 100% by the evening deadline (see <see cref="SolarDayPlanner"/>).
///
/// <para>Like the live-solar controller it only ever modulates the <em>current</em>, and only while the
/// charger's own use-mode is <see cref="EvChargerMode.Fast"/> — which starting the mode wrote once and
/// which nothing here re-asserts. What it adds:</para>
/// <list type="bullet">
/// <item><description>a <b>moving SOC floor</b> from the plan instead of a fixed 95% gate, with its own
/// resume margin, a guard band that hands the battery part of the surplus while it recovers, and a
/// lighter touch when the floor in force is the owner's clamp rather than the forecast's
/// trajectory;</description></item>
/// <item><description>a bounded <b>battery loan</b> that bridges a real-but-insufficient surplus up to
/// the charger's 6 A floor, repaid later from sun that would otherwise have been exported;</description></item>
/// <item><description><b>dwell timers</b>, so a passing cloud can't start and stop the session every few
/// minutes;</description></item>
/// <item><description>a <b>final guard</b> before the deadline, and a per-session energy ceiling.</description></item>
/// </list>
///
/// <para>Whenever the plan is missing, stale or untrusted it delegates to the live-solar controller —
/// the fallback is the whole point: an absent forecast must never be read as optimistic headroom.</para>
///
/// <para>Pure and side-effect free. All cross-cycle state (dwell, session energy, energy loaned today)
/// arrives through <see cref="ChargingControlInput"/>.</para>
/// </summary>
public sealed class ForecastedChargingController : IChargingController
{
    // SOC readings are whole percent; anything within this of 100 counts as full.
    private const double FullSocEpsilon = 0.5;

    private readonly ChargePowerConverter _power;
    private readonly IChargingController _fallback;
    private readonly ForecastedChargingOptions _options;
    private readonly IForecastRuntimeSettings? _runtime;
    private readonly int _minAmps;
    private readonly int _maxAmps;

    /// <param name="powerConverter">Phase-aware watts↔amps conversion; the source of the minimum charge power.</param>
    /// <param name="fallback">
    /// The controller to defer to when no usable plan exists — in practice the live-solar controller,
    /// so "no forecast" degrades to the conservative behaviour that shipped before this mode.
    /// </param>
    /// <param name="options">Decision parameters.</param>
    /// <param name="runtime">
    /// Runtime-settable overrides (the Home Assistant number entities). When null, the values in
    /// <paramref name="options"/> are used as-is — which is what unit tests want.
    /// </param>
    public ForecastedChargingController(
        ChargePowerConverter powerConverter,
        IChargingController fallback,
        ForecastedChargingOptions options,
        IForecastRuntimeSettings? runtime = null)
    {
        _power = powerConverter ?? throw new ArgumentNullException(nameof(powerConverter));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _runtime = runtime;

        if (options.CurrentStepAmps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.CurrentStepAmps, "Current step must be positive.");
        }

        // Constrained to what the hardware accepts, so an illegal setpoint can never even be targeted.
        _minAmps = Math.Clamp(options.MinChargingCurrentAmps, EvChargerLimits.MinCurrentAmps, EvChargerLimits.MaxCurrentAmps);
        _maxAmps = Math.Clamp(options.MaxChargingCurrentAmps, _minAmps, EvChargerLimits.MaxCurrentAmps);
    }

    /// <summary>The charger's minimum viable power — the line between shoulder and plateau production.</summary>
    public double MinChargePowerWatts => _power.AmpsToWatts(_minAmps);

    /// <summary>How far above the plan's floor the SOC must have recovered before a pause may end.</summary>
    private double FloorResumeMarginPercent =>
        Math.Max(0, _runtime?.FloorResumeMarginPercent ?? _options.FloorResumeMarginPercent);

    public ChargingControlDecision Decide(ChargingControlInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Precondition, unchanged from live-solar control: the charger is in Fast (starting the mode
        // wrote it) and we only modulate the current under it.
        if (ChargerOwnership.NotOurs(input) is { } notOurs)
        {
            return notOurs;
        }

        var plan = input.Plan;
        if (plan is null || !plan.IsUsable)
        {
            var fallback = _fallback.Decide(input);
            return fallback with { Reason = $"No usable day plan ({plan?.Reason ?? "none computed"}); live-solar fallback: {fallback.Reason}" };
        }

        var state = input.State;
        var soc = state.BatterySocPercent;

        // --- Hard stops: these ignore the dwell timers, because "keep the session tidy" must never
        // outrank the battery's evening guarantee or the car's energy ceiling. ---

        var sessionTargetWh = _runtime?.SessionEnergyTargetWh ?? _options.SessionEnergyTargetWh;
        if (sessionTargetWh > 0 && input.SessionEnergyWh >= sessionTargetWh)
        {
            return Pause($"Session target reached: {input.SessionEnergyWh / 1000:F1}kWh of {sessionTargetWh / 1000:F1}kWh delivered.");
        }

        if (state.Timestamp >= plan.Deadline - _options.FinalGuardBefore
            && state.Timestamp <= plan.Deadline
            && soc < 100 - FullSocEpsilon)
        {
            return Pause($"Final guard before {plan.Deadline.LocalDateTime:HH:mm}: battery at {soc:F0}%, everything goes to it.");
        }

        // The floor gate, asymmetric in the same way as the surplus threshold below: charging continues
        // down to the floor, but a paused session may only come back once the battery has recovered a
        // margin above it. Without that margin the gate turns on a single percent of SOC — the inverter
        // reports whole percent — and the car cycles on and off for the whole morning.
        var floorToClear = input.Charging
            ? plan.RequiredSocFloorPercent
            : Math.Min(100, plan.RequiredSocFloorPercent + FloorResumeMarginPercent);

        if (soc < floorToClear)
        {
            var reason = input.Charging
                ? $"Battery {soc:F0}% below the {plan.RequiredSocFloorPercent:F0}% floor the forecast requires for 100% by {plan.Deadline.LocalDateTime:HH:mm}."
                : $"Battery {soc:F0}% has not recovered to the {floorToClear:F0}% needed to restart (floor {plan.RequiredSocFloorPercent:F0}% + {FloorResumeMarginPercent:F0}% margin).";

            // Which floor was breached decides how hard the stop is. Falling through the *trajectory*
            // costs the evening 100% and ends the session at once. Falling through the owner's *clamp*
            // while the trajectory is still far below — the normal case on a sunny morning — costs only
            // a preference the rest of the day can make good, so it goes through the dwell timer like
            // any other soft reason rather than cutting a running session short on a 1% wobble.
            return soc < plan.TrajectorySocFloorPercent ? Pause(reason) : SoftPause(input, reason);
        }

        if (plan.Outlook == DayOutlook.NoChargeToday)
        {
            return Pause($"No chargeable window today; the battery has priority. {plan.Reason}");
        }

        // --- Soft reasons: subject to the dwell timers. ---

        if (!input.Charging && input.TimeInCurrentState < _options.MinPauseTime)
        {
            return Pause($"Paused {input.TimeInCurrentState.TotalMinutes:F0}min of the {_options.MinPauseTime.TotalMinutes:F0}min minimum before restarting.");
        }

        if (plan.FeasibleEvEnergyWh <= 0)
        {
            return SoftPause(input, $"No deliverable budget left today ({plan.Reason}).");
        }

        var surplusWatts = input.SurplusWatts;

        // The guard band: the resume margin's width, measured up from the floor. Inside it the battery
        // is still recovering, so it gets first refusal on part of the surplus — and lends nothing,
        // since a loan and a reserve at the same SOC would just cancel out. Only a running session can
        // be in here at all; a paused one is held below by the resume margin itself.
        var inGuardBand = soc < plan.RequiredSocFloorPercent + FloorResumeMarginPercent;
        var reserveWatts = inGuardBand ? Math.Max(0, _options.FloorGuardReserveWatts) : 0;

        var loanWatts = inGuardBand ? 0 : LoanWatts(input, plan, surplusWatts);
        var availableWatts = Math.Max(0, surplusWatts + loanWatts - reserveWatts);

        var reserveText = reserveWatts > 0
            ? $" − {reserveWatts:F0}W reserved for the battery inside the {FloorResumeMarginPercent:F0}% guard band"
            : string.Empty;

        // Asymmetric threshold: keep charging down to the minimum, but only (re)start a hysteresis
        // margin above it.
        var startThresholdWatts = input.Charging ? MinChargePowerWatts : MinChargePowerWatts + _options.ResumeHysteresisWatts;
        if (availableWatts < startThresholdWatts)
        {
            return SoftPause(
                input,
                $"Available {availableWatts:F0}W (surplus {surplusWatts:F0}W + loan {loanWatts:F0}W{reserveText}) below the {(input.Charging ? "minimum" : "start")} threshold {startThresholdWatts:F0}W.");
        }

        var targetAmps = ToHardwareCurrent(availableWatts);
        if (targetAmps < _minAmps)
        {
            return SoftPause(input, $"Available {availableWatts:F0}W quantises below the minimum {_minAmps}A.");
        }

        var loanText = loanWatts > 0
            ? $" (loan +{loanWatts:F0}W bridging {surplusWatts:F0}W to {MinChargePowerWatts:F0}W)"
            : reserveText;

        return new ChargingControlDecision(
            ChargingControlAction.Charge,
            targetAmps,
            $"Plan: {plan.FeasibleEvEnergyWh / 1000:F1}kWh budget, floor {plan.RequiredSocFloorPercent:F0}%, SOC {soc:F0}%. Available {availableWatts:F0}W -> charge at {targetAmps}A{loanText}.",
            loanWatts);
    }

    /// <summary>
    /// How much the battery may lend right now. The loan exists for one reason: on three phases the
    /// charger's 6 A floor is ~4.2 kW, so a perfectly good 3 kW surplus charges nothing at all and is
    /// exported. Lending the difference converts that spill into charge, and the forecast is what makes
    /// it safe to repay. It is therefore sized to reach exactly the floor — never more — and refused
    /// unless the surplus is already real.
    /// </summary>
    private double LoanWatts(ChargingControlInput input, SolarDayPlan plan, double surplusWatts)
    {
        if (!_options.EnableBatteryLoan)
        {
            return 0;
        }

        // A shortfall day has, by definition, no capacity to repay: every watt is already spoken for by
        // the house and the battery's own evening target.
        if (plan.HasShortfall)
        {
            return 0;
        }

        if (input.LoanedTodayWh >= _options.MaxDailyLoanWh)
        {
            return 0;
        }

        if (input.State.BatterySocPercent <= plan.RequiredSocFloorPercent + _options.LoanSocMarginPercent)
        {
            return 0;
        }

        // Only a genuine surplus gets topped up. Below this line the sun isn't really contributing and
        // the "loan" would just be running the car off the house battery.
        if (surplusWatts < _options.MinBridgeSurplusWatts)
        {
            return 0;
        }

        var gapWatts = MinChargePowerWatts - surplusWatts;
        if (gapWatts <= 0)
        {
            // The sun already clears the floor; there is nothing to bridge. Deliberately no top-up
            // beyond it — the loan is a bridge, not a booster.
            return 0;
        }

        return Math.Min(gapWatts, _options.MaxLoanPowerWatts);
    }

    /// <summary>
    /// A pause for a soft reason (budget, surplus dip). While the minimum run time is unexpired the
    /// session is instead held at the minimum current: stopping and restarting a charge costs a
    /// contactor cycle and a vehicle wake, and a few minutes at 6 A is the cheaper trade.
    /// </summary>
    private ChargingControlDecision SoftPause(ChargingControlInput input, string reason)
    {
        if (input.Charging && input.TimeInCurrentState < _options.MinRunTime)
        {
            return new ChargingControlDecision(
                ChargingControlAction.Charge,
                _minAmps,
                $"{reason} Holding at {_minAmps}A for the {_options.MinRunTime.TotalMinutes:F0}min minimum run time.");
        }

        return Pause(reason);
    }

    private static ChargingControlDecision Pause(string reason) => new(ChargingControlAction.Pause, null, reason);

    // Whole-amp setpoint the charger accepts: convert (phase-aware), floor to the step, clamp to max.
    private int ToHardwareCurrent(double availableWatts)
    {
        var rawAmps = _power.WattsToAmps(availableWatts);
        var steppedAmps = (int)Math.Floor(rawAmps / _options.CurrentStepAmps) * _options.CurrentStepAmps;
        return Math.Min(steppedAmps, _maxAmps);
    }
}
