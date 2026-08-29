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

        Assert.True(decision!.SessionComplete);

        // Never a current: writing one to a charger that is not ours is what the guard exists to stop.
        Assert.Equal(ChargingControlAction.None, decision.Action);
        Assert.Null(decision.ChargeCurrentAmps);
    }
}
