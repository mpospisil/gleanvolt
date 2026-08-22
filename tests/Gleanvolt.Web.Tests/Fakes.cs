using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// A clock that does not move, in a zone that is not the build agent's. Both halves matter: the
/// pages format timestamps in the app's configured zone, and CI must not be able to pass a test that
/// would fail in Prague.
/// </summary>
internal sealed class FixedTimeProvider(DateTimeOffset now, TimeZoneInfo zone) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public override TimeZoneInfo LocalTimeZone { get; } = zone;
}

internal static class Statuses
{
    /// <summary>
    /// A status with every field filled in plausibly, so a test can name only what it is about.
    /// </summary>
    public static ChargeControlStatus Sample(
        DateTimeOffset timestamp,
        ChargeControlMode mode = ChargeControlMode.Off) =>
        new(
            Mode: mode,
            DryRun: false,
            HoldingControl: false,
            State: ChargeControlState.Idle,
            SurplusWatts: null,
            TargetCurrentAmps: null,
            ActiveCurrentAmps: 6,
            BatterySocPercent: 64,
            ChargerStatus: EvChargerStatus.Available,
            CarConnected: false,
            SolarPowerWatts: 1200,
            ForecastSolarPowerWatts: 6100,
            EvChargerPowerWatts: 0,
            EvChargingCurrentAmps: 0,
            BatteryPowerWatts: 400,
            GridPowerWatts: -150,
            BatteryHoldEnabled: false,
            BatteryHoldRequested: false,
            BatteryHoldActive: false,
            BatteryHoldTargetWatts: null,
            Plan: null,
            LoanPowerWatts: 0,
            SessionEnergyWh: 0,
            LoanedTodayWh: 0,
            TomorrowForecastWh: null,
            Timestamp: timestamp);
}

/// <summary>
/// A minimal stand-in for <see cref="ChargeControlModeSelector"/> (which lives in Gleanvolt.Worker and
/// so isn't reachable from here): records every <see cref="Set"/> call and raises
/// <see cref="Changed"/> exactly like the real thing, which is what the dashboard's mode line depends
/// on to notice a mode changing underneath it (e.g. FastNoBattery ending its own session).
/// </summary>
internal sealed class FakeChargeControlModeSelector(ChargeControlMode initialMode = ChargeControlMode.Off)
    : IChargeControlModeSelector
{
    public ChargeControlMode Mode { get; private set; } = initialMode;

    public List<(ChargeControlMode Mode, string Source)> Sets { get; } = [];

    public event Action<ChargeControlMode>? Changed;

    public void Set(ChargeControlMode mode, string source)
    {
        Sets.Add((mode, source));

        if (Mode == mode)
        {
            return;
        }

        Mode = mode;
        Changed?.Invoke(mode);
    }
}

/// <summary>
/// A stand-in for <c>ChargeActions</c> (which lives in the hosting assembly, and writes to a charger):
/// records every press and moves the mode selector the way the real one does, so a page test sees the
/// same state change a press really causes. Set <see cref="Failure"/> to make the charger refuse the
/// use-mode write — a start then leaves the mode alone, exactly as the real action does.
/// </summary>
internal sealed class FakeChargeActions(FakeChargeControlModeSelector selector) : IChargeActions
{
    public List<(ChargeControlMode Mode, string Source)> Starts { get; } = [];

    public List<string> Stops { get; } = [];

    /// <summary>The message a failed use-mode write reports, or null while the charger answers.</summary>
    public string? Failure { get; set; }

    public Task<ChargeActionResult> StartAsync(
        ChargeControlMode mode, string source, CancellationToken cancellationToken = default)
    {
        if (mode == ChargeControlMode.Off)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Off is not a strategy to start.");
        }

        Starts.Add((mode, source));

        if (Failure is { } failure)
        {
            return Task.FromResult(ChargeActionResult.Failed(failure));
        }

        selector.Set(mode, source);
        return Task.FromResult(ChargeActionResult.Success);
    }

    public Task<ChargeActionResult> StopAsync(string source, CancellationToken cancellationToken = default)
    {
        Stops.Add(source);

        // Off is released whether or not the write landed, like the real action.
        selector.Set(ChargeControlMode.Off, source);

        return Task.FromResult(Failure is { } failure
            ? ChargeActionResult.Failed(failure)
            : ChargeActionResult.Success);
    }
}

