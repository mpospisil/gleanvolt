using Microsoft.Extensions.Options;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Fast;
using Gleanvolt.Hosting.Forecasting;
using Gleanvolt.Hosting.Targeting;

namespace Gleanvolt.Hosting;

public sealed class PollingService : BackgroundService
{
    // How much battery discharge to tolerate before treating an armed hold as ineffective. See the
    // use site: a working hold still leaves a small standby trickle, so 0 is not the right line.
    /// <summary>
    /// How far the battery may sit below zero, with the hold armed, before it counts as a breach.
    ///
    /// <para>Raised from 150W on 2026-08-24. Measured on this inverter while a 10 kW EV charge ran on a
    /// working hold, the pack trickled at <b>165–460W</b> — inverter standby and control overhead, not
    /// load being served — and the grid carried the rest, exactly as intended. At 150W that fired on
    /// every poll, roughly a hundred times an hour, saying the command "may not be taking effect" about
    /// a hold that was doing its job. Over four such hours the pack gave up 0.40kWh in total.</para>
    ///
    /// <para>The cost of a false alarm here is not noise, it is misdirection: this warning sent three
    /// separate investigations after inverter firmware, register 0x7C and the power-target formula,
    /// none of which was at fault.</para>
    /// </summary>
    private const double ResidualDischargeWatts = 500;

    /// <summary>
    /// How long a breach must persist before it is worth saying out loud. A hold takes a moment to bite
    /// after arming, and a sudden PV collapse under load can pull the pack for a few seconds before the
    /// setpoint catches up; neither is the failure this warning is for.
    /// </summary>
    private static readonly TimeSpan HoldBreachDwell = TimeSpan.FromMinutes(2);

    /// <summary>When the current run of over-threshold discharge began, or null when the pack is behaving.</summary>
    private DateTimeOffset? _holdBreachSince;

    private readonly IEnergyStateReader _energyStateReader;
    private readonly ISolarForecastService _solarForecast;
    private readonly ChargingControlCoordinator _chargingControl;
    private readonly DayPlanProvider _dayPlan;
    private readonly TargetedChargeProvider _targetedCharge;
    private readonly FastChargeProvider _fastCharge;
    private readonly IChargeControlModeSelector _mode;
    private readonly IChargeActions _chargeActions;
    private readonly IBatteryHoldSelector _batteryHold;
    private readonly IBatteryDischargeControl _batteryDischargeControl;
    private readonly ChargeControlStatusHolder _statusHolder;
    private readonly ChargePowerConverter _power;
    private readonly bool _chargeControlDryRun;
    private readonly BatteryHoldOptions _batteryHoldOptions;
    private readonly ForecastChargeOptions _forecastOptions;
    private readonly ILogger<PollingService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pollInterval;

    // Whether a mode has armed the hold itself, as opposed to the owner's switch. Kept here rather
    // than in the selector so the manual switch stays exactly what the owner set.
    private bool _autoHold;

    // The fast mode's hold is the exception, and deliberately so (#119). It is armed *through* the
    // selector rather than beside it, because the HA switch publishes what is actually armed
    // (BatteryHoldActive) rather than what was asked for: a hold armed only in _autoHold shows ON in
    // Home Assistant while the selector still reads false, so the owner's OFF is a Set(false) that
    // changes nothing, fires no event, and is silently discarded. Arming through the selector keeps
    // its level matching what the owner is looking at, which is the whole of what makes the switch
    // work during a fast charge. This flag only records that the release is ours to perform.
    private bool _fastHold;

    // The mode the previous cycle ran under, so a selection can be noticed once instead of per poll.
    private ChargeControlMode _lastMode = ChargeControlMode.Off;

