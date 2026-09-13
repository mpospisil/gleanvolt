using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Forecasting;
using Gleanvolt.Hosting.SolarGrid;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The solar-grid mode end to end through the poll loop. What is worth trusting only in assembly: the
/// bridge arming the hold (controller → coordinator → polling service), the forecast ending the mode
/// through the same stop path the Off button uses, and a stand-down being re-armed by the sun.
/// </summary>
public class SolarGridModeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeEvChargerControl _charger = new();
    private readonly FakeBatteryDischargeControl _inverter = new();
    private readonly ChargeControlModeSelector _mode =
        new(ChargeControlMode.SolarGrid, NullLogger<ChargeControlModeSelector>.Instance);
    private readonly ChargeControlStatusHolder _status = new();

    private List<(int Active, int Target, string Reason)> _writes = [];

    [Fact]
    public async Task ASurplusUnderTheFloor_IsBridgedFromTheGrid_WithTheHoldArmed()
    {
        // 3kW clears the 2kW minimum but not the 4.14kW floor: 6A, and the pack held out of the gap.
        await RunAsync(
            new SunForecastService(Now, from: Now, until: Now.AddHours(6), watts: 5_000),
            Exporting(Now, 3_000),
            Exporting(Now.AddMinutes(1), 3_000));

        Assert.All(_writes, w => Assert.Equal(6, w.Target));
        Assert.All(_inverter.Applied, hold => Assert.True(hold));
        Assert.Equal(ChargeControlMode.SolarGrid, _mode.Mode);
        Assert.NotNull(_status.Current?.SolarGrid);
    }

    [Fact]
    public async Task ASurplusOverTheFloor_TakesNothingFromTheGrid_AndLeavesThePackFree()
    {
        await RunAsync(
            new SunForecastService(Now, from: Now, until: Now.AddHours(6), watts: 8_000),
            Exporting(Now, 6_000),
            Exporting(Now.AddMinutes(1), 6_000));

        Assert.All(_writes, w => Assert.Equal(8, w.Target));
        Assert.All(_inverter.Applied, hold => Assert.False(hold));
    }

    [Fact]
    public async Task NoSunLeftToday_EndsTheModeLikeTheOffButton()
    {
        // The forecast's useful sun was over an hour ago and the roof agrees.
        await RunAsync(
            new SunForecastService(Now, from: Now.AddHours(-3), until: Now.AddHours(-1), watts: 5_000),
            Exporting(Now, 0));

        Assert.Equal(ChargeControlMode.Off, _mode.Mode);
        Assert.Contains(_charger.ModeWrites, w => w.Mode == EvChargerMode.Stop);
        Assert.Contains("returning to Off", _writes[^1].Reason);
    }

    [Fact]
    public async Task SunHoursAway_StandsTheChargerDown_AndTheSunRearmsIt()
    {
        await RunAsync(
            new SunForecastService(Now, from: Now.AddHours(2), until: Now.AddHours(6), watts: 8_000),
            Exporting(Now, 0),
            Exporting(Now.AddHours(2), 6_000));

        Assert.Equal([EvChargerMode.Stop, EvChargerMode.Fast], _charger.ModeWrites.Select(w => w.Mode));
        Assert.Equal(8, _writes[^1].Target);
        Assert.Equal(ChargeControlMode.SolarGrid, _mode.Mode);
    }

    [Fact]
    public async Task ACarTheChargerStartedAtPlugIn_IsTakenOverAtOnce_WithoutTheRestartDwell()
    {
        // 2026-09-13 10:37: plugged in, the charger started the car at 16A by itself, and the mode was
        // picked a minute later. Its first decision used to stop the car and wait 15 minutes to
        // "restart" a charge it never ran, with 2.3kW of surplus there.
        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Fast, 16);

        await RunAsync(
            new SunForecastService(Now, from: Now, until: Now.AddHours(6), watts: 5_000),
            Drawing(Now, surplusWatts: 2_300, evWatts: 10_900),
            Drawing(Now.AddSeconds(8), surplusWatts: 2_300, evWatts: 4_140));

        Assert.DoesNotContain(_writes, w => w.Reason.Contains("minimum before restarting"));
        Assert.Equal(6, _writes[0].Target);
    }

    [Fact]
    public async Task APauseAfterThisModeHasCharged_StillWaitsTheRestartDwell()
    {
        // The dwell's real job: a charge this mode ran, paused by a cloud, is not restarted a minute
        // later just because the average climbed back over the threshold.
        await RunAsync(
            new SunForecastService(Now, from: Now, until: Now.AddHours(6), watts: 8_000),
            Drawing(Now, surplusWatts: 6_000, evWatts: 0),
            Drawing(Now.AddMinutes(1), surplusWatts: 6_000, evWatts: 5_520),
            Drawing(Now.AddMinutes(11), surplusWatts: 0, evWatts: 5_520),
            Exporting(Now.AddMinutes(12), 6_000));

        // Paused at 11 min; the 3kW average at 12 min would have restarted at 6A without the dwell.
        Assert.Equal(0, _writes[^1].Target);
        Assert.Contains("minimum before restarting", _writes[^1].Reason);
    }

    // A car drawing evWatts while the roof makes the stated surplus over a 300W house.
    private static EnergyState Drawing(DateTimeOffset at, double surplusWatts, double evWatts) =>
        new(at, BatterySocPercent: 60, BatteryPowerWatts: 0, SolarPowerWatts: surplusWatts + 300,
            GridPowerWatts: evWatts - surplusWatts, EvChargerStatus.Charging, EvChargerPowerWatts: evWatts);

    // Drives the real poll loop over a scripted telemetry sequence, then stops it -- the arrangement
    // TargetedModeTests uses, with the solar-grid mode's own provider in it.
    private async Task RunAsync(ISolarForecastService forecast, params EnergyState[] states)
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
        var forecastOptions = Options.Create(new ForecastChargeOptions());
        var solarGridOptions = Options.Create(new SolarGridChargeOptions());

        var coordinator = new ChargingControlCoordinator(
            new Dictionary<ChargeControlMode, IChargingController>
            {
                [ChargeControlMode.SolarGrid] = new SolarGridChargingController(
                    power,
                    new SolarGridChargingOptions(
                        MinChargingCurrentAmps: chargeControl.MinChargingCurrentAmps,
                        MaxChargingCurrentAmps: chargeControl.MaxChargingCurrentAmps,
                        CurrentStepAmps: chargeControl.CurrentStepAmps,
                        ResumeHysteresisWatts: chargeControl.ResumeHysteresisWatts,
                        MinRunTime: TimeSpan.FromMinutes(10),
                        MinPauseTime: TimeSpan.FromMinutes(15),
                        CompletionDwell: chargeControl.CompletionDwell)),
            },
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
            NullLogger<DayPlanProvider>.Instance);

        var solarGrid = new SolarGridProvider(
            forecast,
            dayPlan,
            new SolarGridSettings(solarGridOptions, NullLogger<SolarGridSettings>.Instance),
            solarGridOptions,
            forecastOptions,
            NullLogger<SolarGridProvider>.Instance,
            new UtcClock());

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
            _status,
            power,
            Options.Create(new ControllerOptions { PollIntervalSeconds = 0 }),
            Options.Create(chargeControl),
            Options.Create(new BatteryHoldOptions { Enabled = true, DryRun = true }),
            forecastOptions,
            NullLogger<PollingService>.Instance,
            solarGrid: solarGrid);

        await service.StartAsync(CancellationToken.None);
        await reader.Exhausted.WaitAsync(TimeSpan.FromSeconds(10));
        _writes = [.. _charger.CurrentWrites];
        await service.StopAsync(CancellationToken.None);
    }

    // A plugged-in car taking nothing while the roof exports the stated surplus over a 300W house.
    private static EnergyState Exporting(DateTimeOffset at, double surplusWatts) =>
        new(at, BatterySocPercent: 60, BatteryPowerWatts: 0, SolarPowerWatts: surplusWatts + 300,
            GridPowerWatts: -surplusWatts, EvChargerStatus.Charging, EvChargerPowerWatts: 0);

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