/// <summary>A minimal stand-in for <see cref="BatteryHoldSelector"/>; see <see cref="FakeChargeControlModeSelector"/>.</summary>
internal sealed class FakeBatteryHoldSelector(bool initialHold = false) : IBatteryHoldSelector
{
    public bool Hold { get; private set; } = initialHold;

    public List<(bool Hold, string Source)> Sets { get; } = [];

    public event Action<bool>? Changed;

    public void Set(bool hold, string source)
    {
        Sets.Add((hold, source));

        if (Hold == hold)
        {
            return;
        }

        Hold = hold;
        Changed?.Invoke(hold);
    }
}

/// <summary>
/// A stand-in for <c>HostShutdown</c> (which lives in Gleanvolt.Worker and owns the real host): records
/// who asked to stop, and — crucially for a test — does not actually stop anything.
/// </summary>
internal sealed class FakeServiceShutdown : IServiceShutdown
{
    public List<string> Requests { get; } = [];

    public void RequestStop(string source) => Requests.Add(source);
}

/// <summary>A minimal stand-in for <see cref="ForecastRuntimeSettings"/>; see <see cref="FakeChargeControlModeSelector"/>.</summary>
internal sealed class FakeForecastRuntimeSettings : IForecastRuntimeSettings
{
    public double DailyEvTargetWh { get; private set; } = 15_000;

    public double SessionEnergyTargetWh { get; private set; }

    public double MinBatterySocFloorPercent { get; private set; } = 50;

    public double FloorResumeMarginPercent { get; private set; } = 5;

    public List<(string Setting, double Value, string Source)> Sets { get; } = [];

    public void SetDailyEvTargetWh(double wattHours, string source)
    {
        DailyEvTargetWh = Math.Max(0, wattHours);
        Sets.Add((nameof(DailyEvTargetWh), DailyEvTargetWh, source));
    }

    public void SetSessionEnergyTargetWh(double wattHours, string source)
    {
        SessionEnergyTargetWh = Math.Max(0, wattHours);
        Sets.Add((nameof(SessionEnergyTargetWh), SessionEnergyTargetWh, source));
    }

    public void SetMinBatterySocFloorPercent(double percent, string source)
    {
        MinBatterySocFloorPercent = Math.Clamp(percent, 0, 100);
        Sets.Add((nameof(MinBatterySocFloorPercent), MinBatterySocFloorPercent, source));
    }

    public void SetFloorResumeMarginPercent(double percent, string source)
    {
        FloorResumeMarginPercent = Math.Clamp(percent, 0, 100);
        Sets.Add((nameof(FloorResumeMarginPercent), FloorResumeMarginPercent, source));
    }
}

/// <summary>
/// A minimal stand-in for <see cref="Gleanvolt.Infrastructure.Sessions.SqliteChargingSessionStore"/>
/// (which lives in Gleanvolt.Infrastructure and pulls in SQLite, neither appropriate for a Blazor
/// component test): an in-memory list queried the same way the real store is, plus a way to make it
/// fail like an unopened or disabled store would.
/// </summary>
internal sealed class FakeChargingSessionStore : IChargingSessionStore
{
    public List<ChargingSession> Sessions { get; } = [];

    public Dictionary<Guid, ChargingSessionDocument> Documents { get; } = [];

    /// <summary>Makes every query throw, the way browsing does when SessionStore:Enabled is false.</summary>
    public bool Unavailable { get; set; }

    public Task<int> InitializeAsync(CancellationToken cancellationToken) => Task.FromResult(0);

    public Task StartSessionAsync(ChargingSession session, CancellationToken cancellationToken)
    {
        Sessions.Add(session);
        return Task.CompletedTask;
    }

    public Task AppendAsync(
        IReadOnlyList<ChargingSessionSample> samples,
        IReadOnlyList<ChargingSessionEvent> events,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CompleteSessionAsync(ChargingSession session, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<ChargingSession>> GetSessionsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();

        IReadOnlyList<ChargingSession> result = Sessions
            .Where(s => s.StartedAt >= from && s.StartedAt < to)
            .OrderByDescending(s => s.StartedAt)
            .ToList();

        return Task.FromResult(result);
    }

    public Task<ChargingSessionDocument?> ExportAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();

        return Task.FromResult(Documents.GetValueOrDefault(sessionId));
    }

    public Task<int> PruneAsync(TimeSpan retention, CancellationToken cancellationToken) => Task.FromResult(0);

