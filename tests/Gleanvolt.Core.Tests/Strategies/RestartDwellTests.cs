using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

/// <summary>
/// The restart dwell every mode that has one asks. The case it exists to refuse is the second: a car the
/// charger started by itself at plug-in has drawn power, but not because this mode asked.
/// </summary>
public class RestartDwellTests
{
    private static readonly TimeSpan MinPause = TimeSpan.FromMinutes(15);

    private static ChargingControlInput Input(
        bool charging = false, bool chargedThisMode = true, TimeSpan? inState = null, bool evDrewPower = true) =>
        new(
            new EnergyState(new DateTimeOffset(2026, 9, 13, 8, 38, 0, TimeSpan.Zero), 36, 0, 0, 0, EvChargerStatus.ChargePaused, 0),
            2_300,
            new EvChargerSettings(EvChargerMode.Fast, 0),
            charging,
            TimeInCurrentState: inState ?? TimeSpan.FromMinutes(5),
            EvDrewPower: evDrewPower,
            ChargedThisMode: chargedThisMode);

    [Fact]
    public void Holds_a_restart_after_this_modes_own_charge_inside_the_minimum_pause()
    {
        Assert.True(RestartDwell.Holds(Input(), MinPause));
    }

    [Fact]
    public void Does_not_hold_before_this_mode_has_charged_even_if_the_car_drew_power()
    {
        Assert.False(RestartDwell.Holds(Input(chargedThisMode: false, inState: TimeSpan.Zero, evDrewPower: true), MinPause));
    }

    [Fact]
    public void Does_not_hold_a_charge_that_is_running()
    {
        Assert.False(RestartDwell.Holds(Input(charging: true), MinPause));
    }

    [Fact]
    public void Lets_go_once_the_minimum_pause_has_passed()
    {
        Assert.False(RestartDwell.Holds(Input(inState: MinPause), MinPause));
    }

    [Fact]
    public void Says_how_far_into_the_wait_it_is()
    {
        Assert.Equal("Paused 5min of the 15min minimum before restarting.", RestartDwell.Reason(Input(), MinPause));
    }
}
