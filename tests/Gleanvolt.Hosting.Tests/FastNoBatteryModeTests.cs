using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Fast;
using Gleanvolt.Hosting.Forecasting;
using Gleanvolt.Hosting.Targeting;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The fast-without-battery mode end to end through the poll loop, because its headline behaviour —
/// arm the hold, pin the current, then end itself — is spread across the controller, the coordinator
/// and the polling service, and only the assembly of the three is worth trusting.
/// </summary>
public class FastNoBatteryModeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 18, 0, 0, TimeSpan.Zero);

    private readonly FakeEvChargerControl _charger = new();
    private readonly FakeBatteryDischargeControl _inverter = new();
    private readonly ChargeControlModeSelector _mode =
        new(ChargeControlMode.FastNoBattery, NullLogger<ChargeControlModeSelector>.Instance);
    private readonly BatteryHoldSelector _manualHold =
        new(initialHold: false, NullLogger<BatteryHoldSelector>.Instance);
    private readonly FastChargeSelector _fastLimit = new(NullLogger<FastChargeSelector>.Instance);
    private readonly ChargeControlStatusHolder _status = new();

    // The setpoint writes made while the loop was running. Snapshotted before the service is stopped,
    // because stopping legitimately writes a pause of its own -- which would otherwise mask what the
    // mode itself did. (The fake charger writes on every cycle: suppressing repeats is the real
    // EvChargerControl's job, tested there.)
    private List<(int Active, int Target, string Reason)> _writes = [];

    private int? LastTarget => _writes.Count == 0 ? null : _writes[^1].Target;

    [Fact]
    public async Task TheHoldIsArmedForAsLongAsTheModeIsSelected()
    {
        // No forecast, no plan, mid-evening: none of the forecast mode's conditions are met, and the
        // hold is armed anyway -- that is the whole point of this mode.
        await RunAsync(Charging(Now), Charging(Now.AddMinutes(1)));

        Assert.All(_inverter.Applied, hold => Assert.True(hold));
        Assert.Equal(ChargeControlMode.FastNoBattery, _mode.Mode);
    }

    [Fact]
    public async Task TheChargerIsPinnedAtTheConfiguredMaximum()
    {
        await RunAsync(Charging(Now), Charging(Now.AddMinutes(1)));

        Assert.NotEmpty(_writes);
        Assert.All(_writes, w => Assert.Equal(16, w.Target));
    }

    [Fact]
    public async Task AFinishedCarPausesTheCharger_ReturnsToOff_AndReleasesTheHold()
    {
        await RunAsync(
            Charging(Now),                      // the car draws -> we are committed
            Idle(Now.AddMinutes(1)),            // it stops; the idle clock starts
            Idle(Now.AddMinutes(4)));           // three minutes idle, past the two-minute dwell

        Assert.Equal(ChargeControlMode.Off, _mode.Mode);
        Assert.Equal(0, LastTarget);            // paused, not left armed at 16A
        Assert.Contains("returning to Off", _writes[^1].Reason);
        Assert.False(_inverter.Applied[^1]);    // released in the same cycle, not a poll later
    }

    [Fact]
    public async Task AFinishedCarStopsTheChargerTheWayTheOffButtonWould()
    {
        // #89: a mode that switches itself off must leave the charger exactly where Off leaves it --
        // stopped, not sitting in Fast at the pause current, which is a different end state.
        await RunAsync(Charging(Now), Idle(Now.AddMinutes(1)), Idle(Now.AddMinutes(4)));

        Assert.Equal(EvChargerMode.Stop, Assert.Single(_charger.ModeWrites).Mode);
        Assert.Contains("charging finished", Assert.Single(_charger.ModeWrites).Reason);
    }

    [Fact]
    public async Task TheChargerIsStoppedBeforeTheHoldIsReleased_InTheOneCycle()
    {
        var modeWritesWhenTheHoldWasReleased = new List<int>();
        _inverter.OnApply = hold =>
        {
            if (!hold)
            {
                modeWritesWhenTheHoldWasReleased.Add(_charger.ModeWrites.Count);
            }
        };

        await RunAsync(Charging(Now), Idle(Now.AddMinutes(1)), Idle(Now.AddMinutes(4)));

        // The release that matters is the last one: by then the charger has been stopped.
        Assert.Equal(1, modeWritesWhenTheHoldWasReleased[^1]);
    }

    [Fact]
    public async Task EveryEndingReleasesTheHold_EvenOneTheOwnerAskedForFirst()
    {
        // This asserted the opposite until #119, when it was decided that a fast charge ending must
        // leave nothing armed. The old rule -- "a manually requested hold is never released by a mode"
        // -- reads well until the mode is the one holding the switch: it left the pack locked out of
        // serving the house with nothing charging, and no line in the log saying why.
        _manualHold.Set(true, "test");

        await RunAsync(Charging(Now), Idle(Now.AddMinutes(1)), Idle(Now.AddMinutes(4)));

        Assert.Equal(ChargeControlMode.Off, _mode.Mode);
        Assert.False(_inverter.Applied[^1]);
        Assert.False(_manualHold.Hold);
    }

    [Fact]
    public async Task TheModeArmsTheHoldThroughTheSwitch_SoItsLevelMatchesWhatIsOnScreen()
    {
        // Home Assistant publishes what is *armed*, not what was asked for. A hold armed beside the
        // switch shows ON there while the switch itself still reads false, and the owner's OFF is then
        // a Set(false) that changes nothing and raises no event.
        await RunAsync(Charging(Now), Charging(Now.AddMinutes(1)));

        Assert.True(_manualHold.Hold);
        Assert.True(_inverter.Applied[^1]);
    }

    [Fact]
    public async Task TheOwnerCanReleaseTheHoldWhileTheChargeRuns_AndItStaysReleased()
    {
        // The §7 requirement of #119: armed by default, but the switch means something. Turning it off
        // mid-charge used to move the switch in HA and change nothing at all.
        var released = false;
        _inverter.OnApply = _ =>
        {
            if (!released && _manualHold.Hold)
            {
                released = true;
                _manualHold.Set(false, "owner");
            }
        };

        await RunAsync(Charging(Now), Charging(Now.AddMinutes(1)), Charging(Now.AddMinutes(2)));

        Assert.True(released);
        Assert.False(_inverter.Applied[^1]);

        // ...and the car is still charging flat out. Releasing the hold is not stopping the charge.
        Assert.Equal(ChargeControlMode.FastNoBattery, _mode.Mode);
        Assert.Equal(16, LastTarget);
    }

    // -- The amount (#119). A stopping condition and nothing more: the current stays pinned at the
    // maximum throughout, and the only thing the limit changes is when the mode ends itself.

    [Fact]
    public async Task TheModeEndsItselfWhenTheAmountAskedForHasBeenDelivered()
    {
        // 11040W for two five-minute polls is 1.84kWh, past the 1.5kWh asked for.
        await RunAsync(
            [Charging(Now), Charging(Now.AddMinutes(5)), Charging(Now.AddMinutes(10))],
            limit: new FastChargeLimit(1_500, Now));

        Assert.Equal(ChargeControlMode.Off, _mode.Mode);
        Assert.Equal(0, LastTarget);
        Assert.Contains("Fast target reached", _writes[^1].Reason);
        Assert.Equal(EvChargerMode.Stop, Assert.Single(_charger.ModeWrites).Mode);
        Assert.False(_inverter.Applied[^1]);
        Assert.False(_manualHold.Hold);
    }

    [Fact]
    public async Task TheCurrentStaysAtTheMaximumUntilTheAmountIsMet()
    {
        // The amount does not modulate anything -- that is what the targeted mode is for.
        await RunAsync(
            [Charging(Now), Charging(Now.AddMinutes(5))],
            limit: new FastChargeLimit(50_000, Now));

        Assert.All(_writes, w => Assert.Equal(16, w.Target));
        Assert.Equal(ChargeControlMode.FastNoBattery, _mode.Mode);
    }

    [Fact]
    public async Task ACarThatStopsFirstEndsTheModeAndSaysHowFarShortItGot()
    {
        // The car reached *its* limit before ours. Both are endings; only one of them met the number,
        // and the log has to be able to tell them apart.
        await RunAsync(
            [Charging(Now), Idle(Now.AddMinutes(1)), Idle(Now.AddMinutes(4))],
            limit: new FastChargeLimit(50_000, Now));

        Assert.Equal(ChargeControlMode.Off, _mode.Mode);
        Assert.Contains("charge limit reached", _writes[^1].Reason);
        Assert.Contains("of the 50.0kWh asked for", _writes[^1].Reason);
        Assert.False(_inverter.Applied[^1]);
    }

    [Fact]
    public async Task AnUnplugEndsTheModeAndReleasesTheHold()
    {
        await RunAsync(
            [Charging(Now), Unplugged(Now.AddMinutes(1))],
            limit: new FastChargeLimit(50_000, Now));

        Assert.Equal(ChargeControlMode.Off, _mode.Mode);
        Assert.Contains("unplugged", _writes[^1].Reason);
        Assert.False(_inverter.Applied[^1]);
        Assert.False(_manualHold.Hold);
    }

    [Fact]
    public async Task PressingOffReleasesTheHold()
    {
        // The ending the release must not be hooked to individually -- and the one most likely to be
        // forgotten, because nothing in the fast mode's own code runs when somebody presses Off.
        var pressed = false;
        _inverter.OnApply = _ =>
        {
            if (!pressed)
            {
                pressed = true;
                _mode.Set(ChargeControlMode.Off, "owner");
            }
        };

        await RunAsync(Charging(Now), Charging(Now.AddMinutes(1)), Charging(Now.AddMinutes(2)));

        Assert.True(pressed);
        Assert.False(_inverter.Applied[^1]);
        Assert.False(_manualHold.Hold);
    }

    [Fact]
    public async Task SwitchingStraightToAnotherModeReleasesTheHold()
    {
        // Targeted returns early from AutoHold, so a release written only into its catch-all branch
        // would miss this transition entirely.
        var switched = false;
        _inverter.OnApply = _ =>
        {
            if (!switched)
            {
                switched = true;
                _mode.Set(ChargeControlMode.Targeted, "owner");
            }
        };

        await RunAsync(Charging(Now), Charging(Now.AddMinutes(1)), Charging(Now.AddMinutes(2)));

        Assert.True(switched);
        Assert.False(_inverter.Applied[^1]);
        Assert.False(_manualHold.Hold);
    }

    [Fact]
    public async Task AnIdleCarThatNeverChargedDoesNotEndTheMode()
    {
        // Plugged in and waiting (its own schedule, or still negotiating) for well past the dwell.
        await RunAsync(Idle(Now), Idle(Now.AddMinutes(5)), Idle(Now.AddMinutes(10)));

        Assert.Equal(ChargeControlMode.FastNoBattery, _mode.Mode);
        Assert.Equal(16, LastTarget);
    }

    [Fact]
    public async Task WithoutTheHoldFeatureItStillChargesAtTheMaximum()
    {
        // BatteryHold:Enabled false -- the mode can't keep its promise about the battery (it warns), but
        // a select option that silently did nothing would be worse.
        await RunAsync([Charging(Now), Charging(Now.AddMinutes(1))], batteryHoldEnabled: false);

        Assert.Equal(16, LastTarget);
        Assert.Empty(_inverter.Applied);
    }

    [Fact]
    public async Task InOffTheChargerAndTheInverterAreLeftAlone()
    {
        _mode.Set(ChargeControlMode.Off, "test");

        await RunAsync(Charging(Now), Charging(Now.AddMinutes(1)));

        Assert.Empty(_writes);
        Assert.All(_inverter.Applied, hold => Assert.False(hold));
    }

    private Task RunAsync(params EnergyState[] states) => RunAsync(states, batteryHoldEnabled: true);

    // Drives the real poll loop over a scripted telemetry sequence, then stops it. The service parks on
    // the reader once the script runs out, so exactly these polls happen -- no timing assumptions.
    private async Task RunAsync(EnergyState[] states, bool batteryHoldEnabled = true, FastChargeLimit? limit = null)
    {
        if (limit is not null)
        {
            _fastLimit.Set(limit, "test");
        }

        var chargeControl = new ChargeControlOptions
        {
            Phases = 3,
            MaxChargingCurrentAmps = 16,
            PauseCurrentAmps = 0,
            CompletionDwell = TimeSpan.FromMinutes(2),
            CompletionPowerThresholdWatts = 200,
        };

        var power = new ChargePowerConverter(chargeControl.NominalVoltage, chargeControl.Phases);
        var forecast = new NoForecastService();

        var coordinator = new ChargingControlCoordinator(
            new Dictionary<ChargeControlMode, IChargingController>
            {
                [ChargeControlMode.FastNoBattery] = new FastChargingController(
                    chargeControl.MaxChargingCurrentAmps, chargeControl.CompletionDwell),
            },
            _charger,
            new SurplusMovingAverage(TimeSpan.FromMinutes(3)),
            pauseCurrentAmps: chargeControl.PauseCurrentAmps,
            idlePowerThresholdWatts: chargeControl.CompletionPowerThresholdWatts,
            NullLogger<ChargingControlCoordinator>.Instance);

        var forecastOptions = Options.Create(new ForecastChargeOptions());
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
            TargetedCharge.Provider(forecast, dayPlan, power, chargeControl, forecastOptions),
            FastCharge.Provider(_fastLimit),
            _mode,
            // The real actions over the fake charger: a mode that ends itself has to stop the charger
            // exactly as the Off button does, and that is the code path it goes through.
            new ChargeActions(_charger, _mode, NullLogger<ChargeActions>.Instance),
            _manualHold,
            _inverter,
            _status,
            power,
            Options.Create(new ControllerOptions { PollIntervalSeconds = 0 }),
            Options.Create(chargeControl),
            Options.Create(new BatteryHoldOptions { Enabled = batteryHoldEnabled, DryRun = true }),
            forecastOptions,
            NullLogger<PollingService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await reader.Exhausted.WaitAsync(TimeSpan.FromSeconds(10));
        _writes = [.. _charger.CurrentWrites];
        await service.StopAsync(CancellationToken.None);
    }

    private static EnergyState Charging(DateTimeOffset at) =>
        new(at, BatterySocPercent: 60, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 11040,
            EvChargerStatus.Charging, EvChargerPowerWatts: 11040);

    // Positively reported as having no car, which is an ending -- unlike Unknown, which is a dropped
    // read and must not be.
    private static EnergyState Unplugged(DateTimeOffset at) =>
        new(at, BatterySocPercent: 60, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 300,
            EvChargerStatus.Available, EvChargerPowerWatts: 0);

    // Plugged in, drawing nothing.
    private static EnergyState Idle(DateTimeOffset at) =>
        new(at, BatterySocPercent: 60, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 300,
            EvChargerStatus.SuspendedEv, EvChargerPowerWatts: 0);
}