    public PollingService(
        IEnergyStateReader energyStateReader,
        ISolarForecastService solarForecast,
        ChargingControlCoordinator chargingControl,
        DayPlanProvider dayPlan,
        TargetedChargeProvider targetedCharge,
        FastChargeProvider fastCharge,
        IChargeControlModeSelector mode,
        IChargeActions chargeActions,
        IBatteryHoldSelector batteryHold,
        IBatteryDischargeControl batteryDischargeControl,
        ChargeControlStatusHolder statusHolder,
        ChargePowerConverter power,
        IOptions<ControllerOptions> controllerOptions,
        IOptions<ChargeControlOptions> chargeControlOptions,
        IOptions<BatteryHoldOptions> batteryHoldOptions,
        IOptions<ForecastChargeOptions> forecastOptions,
        ILogger<PollingService> logger,
        TimeProvider? timeProvider = null)
    {
        _energyStateReader = energyStateReader;
        _solarForecast = solarForecast;
        _chargingControl = chargingControl;
        _dayPlan = dayPlan;
        _targetedCharge = targetedCharge;
        _fastCharge = fastCharge;
        _mode = mode;
        _chargeActions = chargeActions;
        _batteryHold = batteryHold;
        _batteryDischargeControl = batteryDischargeControl;
        _statusHolder = statusHolder;
        _power = power;
        _chargeControlDryRun = chargeControlOptions.Value.DryRun;
        _batteryHoldOptions = batteryHoldOptions.Value;
        _forecastOptions = forecastOptions.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pollInterval = TimeSpan.FromSeconds(controllerOptions.Value.PollIntervalSeconds);
    }

