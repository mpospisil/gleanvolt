using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

public class ChargerWaitTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 14, 0, 0, TimeSpan.Zero);

    private static ChargingControlInput Input(
        bool charging = false,
        TimeSpan pausedFor = default,
        bool stoodDown = false,
        SolarGridOutlook? outlook = null) =>
        new(
            new EnergyState(Now, 60, 0, 0, 0, EvChargerStatus.Charging, 0),
            0,
            new EvChargerSettings(stoodDown ? EvChargerMode.Stop : EvChargerMode.Fast, 0),
            charging,
            TimeInCurrentState: pausedFor,
            ChargerStoodDown: stoodDown,
            Outlook: outlook);

    [Fact]
    public void AShortWaitIsAPause()
    {
        // A cloud inside a sunny period: a pause costs no use-mode writes.
        var decision = ChargerWait.For(Input(), Now.AddMinutes(10), "waiting");

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.Equal("waiting", decision.Reason);
        Assert.Equal(Now.AddMinutes(10), decision.WaitUntil);
    }

    [Fact]
    public void AWaitOfFifteenMinutesOrMoreIsAStandDown()
    {
        var decision = ChargerWait.For(Input(), Now.Add(ChargerWait.StandDownAfter), "waiting");

        Assert.Equal(ChargingControlAction.StandDown, decision.Action);
        Assert.Contains($"until {Now.Add(ChargerWait.StandDownAfter).LocalDateTime:HH:mm}", decision.Reason);
        Assert.False(decision.Appointment);
    }

    [Fact]
    public void AWaitOfUnknownLengthIsAStandDown()
    {
        // Paused however long, this wallbox ends the session itself after about seven minutes and the
        // mode loses its charger (#135, #280).
        var decision = ChargerWait.For(Input(), until: null, "waiting");

        Assert.Equal(ChargingControlAction.StandDown, decision.Action);
        Assert.Contains("Not known for how long", decision.Reason);
        Assert.Null(decision.WaitUntil);
    }

    [Fact]
    public void APauseThatHasLastedFiveMinutesIsStoodDown()
    {
        // Before the wallbox's own timeout gets there first: a short wait that keeps getting longer.
        var decision = ChargerWait.For(Input(pausedFor: ChargerWait.MaxPause), Now.AddMinutes(2), "waiting");

        Assert.Equal(ChargingControlAction.StandDown, decision.Action);
        Assert.Contains("Paused 5 min already", decision.Reason);
    }

    [Fact]
    public void ALongChargeAboutToPauseIsNotMistakenForALongPause()
    {
        // TimeInCurrentState is how long it has been charging, not paused.
        var decision = ChargerWait.For(Input(charging: true, pausedFor: TimeSpan.FromHours(2)), Now.AddMinutes(2), "waiting");

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
    }

    [Fact]
    public void AStoodDownChargerStaysStoodDownForAShortWait()
    {
        // Back to Fast only to sit at 0A would be two use-mode writes for nothing; a charge re-arms it.
        var decision = ChargerWait.For(Input(stoodDown: true), Now.AddMinutes(2), "waiting");

        Assert.Equal(ChargingControlAction.StandDown, decision.Action);
        Assert.Equal("waiting", decision.Reason);
    }

    [Fact]
    public void TheNextSunIsTheOutlooksWhenItIsUsable()
    {
        var sun = new SolarGridOutlook(4_000, Now, ForecastUsable: true, NextSunAt: Now.AddHours(1), SunUntil: Now.AddHours(3), "sun later");

        Assert.Equal(Now.AddHours(1), ChargerWait.NextSun(Input(outlook: sun)));
        Assert.Null(ChargerWait.NextSun(Input(outlook: SolarGridOutlook.Unavailable(4_000, Now, "none"))));
        Assert.Null(ChargerWait.NextSun(Input()));
    }

    [Fact]
    public void TheDayIsOverOnlyOnAUsableForecastWithNoSunLeft()
    {
        var none = new SolarGridOutlook(4_000, Now, ForecastUsable: true, NextSunAt: null, SunUntil: null, "no sun left");

        var over = ChargerWait.DayIsOver(Input(outlook: none), "Surplus 0W.");

        Assert.Equal(ChargingSessionEndReason.NoSunLeftToday, over!.EndsSession);
        Assert.Equal(ChargingControlAction.Pause, over.Action);

        // No forecast is not "no sun": the mode stays on, stood down, until its owner switches it off.
        Assert.Null(ChargerWait.DayIsOver(Input(outlook: SolarGridOutlook.Unavailable(4_000, Now, "none")), "x"));
        Assert.Null(ChargerWait.DayIsOver(Input(), "x"));
    }

    [Fact]
    public void ARestartDwellEndsWhenThePauseHasLastedItsMinimum()
    {
        Assert.Equal(Now.AddMinutes(11), ChargerWait.DwellEnds(Input(pausedFor: TimeSpan.FromMinutes(4)), TimeSpan.FromMinutes(15)));
        Assert.Equal(Now, ChargerWait.DwellEnds(Input(pausedFor: TimeSpan.FromMinutes(20)), TimeSpan.FromMinutes(15)));
    }
}
