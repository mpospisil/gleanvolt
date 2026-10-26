using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

public class ChargerOwnershipTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    private static ChargingControlInput Input(
        EvChargerMode chargerMode,
        bool stoodDown = false,
        TimeSpan notFastFor = default) =>
        new(
            new EnergyState(Now, 50, 0, 0, 0, EvChargerStatus.Charging, 0),
            0,
            new EvChargerSettings(chargerMode, 6),
            Charging: true,
            ChargerStoodDown: stoodDown,
            ChargerNotFastFor: notFastFor);

    [Fact]
    public void AChargerInFastIsOursToDrive()
    {
        Assert.Null(ChargerOwnership.NotOurs(Input(EvChargerMode.Fast)));
    }

    [Fact]
    public void AStopWeCommandedIsOursToDrive()
    {
        // The deferred charge's own stand-down. Without this the mode cannot arm the charger it is
        // waiting for -- the 8.5-hour inert overnight charge of 2026-08-28.
        Assert.Null(ChargerOwnership.NotOurs(Input(EvChargerMode.Stop, stoodDown: true, notFastFor: TimeSpan.FromHours(7))));
    }

    [Theory]
    [InlineData(EvChargerMode.Stop)]
    [InlineData(EvChargerMode.Eco)]
    [InlineData(EvChargerMode.Green)]
    public void AModeTheOwnerSetIsLeftAlone(EvChargerMode ownersMode)
    {
        var decision = ChargerOwnership.NotOurs(Input(ownersMode, notFastFor: TimeSpan.FromSeconds(30)));

        Assert.Equal(ChargingControlAction.None, decision!.Action);
        Assert.False(decision.SessionComplete);
        Assert.Contains("leaving it untouched", decision.Reason);
    }

    [Fact]
    public void ABriefJunkReadingIsIgnoredRatherThanBelieved()
    {
        // This installation's charger drops its Modbus link ~45 times a day and reports transient junk
        // use-modes on recovery: all ten lifetime Eco sightings fall in one two-minute window right
        // after "reachable again", interleaved with Stop. Ending a charge on one of those would be the
        // very bug this grace period exists to avoid, and it would fire nightly.
        var decision = ChargerOwnership.NotOurs(Input(EvChargerMode.Eco, notFastFor: TimeSpan.FromSeconds(8)));

        Assert.False(decision!.SessionComplete);
    }

    [Fact]
    public void AChargerThatHasReallyLeftFastEndsTheMode()
    {
        // 2026-08-24: the car finished, the wallbox went Finishing -> Stop, and Targeted then logged
        // "leaving it untouched" every eight seconds for 2 h 19 min because the guard returned before
        // the completion checks could run. The mode has to notice it is driving nothing.
        var decision = ChargerOwnership.NotOurs(Input(EvChargerMode.Stop, notFastFor: TimeSpan.FromMinutes(3)));

        Assert.Equal(ChargingSessionEndReason.ChargerTakenOver, decision!.EndsSession);

        // Never a current: writing one to a charger that is not ours is what the guard exists to stop.
        Assert.Equal(ChargingControlAction.None, decision.Action);
        Assert.Null(decision.ChargeCurrentAmps);
    }

    // -- The car is full (#280)

    private static readonly TimeSpan Dwell = TimeSpan.FromMinutes(2);

    // The poll after a full car on this wallbox: Finishing, Stop, 0 W, with the use-mode switched by the
    // wallbox itself a moment ago.
    private static ChargingControlInput Closed(
        bool charging = true,
        bool wasDrawing = true,
        bool stoodDown = false,
        EvChargerStatus status = EvChargerStatus.Finishing) =>
        new(
            new EnergyState(Now, 50, 0, 0, 0, status, 0),
            0,
            new EvChargerSettings(EvChargerMode.Stop, 16),
            Charging: charging,
            EvDrewPower: true,
            ChargerStoodDown: stoodDown,
            ChargerNotFastFor: TimeSpan.FromSeconds(8),
            EvWasDrawing: wasDrawing);

    [Theory]
    [InlineData(EvChargerStatus.Finishing)]
    [InlineData(EvChargerStatus.SuspendedEv)]
    public void AWallboxThatClosesTheSessionStraightAfterADrawHasAFullCar(EvChargerStatus status)
    {
        // 2026-10-23 13:23, 10-24 21:18 and 10-25 22:13: Charging / Fast at full power, then
        // Finishing / Stop / 0 W one poll later. All three were filed as ChargerTakenOver two minutes on.
        var decision = ChargerOwnership.Check(Closed(status: status), Dwell);

        Assert.Equal(ChargingSessionEndReason.SessionComplete, decision!.EndsSession);
        Assert.Contains("charge limit reached", decision.Reason);
    }

    [Fact]
    public void TheWallboxGivingUpOnOurPauseIsNotAFullCar()
    {
        // Our own 0 A timeout reads Finishing / Stop as well, but after a pause, with nothing drawing.
        var decision = ChargerOwnership.Check(Closed(charging: false, wasDrawing: false), Dwell);

        Assert.Null(decision!.EndsSession);
        Assert.Equal(ChargingControlAction.None, decision.Action);
    }

    [Fact]
    public void ACarThatWasNotDrawingIsNotJudgedFullOnTheStatusAlone()
    {
        // Asked to charge a poll ago, but the car had not started yet: a Finishing then is not a full car.
        var decision = ChargerOwnership.Check(Closed(wasDrawing: false), Dwell);

        Assert.Null(decision!.EndsSession);
    }

    [Fact]
    public void AStandDownOfOursIsNotAFullCar()
    {
        Assert.Null(ChargerOwnership.Check(Closed(stoodDown: true), Dwell));
    }

    [Fact]
    public void AWallboxThatStaysInFastAtZeroWattsEndsOnTheDwell()
    {
        var input = Input(EvChargerMode.Fast) with { EvDrewPower = true, EvIdleFor = Dwell };

        var decision = ChargerOwnership.Check(input, Dwell, " at 3.0kWh");

        Assert.Equal(ChargingSessionEndReason.SessionComplete, decision!.EndsSession);
        Assert.Contains("Car stopped drawing for 2 min (charge limit reached) at 3.0kWh", decision.Reason);
    }
}
