using Microsoft.Extensions.Options;
using Solax.Core.Enums;
using Solax.Core.Interfaces;
using Solax.Core.Models;
using Solax.Worker.Configuration;

namespace Solax.Worker;

public sealed class SolaxPollingService : BackgroundService
{
    private readonly IEnergyStateReader _energyStateReader;
    private readonly ISolarForecastService _solarForecast;
    private readonly ChargingControlCoordinator _chargingControl;
    private readonly IChargeControlSwitch _chargeControlSwitch;
    private readonly ChargeControlStatusHolder _statusHolder;
    private readonly bool _chargeControlDryRun;
    private readonly ILogger<SolaxPollingService> _logger;
    private readonly TimeSpan _pollInterval;

    public SolaxPollingService(
        IEnergyStateReader energyStateReader,
        ISolarForecastService solarForecast,
        ChargingControlCoordinator chargingControl,
        IChargeControlSwitch chargeControlSwitch,
        ChargeControlStatusHolder statusHolder,
        IOptions<SolaxOptions> options,
        IOptions<ChargeControlOptions> chargeControlOptions,
        ILogger<SolaxPollingService> logger)
    {
        _energyStateReader = energyStateReader;
        _solarForecast = solarForecast;
        _chargingControl = chargingControl;
        _chargeControlSwitch = chargeControlSwitch;
        _statusHolder = statusHolder;
        _chargeControlDryRun = chargeControlOptions.Value.DryRun;
        _logger = logger;
        _pollInterval = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
    }

    // Shutdown runs with a fresh token (ExecuteAsync's is already cancelled), so the pause write can
    // still reach the charger. Without this we'd leave our override on the device after stopping.
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _chargingControl.ReleaseControlAsync("Service stopping.", cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Charge control at startup: {State} ({Mode}). It can be toggled at runtime.",
            _chargeControlSwitch.IsEnabled ? "ENABLED" : "disabled",
            _chargeControlDryRun ? "dry run — no writes" : "live — writing to the charger");

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

                ChargeControlCycleResult result;
                if (_chargeControlSwitch.IsEnabled)
                {
                    result = await _chargingControl.RunCycleAsync(state, stoppingToken);
                }
                else
                {
                    // Switched off: release the charger if we were driving it (pauses, keeps the
                    // session), then report Disabled.
                    await _chargingControl.ReleaseControlAsync("Charge control switched off.", stoppingToken);
                    result = new ChargeControlCycleResult(ChargeControlState.Disabled, null, null, HoldingControl: false);
                }

                _statusHolder.Set(new ChargeControlStatus(
                    Enabled: _chargeControlSwitch.IsEnabled,
                    DryRun: _chargeControlDryRun,
                    HoldingControl: result.HoldingControl,
                    State: result.State,
                    SurplusWatts: result.SurplusWatts,
                    TargetCurrentAmps: result.TargetCurrentAmps,
                    ActiveCurrentAmps: state.ChargeCurrentAmps,
                    BatterySocPercent: state.BatterySocPercent,
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