    private void ThrowIfUnavailable()
    {
        if (Unavailable)
        {
            throw new InvalidOperationException("The session store is unavailable (test double).");
        }
    }
}

/// <summary>Plausible <see cref="ChargingSession"/>/<see cref="ChargingSessionSample"/> values for tests.</summary>
internal static class TestSessions
{
    public static ChargingSession Sample(
        DateTimeOffset startedAt,
        DateTimeOffset? endedAt = null,
        ChargeControlMode startMode = ChargeControlMode.Solar,
        ChargeControlMode? endMode = null,
        double energyDeliveredWh = 5_000,
        double fromSolarWh = 3_000,
        double fromGridWh = 1_500,
        double fromBatteryWh = 500,
        double loanedWh = 0) =>
        new(
            Id: Guid.NewGuid(),
            StartedAt: startedAt,
            EndedAt: endedAt,
            TimeZoneId: "Europe/Prague",
            StartMode: startMode,
            EndMode: endMode ?? startMode,
            EndReason: endedAt is null ? null : ChargingSessionEndReason.ModeOff,
            StartBatterySocPercent: 60,
            EndBatterySocPercent: endedAt is null ? null : 75,
            EnergyDeliveredWh: energyDeliveredWh,
            FromSolarWh: fromSolarWh,
            FromGridWh: fromGridWh,
            FromBatteryWh: fromBatteryWh,
            LoanedWh: loanedWh,
            PeakChargingPowerWatts: 3_000,
            StartPlan: null,
            ForecastRemainingAtStartWh: null,
            Controlled: true);

    public static ChargingSessionSample Sample(Guid sessionId, DateTimeOffset timestamp, double batterySocPercent) =>
        new(
            SessionId: sessionId,
            Timestamp: timestamp,
            Mode: ChargeControlMode.Solar,
            State: ChargeControlState.Charging,
            ChargerStatus: EvChargerStatus.Charging,
            BatterySocPercent: batterySocPercent,
            SolarPowerWatts: 2_000,
            GridPowerWatts: 0,
            BatteryPowerWatts: 0,
            EvChargerPowerWatts: 2_000,
            EvChargingCurrentAmps: 8,
            ActiveCurrentAmps: 8,
            TargetCurrentAmps: 8,
            FromSolarWatts: 2_000,
            FromGridWatts: 0,
            FromBatteryWatts: 0,
            EnergyDeliveredWh: 0,
            FromSolarWh: 0,
            FromGridWh: 0,
            FromBatteryWh: 0,
            LoanedWh: 0,
            SolarWh: 0,
            ForecastSolarWh: null,
            GridImportWh: 0,
            VehicleSocPercent: null,
            VehicleSocCapturedAt: null,
            VehicleChargeTimeRemainingMinutes: 0,
            VehicleChargeTimeRemainingReported: false,
            SurplusWatts: 2_000,
            LoanPowerWatts: 0,
            BatteryHoldActive: false,
            ForecastPowerWatts: null,
            PlanRemainingPvWh: null,
            PlanFeasibleEvEnergyWh: null,
            PlanRequiredSocFloorPercent: null);
}

/// <summary>Plausible <see cref="SolarDayPlan"/> values for tests, standing in for <see cref="Strategies.SolarDayPlanner"/>'s output.</summary>
internal static class TestPlans
{
    public static SolarDayPlan Usable(
        DateTimeOffset now,
        DayOutlook outlook = DayOutlook.Tight,
        (DateTimeOffset Start, DateTimeOffset End)? window = null,
        IReadOnlyList<SolarDayPlanTimelinePoint>? timeline = null) => new(
            RemainingPvWh: 9_000,
            ShoulderEnergyWh: 2_000,
            PlateauEnergyWh: 7_000,
            PlateauClaimedByBatteryWh: 500,
            ExpectedHouseWh: 1_500,
            BatteryToFullWh: 2_500,
            EvBudgetWh: 5_000,
            FeasibleEvEnergyWh: 4_500,
            NextFeasibleWindow: window ?? (now.AddHours(1), now.AddHours(3)),
            RequiredSocFloorPercent: 62,
            TrajectorySocFloorPercent: 62,
            ShortfallWh: 1_000,
            EvExpectedTodayWh: 6_000,
            EvTargetWh: 15_000,
            Outlook: outlook,
            BiasFactor: 0.97,
            Deadline: now.AddHours(6),
            ForecastAsOf: now.AddHours(-1),
            IsUsable: true,
            Reason: "Tight day: 4.5kWh for the car, window 1h from now, SOC floor 62%.",
            Timeline: timeline ?? DefaultTimeline(now));

