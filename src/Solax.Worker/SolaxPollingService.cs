using Microsoft.Extensions.Options;
using Solax.Core.Enums;
using Solax.Core.Interfaces;
using Solax.Core.Models;
using Solax.Core.Strategies;
using Solax.Worker.Configuration;
using Solax.Worker.Forecasting;

namespace Solax.Worker;

public sealed class SolaxPollingService : BackgroundService
{
    // How much battery discharge to tolerate before treating an armed hold as ineffective. See the
    // use site: a working hold still leaves a small standby trickle, so 0 is not the right line.
    private const double ResidualDischargeWatts = 150;

    private readonly IEnergyStateReader _energyStateReader;
    private readonly ISolarForecastService _solarForecast;
    private readonly ChargingControlCoordinator _chargingControl;
    private readonly DayPlanProvider _dayPlan;
    private readonly IChargeControlModeSelector _mode;
    private readonly IBatteryHoldSelector _batteryHold;
    private readonly IBatteryDischargeControl _batteryDischargeControl;
    private readonly ChargeControlStatusHolder _statusHolder;
    private readonly ChargePowerConverter _power;
    private readonly bool _chargeControlDryRun;
    private readonly BatteryHoldOptions _batteryHoldOptions;
    private readonly ForecastChargeOptions _forecastOptions;
    private readonly ILogger<SolaxPollingService> _logger;
    private readonly TimeSpan _pollInterval;

    // Whether a mode has armed the hold itself, as opposed to the owner's switch. Kept here rather
    // than in the selector so the manual switch stays exactly what the owner set.
    private bool _autoHold;

    // The mode the previous cycle ran under, so a selection can be noticed once instead of per poll.
    private ChargeControlMode _lastMode = ChargeControlMode.Off;