/// <summary>Replays a fixed telemetry sequence, then parks until the service is stopped.</summary>
internal sealed class ScriptedEnergyStateReader(params EnergyState[] states) : IEnergyStateReader
{
    private readonly Queue<EnergyState> _states = new(states);
    private readonly TaskCompletionSource _exhausted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once every scripted reading has been polled.</summary>
    public Task Exhausted => _exhausted.Task;

    public async Task<EnergyState> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (_states.TryDequeue(out var state))
        {
            return state;
        }

        _exhausted.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("unreachable");
    }
}

/// <summary>Records every hold reconciliation and reports back what it was asked for.</summary>
internal sealed class FakeBatteryDischargeControl : IBatteryDischargeControl
{
    /// <summary>The <c>hold</c> argument of every ApplyAsync call, in order.</summary>
    public List<bool> Applied { get; } = [];

    /// <summary>
    /// Run before each call is recorded, so a test can see what had already happened by the time the
    /// inverter was written to. Ordering within a cycle is behaviour here, not an implementation
    /// detail: the release has to reach the inverter after the charger has been stopped, in the one
    /// cycle rather than a poll later.
    /// </summary>
    public Action<bool>? OnApply { get; set; }

    public Task<BatteryHoldState> ApplyAsync(
        bool hold, double activePowerTargetWatts, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        OnApply?.Invoke(hold);
        Applied.Add(hold);
        return Task.FromResult(new BatteryHoldState(hold, hold ? activePowerTargetWatts : null, hold ? now : null, Wrote: true));
    }
}

/// <summary>No forecast at all: the day plan is unusable, as it is on a cold start or a Solcast outage.</summary>
internal sealed class NoForecastService : ISolarForecastService
{
    public SolarForecast? GetForecastForToday() => null;

    public SolarForecast? GetForecast(DateTimeOffset from, DateTimeOffset to) => null;

    public SolarForecast? GetDayForecast(DateOnly localDate) => null;
}