    // Shutdown runs with a fresh token (ExecuteAsync's is already cancelled), so the pause write can
    // still reach the charger. Without this we'd leave the charger drawing at our last setpoint.
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _chargingControl.PauseOnShutdownAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Charge control at startup: mode {Mode} ({Writes}) — the charger is left as its owner set it "
            + "until a mode is selected. It can be changed at runtime.",
            _mode.Mode,
            _chargeControlDryRun ? "dry run — no writes" : "live — writing to the charger");

        _logger.LogInformation(
            "Battery discharge hold at startup: {Enabled}{Detail}",
            _batteryHoldOptions.Enabled ? "enabled" : "disabled (no inverter writes are possible)",
            _batteryHoldOptions.Enabled
                ? $", hold off — the battery charges and discharges normally until asked otherwise ({(_batteryHoldOptions.DryRun ? "dry run — no writes" : "live — writing to the inverter")})"
                : string.Empty);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var state = await _energyStateReader.ReadAsync(stoppingToken);

                _logger.LogInformation(
                    "SOC={BatterySocPercent}% BatteryPower={BatteryPowerWatts}W Solar={SolarPowerWatts}W Grid={GridPowerWatts}W EvCharger={EvChargerStatus} EvMode={EvChargeMode} EvCurrent={EvChargeCurrentAmps} EvPower={EvChargerPowerWatts}W",
                    state.BatterySocPercent,
                    state.BatteryPowerWatts,
                    state.SolarPowerWatts,
                    state.GridPowerWatts,
                    state.EvChargerStatus,
                    (object?)state.ChargeMode ?? "n/a",
                    state.ChargeCurrentAmps is null ? "n/a" : $"{state.ChargeCurrentAmps}A",
                    state.EvChargerPowerWatts);

                LogSolarActualVsForecast(state);

                // Built in every mode: the forecast accuracy tracking and today's energy totals are
                // worth having even when the plan isn't the thing driving the charger.
                //
                // Both are wrapped because planning is not control: a plan that cannot be built is a
                // reason to fall back, never a reason to stop driving the charger. Unwrapped, one
                // arithmetic slip in a planner aborts the cycle before a single decision is made --
                // which is how a finished target went on charging for 23 minutes on 2026-08-21.
                var plan = Planned("day plan", () => _dayPlan.Update(state, _chargingControl.LoanedTodayWh))
                    ?? SolarDayPlan.Unavailable(state.Timestamp, "the day plan could not be built this cycle");

                // Built whenever a request exists, in every mode, for the same reason the day plan is:
                // the delivered-energy meter has to keep running across a mode switch, or re-selecting
                // Targeted would restart the count and re-promise energy the car already has.
                var targetedPlan = Planned("targeted plan", () => _targetedCharge.Update(state));

                // Metered in every mode too, and for the same reason as the two above: the count has to
                // survive a mode switch, or leaving Fast and coming back would restart it and re-promise
                // energy the car already has.
                var fastCharge = Planned("fast charge progress", () => _fastCharge.Update(state));

                var mode = _mode.Mode;
                OnModeEntry(mode);

                ChargeControlCycleResult result;
                if (mode is ChargeControlMode.Solar or ChargeControlMode.Forecasted or ChargeControlMode.FastNoBattery
                    or ChargeControlMode.Targeted)
                {
                    result = await _chargingControl.RunCycleAsync(
                        state, mode, plan, stoppingToken, targetedPlan, fastCharge);
                }
                else
                {
                    // Off: stop controlling and leave the charger's current setpoint exactly as it is.
                    _chargingControl.ReleaseControl();
                    result = new ChargeControlCycleResult(ChargeControlState.Disabled, null, null, HoldingControl: false);
                }

                if (result.SessionComplete)
                {
                    // A mode that switches itself off has to leave the charger where the Off button
                    // would: stopped, not sitting in Fast at the pause current. That is one code path,
                    // and this is the other caller of it.
                    //
                    // The controller has already had the pause current written. Ending the mode here --
                    // before the hold is reconciled below -- means the release reaches the inverter in
                    // this same cycle rather than a poll later.
                    await _chargeActions.StopAsync($"{mode} (charging finished)", stoppingToken);
                    mode = ChargeControlMode.Off;
                    result = result with { State = ChargeControlState.Disabled, HoldingControl = false };
                }

                var hold = await ApplyBatteryHoldAsync(
                    state, mode, plan, targetedPlan, fastCharge, result.GridBridgeWatts > 0, stoppingToken);

                _statusHolder.Set(new ChargeControlStatus(
                    Mode: mode,
                    DryRun: _chargeControlDryRun,
                    HoldingControl: result.HoldingControl,
                    State: result.State,
                    SurplusWatts: result.SurplusWatts,
                    TargetCurrentAmps: result.TargetCurrentAmps,
                    ActiveCurrentAmps: state.ChargeCurrentAmps,
                    BatterySocPercent: state.BatterySocPercent,
                    ChargerStatus: state.EvChargerStatus,
                    CarConnected: state.EvChargerStatus.IsCarConnected(),
                    SolarPowerWatts: state.SolarPowerWatts,
                    ForecastSolarPowerWatts: ForecastSolarPowerWatts(state.Timestamp),
                    EvChargerPowerWatts: state.EvChargerPowerWatts,
                    EvChargingCurrentAmps: (int)Math.Round(_power.WattsToAmps(state.EvChargerPowerWatts)),
                    BatteryPowerWatts: state.BatteryPowerWatts,
                    GridPowerWatts: state.GridPowerWatts,
                    BatteryHoldEnabled: _batteryHoldOptions.Enabled,
                    BatteryHoldRequested: _batteryHold.Hold,
                    BatteryHoldActive: hold.Held,
                    BatteryHoldTargetWatts: hold.ActivePowerTargetWatts,
                    Plan: mode == ChargeControlMode.Forecasted ? plan : null,
                    TargetedPlan: mode == ChargeControlMode.Targeted ? targetedPlan : null,
                    // The same rule the two plans above follow, so a mode that is no longer driving
                    // cannot leave a stale amount on display. Note `mode`, not the mode at the top of
                    // the cycle: a fast charge that has just met its limit reads Off by here, and the
                    // amount it was working to belongs to the session that has just ended.
                    FastCharge: mode == ChargeControlMode.FastNoBattery ? fastCharge : null,
                    LoanPowerWatts: result.LoanPowerWatts,
                    SessionEnergyWh: _chargingControl.SessionEnergyWh,
                    LoanedTodayWh: _chargingControl.LoanedTodayWh,
                    TomorrowForecastWh: TomorrowForecastWattHours(state.Timestamp),
                    Timestamp: state.Timestamp,
                    // Carried out of the loop because it is unrecoverable afterwards: the mode has
                    // already been returned to Off above, so nothing downstream could otherwise tell
                    // "the car finished" from "somebody switched it off".
                    SessionCompleted: result.SessionComplete));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A single failed poll (e.g. dropped connection, Modbus timeout) must not
                // take the service down — log and retry on the next tick.
                _logger.LogWarning(ex, "Failed to poll SolaX devices; will retry on next interval.");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Runs a plan build, turning a failure into null and a warning rather than an aborted cycle. The
    /// controllers all have a defined answer for "no plan" — falling back, or pausing — and any of
    /// those beats leaving the charger at its last setpoint with nothing watching it.
    /// </summary>
    private T? Planned<T>(string what, Func<T?> build) where T : class
    {
        try
        {
            return build();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build the {What} this cycle; charge control continues without it.", what);
            return null;
        }
    }

    /// <summary>
    /// What happens once, at the moment a mode is selected, rather than on every poll: the warning
    /// below, and arming the fast mode's discharge hold.
    ///
    /// <para>The warning is the one thing worth saying at entry — the fast mode's promise is that the
    /// pack stays out of the charge, and with the hold feature switched off it cannot keep it. It still
    /// charges (a button that silently did nothing would be worse), but the inverter will serve the car
    /// from the battery, which is the opposite of the intent.</para>
    /// </summary>
    private void OnModeEntry(ChargeControlMode mode)
    {
        if (mode == _lastMode)
        {
            return;
        }

        _lastMode = mode;

        if (mode is ChargeControlMode.FastNoBattery or ChargeControlMode.Targeted && !_batteryHoldOptions.Enabled)
        {
            _logger.LogWarning(
                "{Mode} selected but BatteryHold:Enabled is false: charging at the maximum current anyway, "
                + "with no way to stop the inverter discharging the home battery into the car.",
                mode);
        }
    }

    /// <summary>
    /// Arms the discharge hold for a fast charge that is actually charging — keeping the pack out of
    /// the fastest charge the site can deliver is the mode's entire reason for existing, so it is armed
    /// by default and the owner never has to remember to.
    ///
    /// <para>Through <see cref="IBatteryHoldSelector"/> rather than beside it, which is the whole point:
    /// the Home Assistant switch publishes what is actually armed, so a hold armed only inside this
    /// service shows ON there while the selector still reads false — and the owner's OFF is then a
    /// <c>Set(false)</c> that changes nothing and raises no event. Arming through the switch keeps its
    /// level matching what is on screen, so turning it off is a real transition that really releases
    /// the hold. That is the §7 requirement of #119: armed by default, and the switch means something.</para>
    ///
    /// <para><b>On charging, not on mode entry</b> — the change #122 made, and it is not a nicety. A
    /// charge deferred to 04:12 is selected at 22:00, so arming at entry would lock the pack out of
    /// serving the house for six hours in which nothing is being charged at all. The owner would find a
    /// battery that sat full while the grid carried the evening, and nothing in the log saying why.</para>
    ///
    /// <para>Idempotent, and that matters just as much: it fires on the transition only, so it can run
    /// every cycle without re-arming a hold the owner has deliberately switched off mid-charge.</para>
    /// </summary>
    private void ArmFastHoldWhenCharging(ChargeControlMode mode, bool waiting)
    {
        if (mode != ChargeControlMode.FastNoBattery || waiting || _fastHold || !_batteryHoldOptions.Enabled)
        {
            return;
        }

        _fastHold = true;
        _batteryHold.Set(true, "the FastNoBattery mode charging");
    }

    /// <summary>
    /// Releases a fast charge's hold once that mode is no longer driving — <b>whatever</b> took it
    /// there: the owner pressed Off on any of the three surfaces, the car reached its own charge limit,
    /// the requested amount was delivered, or the car was unplugged.
    ///
    /// <para><b>Keyed on the mode, not on the ending.</b> There are half a dozen ways a fast charge
    /// stops and every one of them ends with this mode no longer selected — the session-complete path
    /// in the loop above sets it to Off itself, and the stop actions go through the mode selector. One
    /// condition covers all of them; a list of endings is how the seventh one gets missed, and the
    /// missed one leaves an armed hold on an inverter with nothing charging. It is the arrangement
    /// <see cref="ChargeControlMode.Targeted"/>'s hold already uses, one branch down.</para>
    ///
    /// <para>A hold the owner had switched on <em>before</em> the fast charge is released along with it.
    /// That is deliberate: the alternative is restoring a remembered prior value, which a restart loses
    /// anyway, and which leaves the pack locked out of serving the house with nothing charging. It is
    /// said out loud in the README.</para>
    /// </summary>
    private void ReleaseFastHoldIfEnded(ChargeControlMode mode)
    {
        if (!_fastHold || mode == ChargeControlMode.FastNoBattery)
        {
            return;
        }

        _fastHold = false;
        _batteryHold.Set(false, "the FastNoBattery mode ending");
    }

    /// <summary>
    /// Reconciles the inverter with the battery-hold switch. The command is not a stored setting and
    /// cannot be read back, so there is nothing to compare against the device — the control writes on
    /// each transition, when the target has moved enough to matter, and to renew before the armed
    /// command lapses. A failure here must not take the poll down: the hold is a preservation feature,
    /// and losing it costs battery charge, not safety.
    /// </summary>
    private async Task<BatteryHoldState> ApplyBatteryHoldAsync(
        EnergyState state,
        ChargeControlMode mode,
        SolarDayPlan plan,
        TargetedChargePlan? targetedPlan,
        FastChargeProgress? fastCharge,
        bool gridBridging,
        CancellationToken cancellationToken)
    {
        if (!_batteryHoldOptions.Enabled)
        {
            return default;
        }

        // Evaluated first, and not inlined into the `||` below: that would short-circuit whenever the
        // switch is on, and the fast mode's release runs *inside* AutoHold -- through the switch. A
        // mode ending while its own hold was armed would then never release it, which is the one
        // outcome this whole arrangement exists to prevent.
        var auto = AutoHold(state, mode, plan, targetedPlan, fastCharge, gridBridging);
        var hold = _batteryHold.Hold || auto;
        var targetWatts = BatteryDischargeHoldStrategy.ActivePowerTargetWatts(state);

        BatteryHoldState result;
        try
        {
            result = await _batteryDischargeControl.ApplyAsync(hold, targetWatts, state.Timestamp, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to apply the battery discharge hold; will retry on next interval.");
            return new BatteryHoldState(Held: false, null, null, Wrote: false);
        }

        // The one observable check available: the command register can't be read back, but the battery
        // itself can. If it is discharging while we believe the hold is armed, the hold isn't working
        // on this firmware — which is exactly what the verification phase needs to surface. Skipped in
        // dry-run, where nothing was written and a discharging battery is the expected outcome.
        //
        // Both a deadband and a dwell, and it took a bad diagnosis to learn why: see
        // ResidualDischargeWatts and HoldBreachDwell. A working hold trickles, and it takes a moment to
        // bite; only a sustained, substantial discharge is evidence of anything.
        var breaching = result.Held && !_batteryHoldOptions.DryRun && state.BatteryPowerWatts < -ResidualDischargeWatts;
        if (!breaching)
        {
            _holdBreachSince = null;
        }
        else
        {
            _holdBreachSince ??= state.Timestamp;

            if (state.Timestamp - _holdBreachSince >= HoldBreachDwell)
            {
                _logger.LogWarning(
                    "Battery discharge hold is armed (target {TargetWatts}W) but the battery has been discharging at "
                    + "{BatteryPowerWatts}W for {BreachMinutes:F0} min. The power-control command may not be taking "
                    + "effect on this firmware.",
                    result.ActivePowerTargetWatts,
                    state.BatteryPowerWatts,
                    (state.Timestamp - _holdBreachSince.Value).TotalMinutes);
            }
        }

        return result;
    }

    /// <summary>
    /// Whether the selected mode wants the discharge hold armed right now, independently of the owner's
    /// manual switch — which is OR-ed with this, so a hold the owner asked for is never released by one
    /// of the modes answered here.
    ///
    /// <para><see cref="ChargeControlMode.FastNoBattery"/> is <b>not</b> here, and that is the change
    /// #119 made. It used to return true unconditionally, which made the mode's hold a floor: flipping
    /// the switch off during a fast charge moved the switch in Home Assistant, logged nothing, and left
    /// the hold armed. Its hold is now armed through the selector on mode entry and released by
    /// <see cref="ReleaseFastHoldIfEnded"/> when the mode stops driving, so for as long as it runs the
    /// owner's switch is the only input — off means off.</para>
    ///
    /// <para><see cref="ChargeControlMode.Forecasted"/> wants it once SOC has reached the floor the plan
    /// requires for a 100% battery by the deadline, so an estimate error cannot dig below it — the grid
    /// covers the gap instead of the pack. Released again only after SOC has recovered a margin above the
    /// floor, so the hold doesn't chatter around the line.</para>
    /// </summary>
    private bool AutoHold(
        EnergyState state,
        ChargeControlMode mode,
        SolarDayPlan plan,
        TargetedChargePlan? targetedPlan,
        FastChargeProgress? fastCharge,
        bool gridBridging)
    {
        ReleaseFastHoldIfEnded(mode);

        if (mode == ChargeControlMode.FastNoBattery)
        {
            // A deferred charge (#122) is selected hours before it runs, and holding the pack out of a
            // charge that has not started is six hours of the house on the grid for nothing.
            ArmFastHoldWhenCharging(mode, waiting: fastCharge?.Plan?.IsWaitingAt(state.Timestamp) == true);

            // Nothing to add beyond that: the mode's own hold is on the switch, so returning true here
            // would put it back to being a floor and take the switch's authority away again.
            //
            // An auto-hold from whatever ran before this is dropped, though. It is not this mode's, and
            // leaving the flag set would tell the forecast mode's hysteresis it was already armed if
            // that mode came back later.
            if (_autoHold)
            {
                _logger.LogInformation(
                    "The previous mode's automatic battery discharge hold is superseded by the {Mode} mode's own.",
                    mode);
                _autoHold = false;
            }

            return false;
        }

        if (mode == ChargeControlMode.Targeted)
        {
            // Scoped to the parts of the cycle that import, unlike the fast mode's blanket hold: the
            // rest of the time the car is running on surplus, and holding the pack there would only
            // push the house onto the grid for nothing.
            //
            // Two of them, not one. The planned grid block is the obvious case. The other is the live
            // grid bridge, which is easy to miss and expensive to get wrong: the controller has just
            // commanded 6 A against a surplus that cannot carry it, so without the hold the shortfall
            // comes out of the pack and the "grid" bridge is quietly a battery loan -- the one thing
            // this mode promises never to do.
            var importing = targetedPlan?.IsInGridBlock(state.Timestamp) == true || gridBridging;

            if (importing != _autoHold)
            {
                _logger.LogInformation(
                    "Battery discharge hold {Action} automatically: the {Mode} plan's {Source} {State}.",
                    importing ? "armed" : "released",
                    mode,
                    gridBridging ? "grid bridge" : "grid top-up",
                    importing ? "has started" : "is not running");
            }

            _autoHold = importing;
            return importing;
        }

        if (mode != ChargeControlMode.Forecasted || !_forecastOptions.AutoArmBatteryHoldAtFloor || !plan.IsUsable)
        {
            if (_autoHold)
            {
                _logger.LogInformation("Battery discharge hold released automatically: mode is now {Mode}.", mode);
            }

            _autoHold = false;
            return false;
        }

        var soc = state.BatterySocPercent;
        var release = plan.RequiredSocFloorPercent + _forecastOptions.HoldReleaseMarginPercent;
        var armed = _autoHold ? soc < release : soc <= plan.RequiredSocFloorPercent;

        if (armed != _autoHold)
        {
            _logger.LogInformation(
                "Battery discharge hold {Action} automatically: SOC {Soc:F0}% against the plan's {Floor:F0}% floor.",
                armed ? "armed" : "released",
                soc,
                plan.RequiredSocFloorPercent);
        }

        _autoHold = armed;
        return armed;
    }

    /// <summary>
    /// What the forecast expected the roof to be making right now, to sit beside the measured figure.
    /// Zero rather than null when nothing covers this instant — no forecast fetched, the provider
    /// down, or past the horizon — so the pair always charts. The session store keeps its own
    /// nullable copy of the same lookup, because there "no forecast" and "forecast said zero" are
    /// different facts worth telling apart after the event.
    /// </summary>
    private double ForecastSolarPowerWatts(DateTimeOffset at) =>
        Math.Max(0, _solarForecast.GetForecastForToday()?.ExpectedPowerWattsAt(at) ?? 0);

    /// <summary>
    /// Tomorrow's forecast production, purely so a shortfall today can be read with the context of
    /// whether waiting a day is worth it. Free: it is in the same Solcast response as today's.
    /// </summary>
    private double? TomorrowForecastWattHours(DateTimeOffset now)
    {
        var zone = _timeProvider.LocalTimeZone;
        var localMidnight = TimeZoneInfo.ConvertTime(now, zone).Date.AddDays(1);
        var start = new DateTimeOffset(localMidnight, zone.GetUtcOffset(localMidnight));

        var forecast = _solarForecast.GetForecast(start, start.AddDays(1));
        return forecast?.Periods.Count > 0 ? forecast.ExpectedEnergyWattHours : null;
    }

    // Logs actual solar generation against what Solcast forecast for this moment, plus their
    // delta (actual minus forecast: positive = producing more than predicted). The forecast comes
    // from the locally cached forecast and is null until the first successful fetch completes;
    // the day's overall shape is logged once per refresh inside the forecast service, not here.
    private void LogSolarActualVsForecast(EnergyState state)
    {
        var forecastNow = _solarForecast.GetForecastForToday()?.ExpectedPowerWattsAt(state.Timestamp);

        if (forecastNow is null)
        {
            _logger.LogInformation(
                "Solar: Actual={SolarPowerWatts:F0}W Forecast=n/a Delta=n/a",
                state.SolarPowerWatts);
            return;
        }

        _logger.LogInformation(
            "Solar: Actual={SolarPowerWatts:F0}W Forecast={ForecastPowerWatts:F0}W Delta={SolarDeltaWatts:F0}W",
            state.SolarPowerWatts,
            forecastNow.Value,
            state.SolarPowerWatts - forecastNow.Value);
    }
}
