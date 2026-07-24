using Microsoft.Extensions.Logging.Abstractions;
using Solax.Core.Enums;
using Solax.Core.Models;
using Solax.Core.Strategies;

namespace Solax.Worker.Tests;

public class ChargingControlCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 11, 0, 0, TimeSpan.Zero);
    private static readonly EvChargerSettings Original = new(EvChargerMode.Eco, 8);

    private readonly FakeEvChargerControl _charger = new();
    private readonly StubChargingController _controller = new();
    private readonly ChargingControlCoordinator _coordinator;

    public ChargingControlCoordinatorTests()
    {
        _coordinator = new ChargingControlCoordinator(
            _controller,
            _charger,
            new SurplusMovingAverage(TimeSpan.FromMinutes(3)),
            NullLogger<ChargingControlCoordinator>.Instance);
    }

    private static EnergyState State(EvChargerStatus status) =>
        new(Now, BatterySocPercent: 50, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 0, status, EvChargerPowerWatts: 0);

    private Task Cycle(EvChargerStatus status) => _coordinator.RunCycleAsync(State(status), CancellationToken.None);

    [Fact]
    public async Task ChargeDecision_BacksUpOriginalAndAppliesTarget()
    {
        _charger.CurrentSettings = Original;
        _controller.NextDecision = new(ChargingControlAction.Charge, new EvChargerSettings(EvChargerMode.Fast, 10), "charge");

        await Cycle(EvChargerStatus.Charging);

        var applied = Assert.Single(_charger.Applied);
        Assert.Equal(Original, applied.Current);
        Assert.Equal(new EvChargerSettings(EvChargerMode.Fast, 10), applied.Target);
        Assert.False(_controller.LastInput!.HasControl); // no control held before this first cycle
    }

    [Fact]
    public async Task PauseDecision_PausesChargerAndReleasesControl()
    {
        _charger.CurrentSettings = Original;
        _controller.NextDecision = new(ChargingControlAction.Charge, new EvChargerSettings(EvChargerMode.Fast, 10), "charge");
        await Cycle(EvChargerStatus.Charging); // takes control

        _controller.NextDecision = new(ChargingControlAction.Pause, null, "no surplus");
        await Cycle(EvChargerStatus.Available); // pause

        Assert.Equal(1, _charger.PauseCount);

        // Control released: a further pause decision is a no-op.
        await Cycle(EvChargerStatus.Available);
        Assert.Equal(1, _charger.PauseCount);
    }

    [Fact]
    public async Task HasControl_IsReportedToTheControllerOnceHeld()
    {
        _charger.CurrentSettings = Original;
        _controller.NextDecision = new(ChargingControlAction.Charge, new EvChargerSettings(EvChargerMode.Fast, 10), "charge");
        await Cycle(EvChargerStatus.Charging);

        _controller.NextDecision = new(ChargingControlAction.Charge, new EvChargerSettings(EvChargerMode.Fast, 16), "more sun");
        await Cycle(EvChargerStatus.Charging);

        Assert.True(_controller.LastInput!.HasControl);
    }

    [Fact]
    public async Task ReleaseControl_PausesOnlyWhenControlIsHeld()
    {
        await _coordinator.ReleaseControlAsync("test", CancellationToken.None);
        Assert.Equal(0, _charger.PauseCount); // never took control

        _controller.NextDecision = new(ChargingControlAction.Charge, new EvChargerSettings(EvChargerMode.Fast, 10), "charge");
        await Cycle(EvChargerStatus.Charging);

        await _coordinator.ReleaseControlAsync("test", CancellationToken.None);
        Assert.Equal(1, _charger.PauseCount);
    }

    [Fact]
    public async Task NoneDecision_WritesNothing()
    {
        _controller.NextDecision = new(ChargingControlAction.None, null, "idle");

        await Cycle(EvChargerStatus.Charging);

        Assert.Empty(_charger.Applied);
    }

    [Fact]
    public async Task StartChargingCommand_IsSentOnceOnTheTransitionIntoCharging()
    {
        _charger.CurrentSettings = Original;
        _controller.NextDecision = new(ChargingControlAction.Charge, new EvChargerSettings(EvChargerMode.Fast, 10), "charge");

        await Cycle(EvChargerStatus.Available); // idle -> charging: must start the session
        Assert.Equal([EvChargerControlCommand.StartCharging], _charger.Commands);

        _controller.NextDecision = new(ChargingControlAction.Charge, new EvChargerSettings(EvChargerMode.Fast, 16), "more sun");
        await Cycle(EvChargerStatus.Charging); // already charging: no repeat start command

        Assert.Equal([EvChargerControlCommand.StartCharging], _charger.Commands);
    }

    [Fact]
    public async Task CloudCycle_PausesWithoutStopping_ThenResumes()
    {
        _charger.CurrentSettings = Original;

        // 1. Surplus available -> take control and start the session.
        _controller.NextDecision = new(ChargingControlAction.Charge, new EvChargerSettings(EvChargerMode.Fast, 16), "surplus");
        await Cycle(EvChargerStatus.Charging);
        Assert.Equal([EvChargerControlCommand.StartCharging], _charger.Commands);

        // 2. Cloud: surplus below the 6A floor -> pause. The session must NOT be torn down.
        _controller.NextDecision = new(ChargingControlAction.Pause, null, "cloud");
        await Cycle(EvChargerStatus.Charging);
        Assert.Equal(1, _charger.PauseCount);
        Assert.DoesNotContain(EvChargerControlCommand.StopCharging, _charger.Commands);

        // 3. Cloud clears -> charging resumes.
        _controller.NextDecision = new(ChargingControlAction.Charge, new EvChargerSettings(EvChargerMode.Fast, 16), "surplus back");
        await Cycle(EvChargerStatus.ChargePaused);

        Assert.Equal(new EvChargerSettings(EvChargerMode.Fast, 16), _charger.CurrentSettings);
        Assert.Equal([EvChargerControlCommand.StartCharging, EvChargerControlCommand.StartCharging], _charger.Commands);
        Assert.DoesNotContain(EvChargerControlCommand.StopCharging, _charger.Commands);
    }

    [Fact]
    public async Task PauseDecision_WithoutControl_WritesNothing()
    {
        _controller.NextDecision = new(ChargingControlAction.Pause, null, "spurious");

        await Cycle(EvChargerStatus.Available);

        Assert.Empty(_charger.Applied);
        Assert.Equal(0, _charger.PauseCount);
    }
}