    public static SolarDayPlan Unavailable(DateTimeOffset now) =>
        SolarDayPlan.Unavailable(now.AddHours(6), "no forecast fetched yet");

    private static IReadOnlyList<SolarDayPlanTimelinePoint> DefaultTimeline(DateTimeOffset now) =>
    [
        new(now, now.AddMinutes(30), 500, false, 70),
        new(now.AddMinutes(30), now.AddHours(1), 5_500, true, 65),
        new(now.AddHours(1), now.AddHours(1.5), 5_500, true, 62),
    ];
}

/// <summary>
/// An in-memory <see cref="IEnergyIntervalStore"/> for the energy viewer, filtering on the same
/// half-open window the SQLite store does so a page test can prove it asked for the right day.
/// </summary>
internal sealed class FakeEnergyIntervalStore : IEnergyIntervalStore
{
    public List<EnergyInterval> Intervals { get; } = [];

    /// <summary>Makes every query throw, the way browsing does when EnergyMonitor:Enabled is false.</summary>
    public bool Unavailable { get; set; }

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task AppendAsync(IReadOnlyList<EnergyInterval> intervals, CancellationToken cancellationToken)
    {
        Intervals.AddRange(intervals);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EnergyInterval>> GetIntervalsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        if (Unavailable)
        {
            throw new InvalidOperationException("The energy interval store is unavailable (test double).");
        }

        IReadOnlyList<EnergyInterval> result = Intervals
            .Where(i => i.PeriodStart >= from && i.PeriodStart < to)
            .OrderBy(i => i.PeriodStart)
            .ToList();

        return Task.FromResult(result);
    }

    public Task<int> PruneAsync(TimeSpan retention, CancellationToken cancellationToken) => Task.FromResult(0);
}

/// <summary>Plausible <see cref="EnergyInterval"/> values for tests.</summary>
internal static class TestIntervals
{
    public static EnergyInterval Sample(
        DateTimeOffset periodStart,
        double solarKwh = 1.0,
        double? forecastSolarKwh = 0.9,
        double gridImportKwh = 0.1,
        double gridExportKwh = 0.2,
        double evKwh = 0.5,
        double batteryChargeKwh = 0.2,
        double batteryDischargeKwh = 0,
        double socStartPercent = 60,
        double socEndPercent = 62,
        TimeSpan? covered = null) =>
        new(
            PeriodStart: periodStart,
            PeriodEnd: periodStart.AddMinutes(15),
            TimeZoneId: "Europe/Prague",
            LocalDate: DateOnly.FromDateTime(periodStart.UtcDateTime),
            SolarKwh: solarKwh,
            ForecastSolarKwh: forecastSolarKwh,
            GridImportKwh: gridImportKwh,
            GridExportKwh: gridExportKwh,
            EvKwh: evKwh,
            BatteryChargeKwh: batteryChargeKwh,
            BatteryDischargeKwh: batteryDischargeKwh,
            SocStartPercent: socStartPercent,
            SocEndPercent: socEndPercent,
            SocMinPercent: Math.Min(socStartPercent, socEndPercent),
            SocMaxPercent: Math.Max(socStartPercent, socEndPercent),
            SocMeanPercent: (socStartPercent + socEndPercent) / 2,
            Covered: covered ?? TimeSpan.FromMinutes(15),
            SampleCount: 180);
}

/// <summary>
/// A minimal stand-in for <c>TargetedChargeSelector</c> (which lives in the hosting assembly): records
/// every call and raises <see cref="Changed"/> exactly like the real thing, which is what the targeted
/// page depends on to notice a request being made or dropped from somewhere else.
/// </summary>
internal sealed class FakeTargetedChargeSelector(TargetedChargeRequest? initial = null) : ITargetedChargeSelector
{
    public TargetedChargeRequest? Request { get; private set; } = initial;

    public List<(TargetedChargeRequest Request, string Source)> Sets { get; } = [];

    public List<string> Clears { get; } = [];

    public event Action<TargetedChargeRequest?>? Changed;

    public void Set(TargetedChargeRequest request, string source)
    {
        Sets.Add((request, source));
        Request = request;
        Changed?.Invoke(request);
    }

    public void Clear(string source)
    {
        Clears.Add(source);

        if (Request is null)
        {
            return;
        }

        Request = null;
        Changed?.Invoke(null);
    }
}

