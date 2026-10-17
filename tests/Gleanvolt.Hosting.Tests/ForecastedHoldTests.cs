using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Forecasting;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The forecast mode never arms the battery hold, through the poll loop. It is there to store the sun and
/// keep the grid out; it pauses the car at its own floor, so a hold could only swap pack for grid.
/// </summary>
public class ForecastedHoldTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeEvChargerControl _charger = new();
    private readonly FakeBatteryDischargeControl _inverter = new();
    private readonly ChargeControlModeSelector _mode =
        new(ChargeControlMode.Forecasted, NullLogger<ChargeControlModeSelector>.Instance);

    private List<(int Active, int Target, string Reason)> _writes = [];

    [Fact]
    public async Task APausedCarBelowTheFloor_LeavesThePackToTheHouse()
    {
        // 2026-10-17: paused at 08:48 with the pack under the floor, and the hold stayed armed all day.
        // At 84% with an oven on, the house took 2kW from the grid while the pack sat still.
        await RunAsync(
            Plugged(Now, socPercent: 30, solarWatts: 1_500, houseWatts: 4_000),
            Plugged(Now.AddMinutes(1), socPercent: 30, solarWatts: 1_500, houseWatts: 4_000));

        Assert.Contains("needed to restart", _writes[^1].Reason);
        Assert.Equal([false, false], _inverter.Applied);
    }

    [Fact]
    public async Task ACarChargingOnTheFloor_IsNotHeldOffThePack()
    {
        // Under the owner's 50% clamp inside the minimum run time the car is still charging. A hold here
        // would only send the car to the grid; the pause at the end of the run time is what protects
        // the evening.
        await RunAsync(
            Drawing(Now, socPercent: 60, surplusWatts: 6_000),
            Drawing(Now.AddMinutes(1), socPercent: 49, surplusWatts: 6_000),
            Drawing(Now.AddMinutes(20), socPercent: 49, surplusWatts: 6_000));

        Assert.Contains("minimum run time", _writes[1].Reason);
        Assert.Equal(0, _writes[^1].Target);
        Assert.Equal([false, false, false], _inverter.Applied);
    }

    // A car drawing at 6kW while the roof makes the stated surplus over a 300W house.
    private static EnergyState Drawing(DateTimeOffset at, double socPercent, double surplusWatts) =>
        new(at, BatterySocPercent: socPercent, BatteryPowerWatts: 0, SolarPowerWatts: surplusWatts + 300,
            GridPowerWatts: 6_000 - surplusWatts, EvChargerStatus.Charging, EvChargerPowerWatts: 6_000);

    // A plugged-in car taking nothing, the house importing what the roof does not cover.
    private static EnergyState Plugged(DateTimeOffset at, double socPercent, double solarWatts, double houseWatts) =>
        new(at, BatterySocPercent: socPercent, BatteryPowerWatts: 0, SolarPowerWatts: solarWatts,
            GridPowerWatts: houseWatts - solarWatts, EvChargerStatus.Charging, EvChargerPowerWatts: 0);

    // The arrangement SolarGridModeTests uses, with the forecast mode's controller in it.
    private async Task RunAsync(params EnergyState[] states)
    {
        var chargeControl = new ChargeControlOptions
        {
            Phases = 3,
            MaxChargingCurrentAmps = 16,
            PauseCurrentAmps = 0,
            CompletionDwell = TimeSpan.FromMinutes(2),
            CompletionPowerThresholdWatts = 200,
        };

        var power = new ChargePowerConverter(chargeControl.NominalVoltage, chargeControl.Phases);
        var forecastSettings = new ForecastChargeOptions();
        var forecastOptions = Options.Create(forecastSettings);
        var forecast = new SunForecastService(Now, from: Now, until: Now.AddHours(6), watts: 6_300);

        var forecasted = new ForecastedChargingController(
            power,
            new LiveSolarChargingController(
                power,
                chargeControl.MinChargingCurrentAmps,
                chargeControl.MaxChargingCurrentAmps,
                chargeControl.CurrentStepAmps,
                chargeControl.ResumeHysteresisWatts,
                chargeControl.BatteryFullSocPercent,
                chargeControl.BatteryReleaseSocPercent),
            new ForecastedChargingOptions(
                MinChargingCurrentAmps: chargeControl.MinChargingCurrentAmps,
                MaxChargingCurrentAmps: chargeControl.MaxChargingCurrentAmps,
                CurrentStepAmps: chargeControl.CurrentStepAmps,
                ResumeHysteresisWatts: chargeControl.ResumeHysteresisWatts,
                FloorResumeMarginPercent: forecastSettings.FloorResumeMarginPercent,
                FloorGuardReserveWatts: forecastSettings.FloorGuardReserveWatts,
                EnableBatteryLoan: false,
                MaxLoanPowerWatts: forecastSettings.MaxLoanPowerWatts,
                MinBridgeSurplusWatts: forecastSettings.MinBridgeSurplusWatts,
                SpillBridgeSurplusWatts: forecastSettings.SpillBridgeSurplusWatts,
                MaxDailyLoanWh: forecastSettings.MaxDailyLoanKWh * 1000,
                LoanSocMarginPercent: forecastSettings.LoanSocMarginPercent,
                MinRunTime: TimeSpan.FromMinutes(10),
                MinPauseTime: TimeSpan.FromMinutes(15),
                FinalGuardBefore: forecastSettings.FinalGuardBefore));

        var coordinator = new ChargingControlCoordinator(
            new Dictionary<ChargeControlMode, IChargingController> { [ChargeControlMode.Forecasted] = forecasted },
            _charger,
            new SurplusMovingAverage(TimeSpan.FromMinutes(3)),
            pauseCurrentAmps: chargeControl.PauseCurrentAmps,
            idlePowerThresholdWatts: chargeControl.CompletionPowerThresholdWatts,
            NullLogger<ChargingControlCoordinator>.Instance);

        var dayPlan = new DayPlanProvider(
            forecast,
            forecastOptions,
            Options.Create(chargeControl),
            power,
            NullLogger<DayPlanProvider>.Instance,
            timeProvider: new UtcClock());

        var reader = new ScriptedEnergyStateReader(states);
        var service = new PollingService(
            reader,
            forecast,
            coordinator,
            dayPlan,
            TargetedCharge.Provider(forecast, dayPlan, power, chargeControl, forecastOptions),
            FastCharge.Provider(),
            _mode,
            new ChargeActions(_charger, _mode, new FastChargingController(16, TimeSpan.FromMinutes(2)), NullLogger<ChargeActions>.Instance),
            new BatteryHoldSelector(initialHold: false, NullLogger<BatteryHoldSelector>.Instance),
            _inverter,
            new ChargeControlStatusHolder(),
            power,
            Options.Create(new ControllerOptions { PollIntervalSeconds = 0 }),
            Options.Create(chargeControl),
            Options.Create(new BatteryHoldOptions { Enabled = true, DryRun = true }),
            forecastOptions,
            NullLogger<PollingService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await reader.Exhausted.WaitAsync(TimeSpan.FromSeconds(10));
        _writes = [.. _charger.CurrentWrites];
        await service.StopAsync(CancellationToken.None);
    }

    // "Today" ends at UTC midnight, so the fixtures' UTC instants mean the same thing on any build agent.
    private sealed class UtcClock : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    /// <summary>A fresh forecast with a flat stretch of sun from one instant to another, and none either side.</summary>
    private sealed class SunForecastService : ISolarForecastService
    {
        private readonly SolarForecast _forecast;

        public SunForecastService(DateTimeOffset retrievedAt, DateTimeOffset from, DateTimeOffset until, double watts)
        {
            var periods = new List<SolarForecastPeriod>();
            for (var end = from.AddMinutes(30); end <= until; end = end.AddMinutes(30))
            {
                periods.Add(new SolarForecastPeriod(end, TimeSpan.FromMinutes(30), watts));
            }

            _forecast = new SolarForecast(retrievedAt, periods);
        }

        public SolarForecast? GetForecastForToday() => _forecast;

        public SolarForecast? GetForecast(DateTimeOffset from, DateTimeOffset to) => _forecast.ForPeriod(from, to);

        public SolarForecast? GetDayForecast(DateOnly localDate) => _forecast;
    }
}