    public SolaxPollingService(
        IEnergyStateReader energyStateReader,
        ISolarForecastService solarForecast,
        ChargingControlCoordinator chargingControl,
        DayPlanProvider dayPlan,
        IChargeControlModeSelector mode,
        IBatteryHoldSelector batteryHold,
        IBatteryDischargeControl batteryDischargeControl,
        ChargeControlStatusHolder statusHolder,
        ChargePowerConverter power,
        IOptions<SolaxOptions> options,
        IOptions<ChargeControlOptions> chargeControlOptions,
        IOptions<BatteryHoldOptions> batteryHoldOptions,
        IOptions<ForecastChargeOptions> forecastOptions,
        ILogger<SolaxPollingService> logger)
    {
        _energyStateReader = energyStateReader;
        _solarForecast = solarForecast;
        _chargingControl = chargingControl;
        _dayPlan = dayPlan;
        _mode = mode;
        _batteryHold = batteryHold;
        _batteryDischargeControl = batteryDischargeControl;
        _statusHolder = statusHolder;
        _power = power;
        _chargeControlDryRun = chargeControlOptions.Value.DryRun;
        _batteryHoldOptions = batteryHoldOptions.Value;
        _forecastOptions = forecastOptions.Value;
        _logger = logger;
        _pollInterval = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
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
                var plan = _dayPlan.Update(state, _chargingControl.LoanedTodayWh);

                var mode = _mode.Mode;
                WarnOnModeEntry(mode);

                ChargeControlCycleResult result;
                if (mode is ChargeControlMode.Solar or ChargeControlMode.Forecasted or ChargeControlMode.FastNoBattery)
                {
                    result = await _chargingControl.RunCycleAsync(state, mode, plan, stoppingToken);
                }
                else
                {
                    // Off: stop controlling and leave the charger's current setpoint exactly as it is.
                    _chargingControl.ReleaseControl();
                    result = new ChargeControlCycleResult(ChargeControlState.Disabled, null, null, HoldingControl: false);
                }

                if (result.SessionComplete)
                {
                    // The controller has already had the pause current written. Ending the mode here --
                    // before the hold is reconciled below -- means the release reaches the inverter in
                    // this same cycle rather than a poll later.
                    _mode.Set(ChargeControlMode.Off, $"{mode} (charging finished)");
                    mode = ChargeControlMode.Off;
                    result = result with { State = ChargeControlState.Disabled, HoldingControl = false };
                }

                var hold = await ApplyBatteryHoldAsync(state, mode, plan, stoppingToken);

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
                    EvChargerPowerWatts: state.EvChargerPowerWatts,
                    EvChargingCurrentAmps: (int)Math.Round(_power.WattsToAmps(state.EvChargerPowerWatts)),
                    BatteryPowerWatts: state.BatteryPowerWatts,
                    GridPowerWatts: state.GridPowerWatts,
                    BatteryHoldEnabled: _batteryHoldOptions.Enabled,
                    BatteryHoldRequested: _batteryHold.Hold,
                    BatteryHoldActive: hold.Held,
                    BatteryHoldTargetWatts: hold.ActivePowerTargetWatts,
                    Plan: mode == ChargeControlMode.Forecasted ? plan : null,
                    LoanPowerWatts: result.LoanPowerWatts,
                    SessionEnergyWh: _chargingControl.SessionEnergyWh,
                    LoanedTodayWh: _chargingControl.LoanedTodayWh,
                    TomorrowForecastWh: TomorrowForecastWattHours(state.Timestamp),
                    Timestamp: state.Timestamp));
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
    /// The one thing worth saying at the moment a mode is selected rather than every poll: the fast
    /// mode's promise is that the pack stays out of the charge, and with the hold feature switched off
    /// it cannot keep it. It still charges — a select option that silently did nothing would be worse —
    /// but the inverter will serve the car from the battery, which is the opposite of the intent.
    /// </summary>
    private void WarnOnModeEntry(ChargeControlMode mode)
    {
        if (mode == _lastMode)
        {
            return;
        }

        _lastMode = mode;

        if (mode == ChargeControlMode.FastNoBattery && !_batteryHoldOptions.Enabled)
        {
            _logger.LogWarning(
                "{Mode} selected but BatteryHold:Enabled is false: charging at the maximum current anyway, "
                + "with no way to stop the inverter discharging the home battery into the car.",
                mode);
        }
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
        CancellationToken cancellationToken)
    {
        if (!_batteryHoldOptions.Enabled)
        {
            return default;
        }

        var hold = _batteryHold.Hold || AutoHold(state, mode, plan);
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
        // The deadband matters: measured on this inverter, an armed hold leaves a residual 50-65W
        // trickle out of the battery (inverter standby draw, not load being served). Warning on any
        // negative value fires every poll and drowns out the signal it exists to give.
        if (result.Held && !_batteryHoldOptions.DryRun && state.BatteryPowerWatts < -ResidualDischargeWatts)
        {
            _logger.LogWarning(
                "Battery discharge hold is armed (target {TargetWatts}W) but the battery is discharging at {BatteryPowerWatts}W. "
                + "The power-control command may not be taking effect on this firmware.",
                result.ActivePowerTargetWatts,
                state.BatteryPowerWatts);
        }

        return result;
    }

    /// <summary>
    /// Whether the selected mode wants the discharge hold armed right now, independently of the owner's
    /// manual switch — which is OR-ed with this and always wins, so a hold the owner asked for is never
    /// released by a mode.
    ///
    /// <para><see cref="ChargeControlMode.FastNoBattery"/> wants it unconditionally: keeping the pack out
    /// of the fastest charge the site can deliver is the mode's entire reason for existing.</para>
    ///
    /// <para><see cref="ChargeControlMode.Forecasted"/> wants it once SOC has reached the floor the plan
    /// requires for a 100% battery by the deadline, so an estimate error cannot dig below it — the grid
    /// covers the gap instead of the pack. Released again only after SOC has recovered a margin above the
    /// floor, so the hold doesn't chatter around the line.</para>
    /// </summary>
    private bool AutoHold(EnergyState state, ChargeControlMode mode, SolarDayPlan plan)
    {
        if (mode == ChargeControlMode.FastNoBattery)
        {
            if (!_autoHold)
            {
                _logger.LogInformation("Battery discharge hold armed automatically for the {Mode} mode.", mode);
                _autoHold = true;
            }

            return true;
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
    /// Tomorrow's forecast production, purely so a shortfall today can be read with the context of
    /// whether waiting a day is worth it. Free: it is in the same Solcast response as today's.
    /// </summary>
    private double? TomorrowForecastWattHours(DateTimeOffset now)
    {
        var localMidnight = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).Date.AddDays(1);
        var start = new DateTimeOffset(localMidnight, TimeZoneInfo.Local.GetUtcOffset(localMidnight));

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
