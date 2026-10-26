using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

/// <summary>
/// <b>Low tariff only</b> in the two modes that buy from the grid (issue #274), at the controller: in
/// high tariff Fast and SolarGrid wait (SolarGrid sun or not, decision (a)), the wait is a pause or a
/// stand-down by its length and never a finish, and with no restriction nothing changes. The edge
/// through the coordinator — paused, low tariff starts, the car draws, still Fast — is in
/// <c>LowTariffEdgeTests</c> in the hosting tests, because the idle clock it depends on lives there.
/// </summary>
public class LowTariffOnlyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.FromHours(2));

    private static readonly FastChargingController Fast = new(maxChargingCurrentAmps: 16, completionDwell: TimeSpan.FromMinutes(2));

    // 230V x 3 phases, 6-16A: the floor is 6A = 4140W, and the minimum surplus is 2kW.
    private static readonly SolarGridChargingController SolarGrid = new(
        new ChargePowerConverter(nominalVoltage: 230, phases: 3),
        new SolarGridChargingOptions(
            MinChargingCurrentAmps: 6,
            MaxChargingCurrentAmps: 16,
            CurrentStepAmps: 1,
            ResumeHysteresisWatts: 200,
            MinRunTime: TimeSpan.FromMinutes(10),
            MinPauseTime: TimeSpan.FromMinutes(15),
            CompletionDwell: TimeSpan.FromMinutes(2),
            MinSurplusWatts: 2000));

    private static readonly SolarGridOutlook SunNow =
        new(2000, Now, ForecastUsable: true, NextSunAt: Now, SunUntil: Now.AddHours(4), "sunny");

    private static readonly SolarGridOutlook NoSunLeft =
        new(2000, Now, ForecastUsable: true, NextSunAt: null, SunUntil: null, "no sun left");

    private static TariffGate High(TimeSpan untilLow) => new(TariffRate.High, Now + untilLow);

    private static readonly TariffGate Low = new(TariffRate.Low, Now.AddHours(3));

    private static ChargingControlInput Input(
        TariffGate? tariff,
        double surplus = 0,
        bool charging = true,
        bool evDrewPower = true,
        TimeSpan evIdleFor = default,
        SolarGridOutlook? outlook = null,
        FastChargeProgress? fastCharge = null,
        EvChargerMode chargerMode = EvChargerMode.Fast) =>
        new(
            new EnergyState(Now, 60, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 0, EvChargerStatus.Charging, EvChargerPowerWatts: 0),
            surplus,
            new EvChargerSettings(chargerMode, 16),
            charging,
            EvDrewPower: evDrewPower,
            EvIdleFor: evIdleFor,
            FastCharge: fastCharge,
            Outlook: outlook ?? SunNow,
            LowTariffOnly: tariff);

    // -- Fast

    [Fact]
    public void FastStandsDownThroughALongHighTariffAndSaysUntilWhen()
    {
        var decision = Fast.Decide(Input(High(TimeSpan.FromHours(10))));

        Assert.Equal(ChargingControlAction.StandDown, decision.Action);
        Assert.False(decision.SessionComplete);
        // In the machine's own zone, as every controller's reasons are: the container's TZ is the site's.
        Assert.StartsWith(
            $"High tariff until {Now.AddHours(10).LocalDateTime:HH:mm}; waiting for low tariff (Fast, low tariff only).",
            decision.Reason);
        Assert.Equal(Now.AddHours(10), decision.WaitUntil);
    }

    [Fact]
    public void FastPausesRatherThanStandsDownForAFewMinutes()
    {
        var decision = Fast.Decide(Input(High(TimeSpan.FromMinutes(5))));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.False(decision.SessionComplete);
    }

    [Fact]
    public void FastChargesFlatOutInLowTariff()
    {
        var decision = Fast.Decide(Input(Low));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
        Assert.Equal(16, decision.ChargeCurrentAmps);
    }

    [Fact]
    public void AWaitForLowTariffIsNeverReadAsAFullCar()
    {
        // An hour of no draw, which outside a wait is a finished car. Inside one it is our own doing, and
        // the coordinator says so: it reads "not charging" through the wait.
        var decision = Fast.Decide(Input(High(TimeSpan.FromHours(2)), charging: false, evIdleFor: TimeSpan.FromHours(1)));

        Assert.Null(decision.EndsSession);
    }

    [Fact]
    public void AMetAmountStillEndsTheChargeInHighTariff()
    {
        var met = new FastChargeProgress(new FastChargeLimit(10_000, Now.AddHours(-3)), DeliveredWh: 10_000);

        var decision = Fast.Decide(Input(High(TimeSpan.FromHours(2)), fastCharge: met));

        Assert.Equal(ChargingSessionEndReason.TargetReached, decision.EndsSession);
    }

    [Fact]
    public void WithoutARestrictionFastChargesWhateverTheHour()
    {
        Assert.Equal(ChargingControlAction.Charge, Fast.Decide(Input(tariff: null)).Action);
    }

    [Fact]
    public void AChargerThatIsNotOursIsLeftAloneEvenInHighTariff()
    {
        var decision = Fast.Decide(Input(High(TimeSpan.FromHours(2)), chargerMode: EvChargerMode.Green));

        Assert.NotEqual(ChargingControlAction.StandDown, decision.Action);
    }

    // -- SolarGrid, decision (a): high tariff pauses the car, sun or not

    [Theory]
    [InlineData(3000)] // over the minimum, under the floor: would be bridged from the grid
    [InlineData(6000)] // over the floor: would follow the sun with nothing from the grid
    public void SolarGridWaitsThroughHighTariffWhateverTheSurplus(double surplus)
    {
        var decision = SolarGrid.Decide(Input(High(TimeSpan.FromHours(1)), surplus: surplus, charging: false, evDrewPower: false));

        Assert.Equal(ChargingControlAction.StandDown, decision.Action);
        Assert.Null(decision.ChargeCurrentAmps);
        Assert.Equal(0, decision.GridBridgeWatts);
        Assert.Contains("SolarGrid, low tariff only", decision.Reason);
    }

    [Theory]
    [InlineData(3000)]
    [InlineData(6000)]
    public void SolarGridChargesAsBeforeInLowTariff(double surplus)
    {
        var withTariff = SolarGrid.Decide(Input(Low, surplus: surplus, charging: false, evDrewPower: false));
        var without = SolarGrid.Decide(Input(null, surplus: surplus, charging: false, evDrewPower: false));

        Assert.Equal(ChargingControlAction.Charge, withTariff.Action);
        Assert.Equal(without.ChargeCurrentAmps, withTariff.ChargeCurrentAmps);
        Assert.Equal(without.GridBridgeWatts, withTariff.GridBridgeWatts);
    }

    [Fact]
    public void AHighTariffEdgeStopsARunningSolarGridChargeEvenInsideItsMinimumRunTime()
    {
        var decision = SolarGrid.Decide(Input(High(TimeSpan.FromHours(1)), surplus: 1000, charging: true));

        Assert.Equal(ChargingControlAction.StandDown, decision.Action);
        Assert.False(decision.SessionComplete);
    }

    [Fact]
    public void SolarGridStillEndsAtTheEndOfItsSunInHighTariff()
    {
        var decision = SolarGrid.Decide(Input(High(TimeSpan.FromHours(6)), surplus: 500, charging: false, evDrewPower: false, outlook: NoSunLeft));

        Assert.Equal(ChargingSessionEndReason.NoSunLeftToday, decision.EndsSession);
    }

    [Fact]
    public void AnUnknownNextChangeIsALongWait()
    {
        var decision = SolarGrid.Decide(Input(new TariffGate(TariffRate.High, NextChange: null), surplus: 6000, charging: false));

        Assert.Equal(ChargingControlAction.StandDown, decision.Action);
        Assert.Contains("for as far as the tariff is known", decision.Reason);
    }

    // -- The gate

    [Fact]
    public void TheGateReadsTheTimelineAtAnInstant()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");
        var timeline = TariffExpansion.Expand(
            new TariffTable([new("00:00", "06:00"), new("22:00", "24:00")], []), zone, new DateOnly(2026, 10, 21), prices: null);

        Assert.Equal(new TariffGate(TariffRate.High, Now.AddHours(10)), TariffGate.At(timeline, Now));
        Assert.Null(TariffGate.At(null, Now));
        Assert.Null(TariffGate.At(timeline, Now.AddDays(5)));
    }
}
