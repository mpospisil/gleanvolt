using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

/// <summary>
/// The three shared rules of #280, asked of every mode: how a mode waits, when the car is full, and when
/// the day is over. How much current each mode asks for is not here; that is what makes them different.
/// </summary>
public class EveryModeWaitsAndEndsTheSameWayTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 14, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Dwell = TimeSpan.FromMinutes(2);
    private static readonly ChargePowerConverter Power = new(230, 3);

    public static TheoryData<ChargeControlMode> Modes =>
    [
        ChargeControlMode.Solar,
        ChargeControlMode.Forecasted,
        ChargeControlMode.FastNoBattery,
        ChargeControlMode.Targeted,
        ChargeControlMode.SolarGrid,
    ];

    public static TheoryData<ChargeControlMode> SunModes =>
    [
        ChargeControlMode.Solar,
        ChargeControlMode.Forecasted,
        ChargeControlMode.SolarGrid,
    ];

    private static LiveSolarChargingController Solar() => new(Power, 6, 16, 1, 200, 95, 90, Dwell);

    private static IChargingController Controller(ChargeControlMode mode) => mode switch
    {
        ChargeControlMode.Solar => Solar(),
        ChargeControlMode.Forecasted => new ForecastedChargingController(Power, Solar(), new ForecastedChargingOptions(
            MinChargingCurrentAmps: 6,
            MaxChargingCurrentAmps: 16,
            CurrentStepAmps: 1,
            ResumeHysteresisWatts: 200,
            FloorResumeMarginPercent: 5,
            FloorGuardReserveWatts: 500,
            EnableBatteryLoan: false,
            MaxLoanPowerWatts: 0,
            MinBridgeSurplusWatts: 2000,
            SpillBridgeSurplusWatts: 400,
            MaxDailyLoanWh: 0,
            LoanSocMarginPercent: 5,
            MinRunTime: TimeSpan.FromMinutes(10),
            MinPauseTime: TimeSpan.FromMinutes(15),
            FinalGuardBefore: TimeSpan.FromHours(1),
            CompletionDwell: Dwell)),
        ChargeControlMode.FastNoBattery => new FastChargingController(16, Dwell),
        ChargeControlMode.Targeted => new TargetedChargingController(Power, new TargetedChargingOptions(
            MinChargingCurrentAmps: 6,
            MaxChargingCurrentAmps: 16,
            CurrentStepAmps: 1,
            ResumeHysteresisWatts: 200,
            MinRunTime: TimeSpan.FromMinutes(10),
            MinPauseTime: TimeSpan.FromMinutes(15),
            CompletionDwell: Dwell)),
        ChargeControlMode.SolarGrid => new SolarGridChargingController(Power, new SolarGridChargingOptions(
            MinChargingCurrentAmps: 6,
            MaxChargingCurrentAmps: 16,
            CurrentStepAmps: 1,
            ResumeHysteresisWatts: 200,
            MinRunTime: TimeSpan.FromMinutes(10),
            MinPauseTime: TimeSpan.FromMinutes(15),
            CompletionDwell: Dwell,
            MinSurplusWatts: 2000)),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    // A targeted charge with nothing owed to the grid and its sun in the block it is in: it waits for a
    // dip to pass, as the solar modes do.
    private static TargetedChargePlan TargetedPlan(DateTimeOffset blockStart, double deliveredWh = 2_000) => new(
        Strategy: TargetedChargeStrategy.Solar,
        Now: Now,
        DepartBy: Now.AddHours(17),
        Deadline: Now.AddHours(17).AddMinutes(-15),
        RequiredEnergyWh: 10_000,
        DeliveredEnergyWh: deliveredWh,
        RemainingEnergyWh: 10_000 - deliveredWh,
        SolarEnergyWh: 8_000,
        ForecastSurplusWh: 8_000,
        RequiredPaceWatts: 0,
        GridEnergyWh: 0,
        CeilingEnergyWh: 90_000,
        ExpectedEnergyWh: 8_000,
        ShortfallWh: 0,
        GridStart: null,
        FeasibleDeparture: null,
        SocFloorPercent: 50,
        BatteryToFullWh: 0,
        Blocks: [new TargetedChargeBlock(blockStart, blockStart.AddHours(3), TargetedChargeSource.Solar, 5_000, 15_000)],
        ForecastAsOf: Now,
        IsUsable: true,
        TailEnergyWh: 0,
        HoldUntil: null,
        Reason: "test");

    private static SolarGridOutlook Sun(double minimum, DateTimeOffset? from) => from is { } start
        ? new(minimum, Now, ForecastUsable: true, NextSunAt: start, SunUntil: start.AddHours(3), "sun")
        : new(minimum, Now, ForecastUsable: true, NextSunAt: null, SunUntil: null, "no forecast period left today clears it");

    private static double Minimum(ChargeControlMode mode) => mode == ChargeControlMode.SolarGrid ? 2_000 : Power.AmpsToWatts(6);

    /// <summary>
    /// The input for a mode that has nothing to do right now: a full battery, no surplus, and for the
    /// two modes that only wait on a schedule, a high tariff ending at the same moment as the sun returns.
    /// </summary>
    private static ChargingControlInput Waiting(
        ChargeControlMode mode,
        DateTimeOffset? sunFrom,
        TimeSpan pausedFor = default,
        bool stoodDown = false,
        double surplus = 0) =>
        new(
            new EnergyState(Now, 100, 0, 0, 0, EvChargerStatus.Charging, 0),
            surplus,
            new EvChargerSettings(stoodDown ? EvChargerMode.Stop : EvChargerMode.Fast, 0),
            Charging: false,
            TargetedPlan: mode == ChargeControlMode.Targeted && sunFrom is { } block ? TargetedPlan(block) : null,
            TimeInCurrentState: pausedFor,
            EvDrewPower: true,
            ChargerStoodDown: stoodDown,
            Outlook: SunModes.Contains(mode) ? Sun(Minimum(mode), sunFrom) : null,
            LowTariffOnly: mode == ChargeControlMode.FastNoBattery && sunFrom is { } edge ? new TariffGate(TariffRate.High, edge) : null);

    // -- 1. One rule for waiting

    [Theory]
    [MemberData(nameof(Modes))]
    public void AShortWaitIsAPause(ChargeControlMode mode)
    {
        var decision = Controller(mode).Decide(Waiting(mode, sunFrom: Now.AddMinutes(5)));

        Assert.Equal(ChargingControlAction.Pause, decision.Action);
        Assert.False(decision.SessionComplete);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void AShortWaitThatHasLastedFiveMinutesIsStoodDown(ChargeControlMode mode)
    {
        // Before the wallbox's own timeout (about seven minutes at 0 A) ends the session for us.
        var decision = Controller(mode).Decide(Waiting(mode, sunFrom: Now.AddMinutes(5), pausedFor: ChargerWait.MaxPause));

        Assert.Equal(ChargingControlAction.StandDown, decision.Action);
        Assert.False(decision.SessionComplete);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void ALongWaitIsAStandDownThatSaysUntilWhen(ChargeControlMode mode)
    {
        var decision = Controller(mode).Decide(Waiting(mode, sunFrom: Now.AddHours(2)));

        Assert.Equal(ChargingControlAction.StandDown, decision.Action);
        Assert.Equal(Now.AddHours(2), decision.WaitUntil);
        Assert.False(decision.Appointment);
    }

    [Theory]
    [MemberData(nameof(SunModes))]
    [InlineData(ChargeControlMode.Targeted)]
    public void AStoodDownChargerIsRearmedWhenTheSunComes(ChargeControlMode mode)
    {
        var decision = Controller(mode).Decide(Waiting(mode, sunFrom: Now, stoodDown: true, surplus: 8_000));

        Assert.Equal(ChargingControlAction.Charge, decision.Action);
    }

    // -- 2. One rule for "the car is full"

    [Theory]
    [MemberData(nameof(Modes))]
    public void ACarDrawingThenFinishingAndStopIsAFullCar(ChargeControlMode mode)
    {
        // The wallbox closes the session and switches to Stop inside one poll, with no taper.
        var input = Waiting(mode, sunFrom: Now) with
        {
            State = new EnergyState(Now, 100, 0, 0, 0, EvChargerStatus.Finishing, 0),
            CurrentSettings = new EvChargerSettings(EvChargerMode.Stop, 16),
            Charging = true,
            ChargerNotFastFor = TimeSpan.FromSeconds(8),
            EvWasDrawing = true,
            LowTariffOnly = null,
        };

        var decision = Controller(mode).Decide(input);

        Assert.Equal(ChargingSessionEndReason.SessionComplete, decision.EndsSession);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void ThePauseThenTheWallboxTimeoutIsNotAFullCar(ChargeControlMode mode)
    {
        // Our own 0 A pause running into the wallbox's timeout: Finishing / Stop after nothing drew. Not
        // full; with rule 1 in place it should not happen at all.
        var input = Waiting(mode, sunFrom: Now.AddMinutes(5)) with
        {
            State = new EnergyState(Now, 100, 0, 0, 0, EvChargerStatus.Finishing, 0),
            CurrentSettings = new EvChargerSettings(EvChargerMode.Stop, 0),
            ChargerNotFastFor = TimeSpan.FromSeconds(8),
            TimeInCurrentState = TimeSpan.FromMinutes(7),
        };

        var decision = Controller(mode).Decide(input);

        Assert.NotEqual(ChargingSessionEndReason.SessionComplete, decision.EndsSession);
    }

    // -- 3. One rule for the end of the day

    [Theory]
    [MemberData(nameof(SunModes))]
    public void WithNoSunLeftTodayTheModeEndsItself(ChargeControlMode mode)
    {
        // Solar and Forecasted used to lose the charger to the wallbox's timeout every evening instead,
        // filed as ChargerTakenOver.
        var decision = Controller(mode).Decide(Waiting(mode, sunFrom: null));

        Assert.Equal(ChargingSessionEndReason.NoSunLeftToday, decision.EndsSession);
    }

    [Theory]
    [MemberData(nameof(SunModes))]
    public void WithoutAUsableForecastTheModeStaysOnStoodDown(ChargeControlMode mode)
    {
        var input = Waiting(mode, sunFrom: null) with { Outlook = SolarGridOutlook.Unavailable(Minimum(mode), Now, "no forecast") };

        var decision = Controller(mode).Decide(input);

        Assert.False(decision.SessionComplete);
        Assert.Equal(ChargingControlAction.StandDown, decision.Action);
    }

    // -- 4. Until full (#279): the end of the day becomes a night stood down

    [Fact]
    public void UntilFullStandsTheChargerDownForTomorrowInsteadOfEnding()
    {
        var tomorrow = new DateTimeOffset(2026, 10, 26, 9, 40, 0, TimeSpan.Zero);
        var input = Waiting(ChargeControlMode.Forecasted, sunFrom: null) with { UntilFull = true };
        input = input with { Outlook = input.Outlook! with { TomorrowFirstSunAt = tomorrow } };

        var decision = Controller(ChargeControlMode.Forecasted).Decide(input);

        Assert.False(decision.SessionComplete);
        Assert.Equal(ChargingControlAction.StandDown, decision.Action);
        Assert.Equal(tomorrow, decision.WaitUntil);
        Assert.Contains("tomorrow's first window", decision.Reason);
    }

    [Fact]
    public void UntilFullWithNoWordOnTomorrowStandsDownUntilUnknown()
    {
        var input = Waiting(ChargeControlMode.Forecasted, sunFrom: null) with { UntilFull = true };

        var decision = Controller(ChargeControlMode.Forecasted).Decide(input);

        Assert.False(decision.SessionComplete);
        Assert.Equal(ChargingControlAction.StandDown, decision.Action);
        Assert.Null(decision.WaitUntil);
    }

    [Fact]
    public void UntilFullStillEndsOnAFullCar()
    {
        // The wallbox closing the session straight after a draw is the car's limit, days in or not.
        var input = Waiting(ChargeControlMode.Forecasted, sunFrom: null) with
        {
            UntilFull = true,
            Charging = true,
            EvWasDrawing = true,
            State = new EnergyState(Now, 100, 0, 0, 0, EvChargerStatus.Finishing, 0),
            CurrentSettings = new EvChargerSettings(EvChargerMode.Stop, 0),
        };

        var decision = Controller(ChargeControlMode.Forecasted).Decide(input);

        Assert.Equal(ChargingSessionEndReason.SessionComplete, decision.EndsSession);
    }

    [Fact]
    public void UntilFullOnlyChangesTheModeTheHostSetsItFor()
    {
        // The flag is the input's, set by the host only while Forecasted drives; without it the rule is #280's.
        Assert.NotNull(ChargerWait.DayIsOver(Waiting(ChargeControlMode.Forecasted, sunFrom: null), "Waiting.")?.EndsSession);
        Assert.Null(ChargerWait.DayIsOver(Waiting(ChargeControlMode.Forecasted, sunFrom: null) with { UntilFull = true }, "Waiting.")?.EndsSession);
    }
}
