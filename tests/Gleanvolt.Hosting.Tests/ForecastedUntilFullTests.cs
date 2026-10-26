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
/// Forecasted <b>until full</b> (#279) through the real poll loop: one session over several days, stood
/// down at night and re-armed on the next day's sun, until the car is full.
/// </summary>
public sealed class ForecastedUntilFullTests : IDisposable
{
    // Monday 26 October 2026, in UTC so the fixtures mean the same on any build agent.
    private static readonly DateTimeOffset Day1 = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeEvChargerControl _charger = new();
    private readonly ChargeControlModeSelector _mode =
        new(ChargeControlMode.Off, NullLogger<ChargeControlModeSelector>.Instance);
    private readonly List<ChargeControlStatus> _statuses = [];
    private Action<ChargeControlStatus>? _afterPoll;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"until-full-{Guid.NewGuid():N}");

    private string SessionPath => Path.Combine(_directory, "until-full.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task OneSessionRunsOverThreeDays_StoodDownAtNight_UntilTheCarIsFull()
    {
        _mode.Set(ChargeControlMode.Forecasted, "test");

        await RunAsync(
            untilFull: true,
            // Day 1: charging in the sun, then nothing left of it.
            Drawing(Day1.AddHours(10)),
            Plugged(Day1.AddHours(16)),
            Stopped(Day1.AddHours(22)),
            // Day 2: dark before the window, then the sun, then the car draws.
            Stopped(Day1.AddDays(1).AddHours(5)),
            Sunny(Day1.AddDays(1).AddHours(10)),
            Drawing(Day1.AddDays(1).AddHours(10).AddMinutes(1)),
            Plugged(Day1.AddDays(1).AddHours(16)),
            // Day 3: the same again.
            Sunny(Day1.AddDays(2).AddHours(10)),
            Drawing(Day1.AddDays(2).AddHours(10).AddMinutes(1)));

        // Never ended: one stand-down per night, one re-arm per morning.
        Assert.DoesNotContain(_statuses, status => status.SessionCompleted);
        Assert.Equal(
            [EvChargerMode.Stop, EvChargerMode.Fast, EvChargerMode.Stop, EvChargerMode.Fast],
            _charger.ModeWrites.Select(write => write.Mode));

        var night = _statuses.Single(status => status.Timestamp == Day1.AddHours(22));
        Assert.Equal(ChargeControlMode.Forecasted, night.Mode);
        Assert.True(night.ChargerStoodDown);
        Assert.Equal(Day1.AddDays(1).AddHours(9), night.WaitUntil);
        Assert.Equal(1, night.UntilFull?.Day);

        var day2 = _statuses.Single(status => status.Timestamp == Day1.AddDays(1).AddHours(10).AddMinutes(1));
        Assert.Equal(ChargeControlMode.Forecasted, day2.Mode);
        Assert.Equal(ChargeControlState.Charging, day2.State);
        Assert.Equal(2, day2.UntilFull?.Day);

        var day3 = _statuses[^1];
        Assert.Equal(ChargeControlMode.Forecasted, day3.Mode);
        Assert.Equal(ChargeControlState.Charging, day3.State);
        Assert.Equal(3, day3.UntilFull?.Day);
        Assert.Equal(Day1.AddHours(10), day3.UntilFull?.Since);

        // Kept on disk while it runs, so a restart resumes it.
        Assert.True(File.Exists(SessionPath));
    }

    [Fact]
    public async Task AFullCarStillEndsIt_AndTheFileGoes()
    {
        _mode.Set(ChargeControlMode.Forecasted, "test");

        await RunAsync(
            untilFull: true,
            Drawing(Day1.AddHours(10)),
            Drawing(Day1.AddHours(10).AddMinutes(1)),
            Finished(Day1.AddHours(10).AddMinutes(2)));

        Assert.Equal(ChargingSessionEndReason.SessionComplete, _statuses[^1].SessionEndReason);
        Assert.Equal(ChargeControlMode.Off, _mode.Mode);
        Assert.False(File.Exists(SessionPath));
    }

    [Fact]
    public async Task WithTheOptionOff_TheDayStillEnds()
    {
        _mode.Set(ChargeControlMode.Forecasted, "test");

        await RunAsync(untilFull: false, Drawing(Day1.AddHours(10)), Plugged(Day1.AddHours(16)));

        Assert.Equal(ChargingSessionEndReason.NoSunLeftToday, _statuses[^1].SessionEndReason);
        Assert.Null(_statuses[0].UntilFull);
        Assert.False(File.Exists(SessionPath));
    }

    [Fact]
    public async Task ARestartResumesTheSession_AndCarriesOnCounting()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(SessionPath, """{"mode":"Forecasted","untilFull":true,"since":"2026-10-26T10:00:00+00:00"}""");

        // Started Off with the option off, as every restart is; the file brings both back.
        await RunAsync(untilFull: false, Stopped(Day1.AddDays(1).AddHours(2)), Stopped(Day1.AddDays(1).AddHours(3)));

        Assert.Equal(ChargeControlMode.Forecasted, _mode.Mode);
        Assert.Equal(EvChargerMode.Fast, _charger.ModeWrites[0].Mode);
        Assert.Contains("restart", _charger.ModeWrites[0].Reason);

        // Night, so the first cycle stands the charger straight back down.
        Assert.Equal(EvChargerMode.Stop, _charger.ModeWrites[^1].Mode);
        Assert.Equal(2, _statuses[^1].UntilFull?.Day);
        Assert.True(File.Exists(SessionPath));
    }

    [Fact]
    public async Task OffEndsIt_AndTheFileGoes()
    {
        _mode.Set(ChargeControlMode.Forecasted, "test");
        var existedWhileRunning = false;
        _afterPoll = status =>
        {
            if (status.Mode == ChargeControlMode.Forecasted)
            {
                existedWhileRunning = File.Exists(SessionPath);
                _mode.Set(ChargeControlMode.Off, "test");
            }
        };

        await RunAsync(untilFull: true, Drawing(Day1.AddHours(10)), Drawing(Day1.AddHours(10).AddMinutes(1)));

        Assert.True(existedWhileRunning);
        Assert.False(File.Exists(SessionPath));
    }

    // A car drawing 6kW at noon-ish sun: 6.3kW of roof over a 300W house.
    private static EnergyState Drawing(DateTimeOffset at) =>
        new(at, BatterySocPercent: 100, BatteryPowerWatts: 0, SolarPowerWatts: 6_300,
            GridPowerWatts: 0, EvChargerStatus.Charging, EvChargerPowerWatts: 6_000);

    // The same sun, the car taking nothing yet: the morning before a re-arm.
    private static EnergyState Sunny(DateTimeOffset at) =>
        new(at, BatterySocPercent: 100, BatteryPowerWatts: 0, SolarPowerWatts: 6_300,
            GridPowerWatts: -6_000, EvChargerStatus.Preparing, EvChargerPowerWatts: 0);

    // Plugged in and taking nothing, no sun.
    private static EnergyState Plugged(DateTimeOffset at) =>
        new(at, BatterySocPercent: 100, BatteryPowerWatts: 0, SolarPowerWatts: 0,
            GridPowerWatts: 300, EvChargerStatus.Charging, EvChargerPowerWatts: 0);

    // Stood down overnight: the charger's status then says nothing about the plug.
    private static EnergyState Stopped(DateTimeOffset at) =>
        new(at, BatterySocPercent: 100, BatteryPowerWatts: 0, SolarPowerWatts: 0,
            GridPowerWatts: 300, EvChargerStatus.Preparing, EvChargerPowerWatts: 0);

    // The wallbox closing the session at the car's limit, straight after a draw.
    private static EnergyState Finished(DateTimeOffset at) =>
        new(at, BatterySocPercent: 100, BatteryPowerWatts: 0, SolarPowerWatts: 6_300,
            GridPowerWatts: -6_000, EvChargerStatus.Finishing, EvChargerPowerWatts: 0);

    private async Task RunAsync(bool untilFull, params EnergyState[] states)
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
        var forecastSettings = new ForecastChargeOptions { UntilFull = untilFull, EnableBatteryLoan = false };
        var forecastOptions = Options.Create(forecastSettings);
        var clock = new UtcClock();
        var reader = new ClockedReader(clock, states);
        var forecast = new DailySunForecast(clock);

        var forecasted = new ForecastedChargingController(
            power,
            new LiveSolarChargingController(
                power,
                chargeControl.MinChargingCurrentAmps,
                chargeControl.MaxChargingCurrentAmps,
                chargeControl.CurrentStepAmps,
                chargeControl.ResumeHysteresisWatts,
                chargeControl.BatteryFullSocPercent,
                chargeControl.BatteryReleaseSocPercent,
                chargeControl.CompletionDwell),
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
                FinalGuardBefore: forecastSettings.FinalGuardBefore,
                CompletionDwell: chargeControl.CompletionDwell));

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
            timeProvider: clock);

        var settings = new UntilFullSettings(forecastOptions, NullLogger<UntilFullSettings>.Instance);
        var session = new UntilFullSessionFile(SessionPath, NullLogger<UntilFullSessionFile>.Instance, clock);
        var solarGridOptions = Options.Create(new SolarGridChargeOptions());

        var solarGrid = new SolarGridProvider(
            forecast,
            dayPlan,
            new SolarGridSettings(solarGridOptions, NullLogger<SolarGridSettings>.Instance),
            solarGridOptions,
            forecastOptions,
            NullLogger<SolarGridProvider>.Instance,
            clock,
            new ChargingLimits(chargeControl.MinChargingCurrentAmps, chargeControl.MaxChargingCurrentAmps, chargeControl.Phases),
            power,
            settings);

        var statusHolder = new ChargeControlStatusHolder();
        statusHolder.Updated += status =>
        {
            _statuses.Add(status);
            _afterPoll?.Invoke(status);
        };

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
            new FakeBatteryDischargeControl(),
            statusHolder,
            power,
            Options.Create(new ControllerOptions { PollIntervalSeconds = 0 }),
            Options.Create(chargeControl),
            Options.Create(new BatteryHoldOptions { Enabled = true, DryRun = true }),
            forecastOptions,
            NullLogger<PollingService>.Instance,
            clock,
            solarGrid,
            untilFull: settings,
            untilFullSession: session);

        await service.StartAsync(CancellationToken.None);
        await reader.Exhausted.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);
    }

    // "Today" ends at UTC midnight, and the poll's own reading is "now", so the forecast follows it.
    private sealed class UtcClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Day1;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    // The scripted readings, moving the clock to each one as it is read.
    private sealed class ClockedReader(UtcClock clock, params EnergyState[] states) : IEnergyStateReader
    {
        private readonly ScriptedEnergyStateReader _inner = new(states);

        public Task Exhausted => _inner.Exhausted;

        public async Task<EnergyState> ReadAsync(CancellationToken cancellationToken = default)
        {
            var state = await _inner.ReadAsync(cancellationToken);
            clock.Now = state.Timestamp;
            return state;
        }
    }

    /// <summary>
    /// A forecast fetched a moment ago with the same sun every day, 09:00 to 15:00 at 6.3kW, for the day
    /// the clock is in and the one after it, as a real provider's reaches past midnight.
    /// </summary>
    private sealed class DailySunForecast(UtcClock clock) : ISolarForecastService
    {
        private SolarForecast Forecast
        {
            get
            {
                var today = new DateTimeOffset(clock.Now.UtcDateTime.Date, TimeSpan.Zero);
                var periods = new List<SolarForecastPeriod>();

                foreach (var day in new[] { today, today.AddDays(1) })
                {
                    for (var end = day.AddHours(9.5); end <= day.AddHours(15); end = end.AddMinutes(30))
                    {
                        periods.Add(new SolarForecastPeriod(end, TimeSpan.FromMinutes(30), 6_300));
                    }
                }

                return new SolarForecast(clock.Now, periods);
            }
        }

        public SolarForecast? GetForecastForToday() =>
            Forecast.ForDate(DateOnly.FromDateTime(clock.Now.UtcDateTime), TimeZoneInfo.Utc);

        public SolarForecast? GetForecast(DateTimeOffset from, DateTimeOffset to) => Forecast.ForPeriod(from, to);

        public SolarForecast? GetDayForecast(DateOnly localDate) => GetForecastForToday();
    }
}
