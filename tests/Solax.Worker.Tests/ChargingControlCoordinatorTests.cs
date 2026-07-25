using Microsoft.Extensions.Logging.Abstractions;
using Solax.Core.Enums;
using Solax.Core.Models;
using Solax.Core.Strategies;

namespace Solax.Worker.Tests;

public class ChargingControlCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 25, 11, 0, 0, TimeSpan.Zero);

    private readonly FakeEvChargerControl _charger = new();
    private readonly StubChargingController _controller = new();
    private readonly ChargingControlCoordinator _coordinator;

    public ChargingControlCoordinatorTests()
    {
        _coordinator = new ChargingControlCoordinator(
            _controller,
            _charger,
            new SurplusMovingAverage(TimeSpan.FromMinutes(3)),
            pauseCurrentAmps: 0,
            NullLogger<ChargingControlCoordinator>.Instance);
    }

    private static EnergyState State() =>
        new(Now, BatterySocPercent: 50, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 0, EvChargerStatus.Available, EvChargerPowerWatts: 0);

    private Task<ChargeControlCycleResult> Cycle() => _coordinator.RunCycleAsync(State(), CancellationToken.None);

    [Fact]
    public async Task ChargeDecision_WritesTheTargetCurrent()
    {
        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Fast, 6);
        _controller.NextDecision = new(ChargingControlAction.Charge, 16, "charge");

        var result = await Cycle();

        var write = Assert.Single(_charger.CurrentWrites);
        Assert.Equal(6, write.Active);
        Assert.Equal(16, write.Target);
        Assert.Equal(ChargeControlState.Charging, result.State);
    }

    [Fact]
    public async Task PauseDecision_WritesThePauseCurrent()
    {
        _charger.CurrentSettings = new EvChargerSettings(EvChargerMode.Fast, 16);
        _controller.NextDecision = new(ChargingControlAction.Pause, null, "no surplus");

        var result = await Cycle();

        Assert.Equal(0, _charger.LastTarget); // pauseCurrentAmps
        Assert.Equal(ChargeControlState.Paused, result.State);
    }

    [Fact]
    public async Task NoneDecision_WritesNothing()
    {
        _controller.NextDecision = new(ChargingControlAction.None, null, "not Fast");

        await Cycle();

        Assert.Empty(_charger.CurrentWrites);
    }

    [Fact]
    public async Task ChargingState_IsFedBackToTheControllerNextCycle()
    {
        _controller.NextDecision = new(ChargingControlAction.Charge, 10, "charge");

        await Cycle();
        Assert.False(_controller.LastInput!.Charging); // wasn't charging going into the first cycle

        await Cycle();
        Assert.True(_controller.LastInput!.Charging); // charging now, from the previous cycle
    }

    [Fact]
    public async Task ReleaseControl_ResetsChargingState_WithoutWriting()
    {
        _controller.NextDecision = new(ChargingControlAction.Charge, 10, "charge");
        await Cycle(); // charging

        _coordinator.ReleaseControl();
        _charger.CurrentWrites.Clear();

        await Cycle();
        Assert.False(_controller.LastInput!.Charging); // reset; ReleaseControl itself wrote nothing
    }

    [Fact]
    public async Task PauseOnShutdown_PausesOnlyWhenCharging()
    {
        await _coordinator.PauseOnShutdownAsync(CancellationToken.None);
        Assert.Empty(_charger.CurrentWrites); // never charging -> nothing

        _controller.NextDecision = new(ChargingControlAction.Charge, 10, "charge");
        await Cycle();
        _charger.CurrentWrites.Clear();

        await _coordinator.PauseOnShutdownAsync(CancellationToken.None);
        Assert.Equal(0, _charger.LastTarget); // dropped to the pause current
    }
}
