using Microsoft.Extensions.Options;
using Solax.Core.Enums;
using Solax.Core.Interfaces;
using Solax.Core.Models;
using Solax.Core.Strategies;
using Solax.Worker.Configuration;

namespace Solax.Worker;

public sealed class SolaxPollingService : BackgroundService
{
    private readonly IEnergyStateReader _energyStateReader;
    private readonly ISolarForecastService _solarForecast;
    private readonly ChargingControlCoordinator _chargingControl;
    private readonly IChargeControlModeSelector _mode;
    private readonly IBatteryHoldSelector _batteryHold;
    private readonly IBatteryDischargeControl _batteryDischargeControl;
    private readonly ChargeControlStatusHolder _statusHolder;
    private readonly ChargePowerConverter _power;
    private readonly bool _chargeControlDryRun;
    private readonly BatteryHoldOptions _batteryHoldOptions;
    private readonly ILogger<SolaxPollingService> _logger;
    private readonly TimeSpan _pollInterval;

    public SolaxPollingService(
        IEnergyStateReader energyStateReader,
        ISolarForecastService solarForecast,
        ChargingControlCoordinator chargingControl,
        IChargeControlModeSelector mode,
        IBatteryHoldSelector batteryHold,
        IBatteryDischargeControl batteryDischargeControl,
        ChargeControlStatusHolder statusHolder,
        ChargePowerConverter power,
        IOptions<SolaxOptions> options,
        IOptions<ChargeControlOptions> chargeControlOptions,
        IOptions<BatteryHoldOptions> batteryHoldOptions,
        ILogger<SolaxPollingService> logger)
    {
        _energyStateReader = energyStateReader;
        _solarForecast = solarForecast;
        _chargingControl = chargingControl;
        _mode = mode;
        _batteryHold = batteryHold;
        _batteryDischargeControl = batteryDischargeControl;
        _statusHolder = statusHolder;
        _power = power;
        _chargeControlDryRun = chargeControlOptions.Value.DryRun;
        _batteryHoldOptions = batteryHoldOptions.Value;
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
            "Charge control at startup: mode {Mode} ({Writes}). It can be changed at runtime.",
            _mode.Mode,
            _chargeControlDryRun ? "dry run — no writes" : "live — writing to the charger");

        _logger.LogInformation(
            "Battery discharge hold at startup: {Enabled}{Detail}",
            _batteryHoldOptions.Enabled ? "enabled" : "disabled (no inverter writes are possible)",
            _batteryHoldOptions.Enabled
                ? $", hold {(_batteryHold.Hold ? "on" : "off")} ({(_batteryHoldOptions.DryRun ? "dry run — no writes" : "live — writing to the inverter")})"
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

                var mode = _mode.Mode;
                ChargeControlCycleResult result;
                if (mode == ChargeControlMode.Solar)
                {
                    result = await _chargingControl.RunCycleAsync(state, stoppingToken);
                }
                else
                {
                    // Off: stop controlling and leave the charger's current setpoint exactly as it is.
                    _chargingControl.ReleaseControl();
                    result = new ChargeControlCycleResult(ChargeControlState.Disabled, null, null, HoldingControl: false);
                }

                var hold = await ApplyBatteryHoldAsync(state, stoppingToken);

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
                    BatteryHoldEnabled: _batteryHoldOptions.Enabled,
                    BatteryHoldRequested: _batteryHold.Hold,
                    BatteryHoldActive: hold.Held,
                    BatteryHoldTargetWatts: hold.ActivePowerTargetWatts,
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
    /// Reconciles the inverter with the battery-hold switch. The command is not a stored setting and
    /// cannot be read back, so there is nothing to compare against the device — the control writes on
    /// each transition, when the target has moved enough to matter, and to renew before the armed
    /// command lapses. A failure here must not take the poll down: the hold is a preservation feature,
    /// and losing it costs battery charge, not safety.
    /// </summary>
    private async Task<BatteryHoldState> ApplyBatteryHoldAsync(EnergyState state, CancellationToken cancellationToken)
    {
        if (!_batteryHoldOptions.Enabled)
        {
            return default;
        }

        var hold = _batteryHold.Hold;
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
        if (result.Held && !_batteryHoldOptions.DryRun && state.BatteryPowerWatts < 0)
        {
            _logger.LogWarning(
                "Battery discharge hold is armed (target {TargetWatts}W) but the battery is discharging at {BatteryPowerWatts}W. "
                + "The power-control command may not be taking effect on this firmware.",
                result.ActivePowerTargetWatts,
                state.BatteryPowerWatts);
        }

        return result;
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
