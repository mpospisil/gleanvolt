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
/// <see cref="Changed"/> exactly like the real thing, which is what the dashboard's mode select
/// depends on to notice a mode changing underneath it (e.g. FastNoBattery ending its own session).
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