/// <summary>Targeted plans with every field filled in plausibly, so a test can name only what it is about.</summary>
internal static class TestTargetedPlans
{
    public static TargetedChargePlan SolarPlusGrid(DateTimeOffset now) => Plan(
        now,
        TargetedChargeStrategy.SolarPlusGrid,
        solarWh: 14_600,
        gridWh: 7_400,
        gridStart: now.AddHours(6.5));

    public static TargetedChargePlan Solar(DateTimeOffset now) => Plan(
        now,
        TargetedChargeStrategy.Solar,
        solarWh: 22_000,
        gridWh: 0,
        gridStart: null);

    public static TargetedChargePlan Maximum(DateTimeOffset now) => Plan(
        now,
        TargetedChargeStrategy.Maximum,
        solarWh: 0,
        gridWh: 8_300,
        gridStart: now,
        departBy: now.AddHours(2).AddMinutes(10),
        requiredWh: 12_000,
        expectedWh: 8_300,
        shortfallWh: 3_700,
        feasibleDeparture: now.AddHours(3).AddMinutes(5));

    /// <summary>
    /// The weak-sun day: real forecast surplus in the window, none of it clearing the charger's floor,
    /// so the whole need falls to the grid — placed over the best of that sun. Solar share zero,
    /// forecast surplus emphatically not.
    /// </summary>
    public static TargetedChargePlan WeakSun(DateTimeOffset now) => Plan(
        now,
        TargetedChargeStrategy.SolarPlusGrid,
        solarWh: 0,
        gridWh: 14_000,
        gridStart: now.AddHours(2),
        forecastSurplusWh: 6_400);

    /// <summary>The genuinely dark window: no surplus forecast at all, so the import starts at once.</summary>
    public static TargetedChargePlan DarkWindow(DateTimeOffset now) => Plan(
        now,
        TargetedChargeStrategy.SolarPlusGrid,
        solarWh: 0,
        gridWh: 14_000,
        gridStart: now,
        forecastSurplusWh: 0);

    public static TargetedChargePlan Complete(DateTimeOffset now) => Plan(
        now,
        TargetedChargeStrategy.Complete,
        solarWh: 0,
        gridWh: 0,
        gridStart: null,
        deliveredWh: 22_000,
        remainingWh: 0,
        expectedWh: 0);

    private static TargetedChargePlan Plan(
        DateTimeOffset now,
        TargetedChargeStrategy strategy,
        double solarWh,
        double gridWh,
        DateTimeOffset? gridStart,
        DateTimeOffset? departBy = null,
        double requiredWh = 22_000,
        double deliveredWh = 0,
        double? remainingWh = null,
        double? expectedWh = null,
        double shortfallWh = 0,
        DateTimeOffset? feasibleDeparture = null,
        bool isUsable = true,
        double? forecastSurplusWh = null)
    {
        var depart = departBy ?? now.AddHours(9);
        var blocks = new List<TargetedChargeBlock>();

        if (solarWh > 0)
        {
            blocks.Add(new TargetedChargeBlock(
                now.AddHours(12), now.AddHours(16), TargetedChargeSource.Solar, 3_650, solarWh));
        }

        if (gridWh > 0 && gridStart is { } start)
        {
            blocks.Add(new TargetedChargeBlock(start, depart, TargetedChargeSource.Grid, 11_040, gridWh));
        }

        return new TargetedChargePlan(
            Strategy: strategy,
            Now: now,
            DepartBy: depart,
            Deadline: depart.AddMinutes(-15),
            RequiredEnergyWh: requiredWh,
            DeliveredEnergyWh: deliveredWh,
            RemainingEnergyWh: remainingWh ?? requiredWh - deliveredWh,
            SolarEnergyWh: solarWh,
            ForecastSurplusWh: forecastSurplusWh ?? solarWh,
            GridEnergyWh: gridWh,
            CeilingEnergyWh: 90_000,
            ExpectedEnergyWh: expectedWh ?? solarWh + gridWh,
            ShortfallWh: shortfallWh,
            GridStart: gridStart,
            FeasibleDeparture: feasibleDeparture,
            SocFloorPercent: 55,
            BatteryToFullWh: 2_000,
            Blocks: blocks,
            ForecastAsOf: now.AddHours(-1),
            IsUsable: isUsable,
            Reason: "test plan");
    }
}
