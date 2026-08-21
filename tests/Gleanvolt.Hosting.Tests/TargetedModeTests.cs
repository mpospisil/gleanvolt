using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Forecasting;
using Gleanvolt.Hosting.Targeting;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The targeted mode end to end through the poll loop. Its headline behaviour — plan backwards, wait,
/// import late, arm the hold only while importing, then end itself — is spread across the planner, the
/// controller, the provider and the polling service, and only the assembly of the four is worth
/// trusting.
///
/// <para>All of these run with no forecast at all, which is the mode's grid-only degradation and also
/// the only way to make the arithmetic exact: the charger's 11.04 kW ceiling and the clock, nothing
/// else.</para>
/// </summary>
public class TargetedModeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 22, 0, 0, TimeSpan.Zero);

    // 16A x 230V x 3 phases: every energy figure below is this against a stretch of clock.
    private const double MaxPowerWatts = 11_040;

    private readonly FakeEvChargerControl _charger = new();
    private readonly FakeBatteryDischargeControl _inverter = new();
    private readonly ChargeControlModeSelector _mode =
        new(ChargeControlMode.Targeted, NullLogger<ChargeControlModeSelector>.Instance);
    private readonly BatteryHoldSelector _manualHold =
        new(initialHold: false, NullLogger<BatteryHoldSelector>.Instance);
    private readonly TargetedChargeSelector _target = new(NullLogger<TargetedChargeSelector>.Instance);
    private readonly ChargeControlStatusHolder _status = new();

    private List<(int Active, int Target, string Reason)> _writes = [];

    private int? LastTarget => _writes.Count == 0 ? null : _writes[^1].Target;

    [Fact]
    public async Task WithNoTimeToSpare_ItChargesAtTheMaximumAndArmsTheHold()
    {
        // 25kWh in two hours is more than the charger can deliver however the plan is arranged.
        Request(25_000, Now.AddHours(2));

        await RunAsync(Drawing(Now, 0), Drawing(Now.AddMinutes(1), MaxPowerWatts));

        Assert.All(_writes, w => Assert.Equal(16, w.Target));
        Assert.All(_inverter.Applied, hold => Assert.True(hold));
        Assert.Equal(ChargeControlMode.Targeted, _mode.Mode);
    }

    [Fact]
    public async Task WithHoursToSpare_ItWaitsAndLeavesTheBatteryAlone()
    {
        // 2kWh needs about eleven minutes of the six hours available, so the block sits at the far end
        // of the night and nothing happens now — including to the home battery.
        Request(2_000, Now.AddHours(6));

        await RunAsync(Drawing(Now, 0), Drawing(Now.AddMinutes(1), 0));

        Assert.Equal(0, LastTarget);
        Assert.Contains("Waiting for sun", _writes[^1].Reason);
        Assert.All(_inverter.Applied, hold => Assert.False(hold));
    }

    [Fact]
    public async Task TheHoldArmsOnlyOnceTheImportStarts()
    {
        // 1.5kWh by 22:10 needs ~8 minutes at full power, so the import starts about two minutes in —
        // and the pack is only locked out for those minutes, not for the whole wait.
        Request(1_500, Now.AddMinutes(10));

        await RunAsync(
            Drawing(Now, 0),                                    // before the block: waiting
            Drawing(Now.AddMinutes(2), MaxPowerWatts));         // inside it: importing

        Assert.Equal([false, true], _inverter.Applied);
        Assert.Equal([0, 16], _writes.Select(w => w.Target));
    }

    [Fact]
    public async Task WhenTheTargetIsMetTheModeReturnsItselfToOffAndTheHoldIsReleased()
    {
        // A car already drawing when the request is made — the charger takes a cycle to obey a pause,
        // and a car that delivers ahead of the plan simply meets the target before the block was due.
        Request(1_500, Now.AddMinutes(10));

        await RunAsync(
            Drawing(Now, MaxPowerWatts),
            Drawing(Now.AddMinutes(4), MaxPowerWatts),
            Drawing(Now.AddMinutes(8), MaxPowerWatts),
            Drawing(Now.AddMinutes(8.5), MaxPowerWatts));

        Assert.Equal(ChargeControlMode.Off, _mode.Mode);
        Assert.Equal(0, LastTarget);
        Assert.Contains("Target reached", _writes[^1].Reason);
        Assert.False(_inverter.Applied[^1]);

        // #89: the same end state the Off button leaves -- stopped, not idling in Fast at the pause
        // current, which the car would happily start drawing from again.
        Assert.Equal(EvChargerMode.Stop, Assert.Single(_charger.ModeWrites).Mode);
    }

    [Fact]
    public async Task TheModeIsHarmlessUntilSomethingIsAskedOfIt()
    {
        await RunAsync(Drawing(Now, 0), Drawing(Now.AddMinutes(1), 0));

        Assert.Equal(0, LastTarget);
        Assert.Contains("No target set", _writes[^1].Reason);
        Assert.Equal(ChargeControlMode.Targeted, _mode.Mode);
        Assert.Null(_status.Current?.TargetedPlan);
    }

    [Fact]
    public async Task ThePlanIsPublishedInTheStatusOnlyWhileThisModeIsDriving()
    {
        Request(3_000, Now.AddHours(2));

        await RunAsync(Drawing(Now, 0));
        Assert.NotNull(_status.Current?.TargetedPlan);

        _mode.Set(ChargeControlMode.Solar, "test");
        await RunAsync(Drawing(Now.AddMinutes(1), 0));

        // The request is still there and still being metered; it just isn't what is driving the
        // charger, so nothing downstream may read it as the current target.
        Assert.Null(_status.Current?.TargetedPlan);
        Assert.NotNull(_target.Request);
    }

    [Fact]
    public async Task PastTheDeparture_TheModeEndsItselfInsteadOfTakingTheCycleDown()
    {
        // Regression (2026-08-21, live): past the departure the provider asked the forecast for a
        // window running backwards, ForPeriod rejected it, and the exception escaped the poll cycle
        // before charge control ran. The mode could not notice its own deadline, so a met target went
        // on charging at 16A for 23 minutes until it was stopped by hand.
        //
        // A real forecast, unlike the rest of this class: the throw was in ForPeriod, so a service
        // that answers null cannot reproduce it.
        Request(1_500, Now.AddMinutes(10));

        await RunAsync(
            forecast: new StubForecastService(Now),
            Drawing(Now.AddMinutes(11), 0),
            Drawing(Now.AddMinutes(12), 0));

        Assert.Equal(ChargeControlMode.Off, _mode.Mode);
        Assert.Equal(0, LastTarget);
        Assert.Contains("has passed", _writes[^1].Reason);
    }

    private void Request(double energyWh, DateTimeOffset departBy) =>
        _target.Set(new TargetedChargeRequest(energyWh, departBy, Now), "test");

    // Drives the real poll loop over a scripted telemetry sequence, then stops it. The service parks on
    // the reader once the script runs out, so exactly these polls happen -- no timing assumptions.
    private Task RunAsync(params EnergyState[] states) => RunAsync(new NoForecastService(), states);

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

        var coordinator = new ChargingControlCoordinator(
            new Dictionary<ChargeControlMode, IChargingController>
            {
                [ChargeControlMode.Targeted] = new TargetedChargingController(
                    power,
                    new TargetedChargingOptions(
                        MinChargingCurrentAmps: chargeControl.MinChargingCurrentAmps,
                        MaxChargingCurrentAmps: chargeControl.MaxChargingCurrentAmps,
                        CurrentStepAmps: chargeControl.CurrentStepAmps,
                        ResumeHysteresisWatts: chargeControl.ResumeHysteresisWatts,
                        MinRunTime: TimeSpan.FromMinutes(10),
                        MinPauseTime: TimeSpan.FromMinutes(15),
                        CompletionDwell: chargeControl.CompletionDwell)),
                [ChargeControlMode.Solar] = new LiveSolarChargingController(power, 6, 16, 1, 200, 95, 90),
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

        var reader = new ScriptedEnergyStateReader(states);
        var service = new PollingService(
            reader,
            forecast,
            coordinator,
            dayPlan,
            TargetedCharge.Provider(
                forecast, dayPlan, power, chargeControl, forecastOptions, _target,
                // No margin, so the arithmetic in these tests is the clock and the ceiling alone.
                new TargetedChargeOptions { SafetyMargin = TimeSpan.Zero }),
            _mode,
            // The real actions over the fake charger: a mode that ends itself has to stop the charger
            // exactly as the Off button does, and that is the code path it goes through.
            new ChargeActions(_charger, _mode, NullLogger<ChargeActions>.Instance),
            _manualHold,
            _inverter,
            _status,
            power,
            Options.Create(new SolaxOptions
            {
                Inverter = new DeviceConfig { Host = "127.0.0.1" },
                EvCharger = new DeviceConfig { Host = "127.0.0.1" },
                PollIntervalSeconds = 0,
            }),
            Options.Create(chargeControl),
            Options.Create(new BatteryHoldOptions { Enabled = true, DryRun = true }),
            forecastOptions,
            NullLogger<PollingService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await reader.Exhausted.WaitAsync(TimeSpan.FromSeconds(10));
        _writes = [.. _charger.CurrentWrites];
        await service.StopAsync(CancellationToken.None);
    }

    // A plugged-in car taking the stated power. SOC is high enough that the home battery's booking
    // never enters into it — these tests are about the clock, not the pack.
    private static EnergyState Drawing(DateTimeOffset at, double evWatts) =>
        new(at, BatterySocPercent: 90, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: evWatts + 300,
            EvChargerStatus.Charging, EvChargerPowerWatts: evWatts);
}

/// <summary>
/// A forecast that answers from real <see cref="SolarForecast"/> periods, so <c>ForPeriod</c>'s own
/// argument checking is in play — which is where the poll loop was thrown from.
/// </summary>
internal sealed class StubForecastService(DateTimeOffset from) : ISolarForecastService
{
    private readonly SolarForecast _forecast = new(
        from,
        [new SolarForecastPeriod(from.AddMinutes(30), TimeSpan.FromMinutes(30), EstimatedPowerWatts: 0)]);

    public SolarForecast? GetForecastForToday() => _forecast;

    public SolarForecast? GetForecast(DateTimeOffset from, DateTimeOffset to) => _forecast.ForPeriod(from, to);
}
