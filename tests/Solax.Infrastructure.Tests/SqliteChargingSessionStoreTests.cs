using Microsoft.Extensions.Logging.Abstractions;
using Solax.Core.Enums;
using Solax.Core.Models;
using Solax.Infrastructure.Sessions;

namespace Solax.Infrastructure.Tests;

/// <summary>
/// Exercises the store against a real SQLite file in a temporary directory — the schema, the SQL and
/// the round-trip of every column are exactly what a fake would fail to check.
/// </summary>
public sealed class SqliteChargingSessionStoreTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 9, 12, 0, 0, TimeSpan.FromHours(2));

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"solax-sessions-{Guid.NewGuid():N}");

    // The store takes an explicit token on every call; nothing in these tests needs to cancel one.
    private static readonly CancellationToken Ct = CancellationToken.None;

    private SqliteChargingSessionStore NewStore() =>
        new(Path.Combine(_directory, "sessions.db"), NullLogger<SqliteChargingSessionStore>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task CreatesItsDirectoryAndFileOnFirstUse()
    {
        using var store = NewStore();

        var recovered = await store.InitializeAsync(Ct);

        Assert.Equal(0, recovered);
        Assert.True(File.Exists(store.DatabasePath));
    }

    [Fact]
    public async Task InitialisingTwiceOverTheSameFileIsHarmless()
    {
        using (var first = NewStore())
        {
            await first.InitializeAsync(Ct);
            await first.StartSessionAsync(OpenSession(), Ct);
        }

        using var second = NewStore();
        await second.InitializeAsync(Ct);

        // The migration ran once; the session written before it is still there (having been recovered,
        // since it was left open) rather than dropped by a re-created schema.
        var sessions = await second.GetSessionsAsync(Noon.AddDays(-1), Noon.AddDays(1), Ct);
        Assert.Single(sessions);
    }

    [Fact]
    public async Task ARecordedSessionComesBackWithEverythingThatWentIn()
    {
        using var store = NewStore();
        await store.InitializeAsync(Ct);

        var session = OpenSession();
        await store.StartSessionAsync(session, Ct);
        await store.AppendAsync(
            [Sample(session.Id, Noon), Sample(session.Id, Noon.AddSeconds(30), evWatts: 8000, targetAmps: 16, activeAmps: 16)],
            [new ChargingSessionEvent(session.Id, Noon, ChargingSessionEventKind.SessionStarted, "Solar took control of a connected car.")],
            Ct);

        await store.CompleteSessionAsync(Closed(session), Ct);

        var document = await store.ExportAsync(session.Id, Ct);

        Assert.NotNull(document);
        Assert.Equal(ChargingSessionDocument.CurrentSchemaVersion, document!.SchemaVersion);
        Assert.Equal(2, document.Samples.Count);
        Assert.Single(document.Events);

        var header = document.Session;
        Assert.Equal(Noon, header.StartedAt);
        Assert.Equal(Noon.AddHours(3), header.EndedAt);
        Assert.Equal(ChargingSessionEndReason.CarUnplugged, header.EndReason);
        Assert.Equal(12_000, header.EnergyDeliveredWh, 0);
        Assert.Equal("Europe/Prague", header.TimeZoneId);
        Assert.True(header.Controlled);
    }

    [Fact]
    public async Task TheFourChargingFiguresSurviveTheRoundTripSeparately()
    {
        using var store = NewStore();
        await store.InitializeAsync(Ct);

        var session = OpenSession();
        await store.StartSessionAsync(session, Ct);

        // A tapering car: the setpoint is still 16 A, the charger reports 16 A back, and the car is
        // taking about 6 A worth of power. All three have to stay distinguishable.
        await store.AppendAsync(
            [Sample(session.Id, Noon, evWatts: 4140, evAmps: 6, targetAmps: 16, activeAmps: 16)],
            [],
            Ct);

        var sample = (await store.ExportAsync(session.Id, Ct))!.Samples.Single();

        Assert.Equal(4140, sample.EvChargerPowerWatts, 0);
        Assert.Equal(6, sample.EvChargingCurrentAmps);
        Assert.Equal(16, sample.ActiveCurrentAmps);
        Assert.Equal(16, sample.TargetCurrentAmps);
    }

    [Fact]
    public async Task AnUnreadableChargerSetpointStaysNullRatherThanBecomingZero()
    {
        using var store = NewStore();
        await store.InitializeAsync(Ct);

        var session = OpenSession();
        await store.StartSessionAsync(session, Ct);
        await store.AppendAsync(
            [Sample(session.Id, Noon, activeAmps: null, targetAmps: null)],
            [],
            Ct);

        var sample = (await store.ExportAsync(session.Id, Ct))!.Samples.Single();

        Assert.Null(sample.ActiveCurrentAmps);
        Assert.Null(sample.TargetCurrentAmps);
    }

    [Fact]
    public async Task TheOpeningPlanRoundTripsIncludingItsChargingWindow()
    {
        using var store = NewStore();
        await store.InitializeAsync(Ct);

        var plan = Plan();
        var session = OpenSession() with { StartPlan = plan, ForecastRemainingAtStartWh = 18_000 };
        await store.StartSessionAsync(session, Ct);

        var stored = (await store.ExportAsync(session.Id, Ct))!.Session;

        Assert.NotNull(stored.StartPlan);

        // The value tuple is the part a default JSON configuration would silently drop, taking the
        // charging window with it.
        Assert.Equal(plan.NextFeasibleWindow, stored.StartPlan!.NextFeasibleWindow);
        Assert.Equal(plan.Outlook, stored.StartPlan.Outlook);
        Assert.Equal(plan.RequiredSocFloorPercent, stored.StartPlan.RequiredSocFloorPercent);
        Assert.Equal(18_000, stored.ForecastRemainingAtStartWh);
    }

    [Fact]
    public async Task ASessionLeftOpenByACrashIsEndedAtItsLastSample()
    {
        var session = OpenSession();

        using (var crashed = NewStore())
        {
            await crashed.InitializeAsync(Ct);
            await crashed.StartSessionAsync(session, Ct);
            await crashed.AppendAsync(
                [
                    Sample(session.Id, Noon, deliveredWh: 0),
                    Sample(session.Id, Noon.AddMinutes(20), evWatts: 8000, deliveredWh: 2600, soc: 71),
                ],
                [],
                Ct);

            // No CompleteSessionAsync: the power went out.
        }

        using var restarted = NewStore();
        var recovered = await restarted.InitializeAsync(Ct);

        Assert.Equal(1, recovered);

        var header = (await restarted.ExportAsync(session.Id, Ct))!.Session;
        Assert.Equal(ChargingSessionEndReason.Interrupted, header.EndReason);
        Assert.Equal(Noon.AddMinutes(20), header.EndedAt);
        Assert.Equal(2600, header.EnergyDeliveredWh, 0);
        Assert.Equal(71, header.EndBatterySocPercent);
        Assert.Equal(8000, header.PeakChargingPowerWatts, 0);
    }

    [Fact]
    public async Task ASessionInterruptedBeforeItsFirstSampleStillCloses()
    {
        var session = OpenSession();

        using (var crashed = NewStore())
        {
            await crashed.InitializeAsync(Ct);
            await crashed.StartSessionAsync(session, Ct);
        }

        using var restarted = NewStore();
        Assert.Equal(1, await restarted.InitializeAsync(Ct));

        var header = (await restarted.ExportAsync(session.Id, Ct))!.Session;
        Assert.Equal(ChargingSessionEndReason.Interrupted, header.EndReason);
        Assert.Equal(session.StartedAt, header.EndedAt);
        Assert.False(header.IsOpen);
    }

    [Fact]
    public async Task SessionsAreListedNewestFirstAndOnlyInsideTheWindow()
    {
        using var store = NewStore();
        await store.InitializeAsync(Ct);

        var older = OpenSession() with { Id = Guid.CreateVersion7(), StartedAt = Noon.AddDays(-2) };
        var newer = OpenSession() with { Id = Guid.CreateVersion7(), StartedAt = Noon };
        var outside = OpenSession() with { Id = Guid.CreateVersion7(), StartedAt = Noon.AddDays(-30) };

        foreach (var session in new[] { older, newer, outside })
        {
            await store.StartSessionAsync(session, Ct);
        }

        var listed = await store.GetSessionsAsync(Noon.AddDays(-7), Noon.AddDays(1), Ct);

        Assert.Equal([newer.Id, older.Id], listed.Select(s => s.Id));
    }

    [Fact]
    public async Task PruningRemovesOldClosedSessionsWithTheirRows()
    {
        using var store = NewStore();
        await store.InitializeAsync(Ct);

        var old = OpenSession() with { Id = Guid.CreateVersion7(), StartedAt = DateTimeOffset.UtcNow.AddDays(-400) };
        await store.StartSessionAsync(old, Ct);
        await store.AppendAsync([Sample(old.Id, old.StartedAt)], [], Ct);
        await store.CompleteSessionAsync(
            old with { EndedAt = old.StartedAt.AddHours(2), EndReason = ChargingSessionEndReason.CarUnplugged },
            Ct);

        var removed = await store.PruneAsync(TimeSpan.FromDays(365), Ct);

        Assert.Equal(1, removed);
        Assert.Null(await store.ExportAsync(old.Id, Ct));
    }

    [Fact]
    public async Task PruningNeverTouchesASessionThatIsStillRunning()
    {
        using var store = NewStore();
        await store.InitializeAsync(Ct);

        // Started long ago and never closed: retention must not be able to delete the session that is
        // being written to right now.
        var running = OpenSession() with { StartedAt = DateTimeOffset.UtcNow.AddDays(-400) };
        await store.StartSessionAsync(running, Ct);

        Assert.Equal(0, await store.PruneAsync(TimeSpan.FromDays(365), Ct));
        Assert.NotNull(await store.ExportAsync(running.Id, Ct));
    }

    [Fact]
    public async Task TimestampsComeBackAsTheSameInstantWhateverOffsetTheyWentInWith()
    {
        using var store = NewStore();
        await store.InitializeAsync(Ct);

        // The same moment written in Prague summer time; stored as UTC and read back as UTC, it must
        // still compare equal — the zone id on the header is what carries "which local evening".
        var session = OpenSession();
        await store.StartSessionAsync(session, Ct);

        var stored = (await store.ExportAsync(session.Id, Ct))!.Session;

        Assert.Equal(session.StartedAt, stored.StartedAt);
        Assert.Equal(session.StartedAt.UtcDateTime, stored.StartedAt.UtcDateTime);
    }

    [Fact]
    public async Task ExportingAnUnknownSessionReturnsNothing()
    {
        using var store = NewStore();
        await store.InitializeAsync(Ct);

        Assert.Null(await store.ExportAsync(Guid.CreateVersion7(), Ct));
    }

    [Fact]
    public async Task AnExportedDocumentSurvivesBeingSerialisedAndReadBack()
    {
        using var store = NewStore();
        await store.InitializeAsync(Ct);

        var session = OpenSession() with { StartPlan = Plan() };
        await store.StartSessionAsync(session, Ct);
        await store.AppendAsync(
            [Sample(session.Id, Noon)],
            [new ChargingSessionEvent(session.Id, Noon, ChargingSessionEventKind.ChargingStarted, "Charging at 6A.")],
            Ct);
        await store.CompleteSessionAsync(Closed(session), Ct);

        var document = (await store.ExportAsync(session.Id, Ct))!;
        var json = ChargingSessionJson.Serialize(document);
        var parsed = ChargingSessionJson.Deserialize(json);

        Assert.NotNull(parsed);
        Assert.Equal(document.SchemaVersion, parsed!.SchemaVersion);
        Assert.Equal(document.Session.Id, parsed.Session.Id);
        Assert.Equal(document.Session.EnergyDeliveredWh, parsed.Session.EnergyDeliveredWh, 3);
        Assert.Equal(document.Samples.Count, parsed.Samples.Count);
        Assert.Equal(ChargingSessionEventKind.ChargingStarted, parsed.Events.Single().Kind);

        // The version has to be on the wire, not merely a property with a default: a consumer reading
        // an object out of a bucket must be able to tell which shape it is holding.
        Assert.Contains("schemaVersion", json);
    }

    private static ChargingSession OpenSession() => new(
        Id: Guid.CreateVersion7(),
        StartedAt: Noon,
        EndedAt: null,
        TimeZoneId: "Europe/Prague",
        StartMode: ChargeControlMode.Solar,
        EndMode: ChargeControlMode.Solar,
        EndReason: null,
        StartBatterySocPercent: 96,
        EndBatterySocPercent: null,
        EnergyDeliveredWh: 0,
        FromSolarWh: 0,
        FromGridWh: 0,
        FromBatteryWh: 0,
        LoanedWh: 0,
        PeakChargingPowerWatts: 0,
        StartPlan: null,
        ForecastRemainingAtStartWh: null,
        Controlled: true);

    private static ChargingSession Closed(ChargingSession session) => session with
    {
        EndedAt = Noon.AddHours(3),
        EndReason = ChargingSessionEndReason.CarUnplugged,
        EndBatterySocPercent = 88,
        EnergyDeliveredWh = 12_000,
        FromSolarWh = 9000,
        FromGridWh = 2000,
        FromBatteryWh = 1000,
        LoanedWh = 800,
        PeakChargingPowerWatts = 11_000,
    };

    private static ChargingSessionSample Sample(
        Guid sessionId,
        DateTimeOffset at,
        double evWatts = 4000,
        int evAmps = 6,
        int? activeAmps = 6,
        int? targetAmps = 6,
        double deliveredWh = 0,
        double soc = 96) => new(
        SessionId: sessionId,
        Timestamp: at,
        Mode: ChargeControlMode.Solar,
        State: ChargeControlState.Charging,
        ChargerStatus: EvChargerStatus.Charging,
        BatterySocPercent: soc,
        SolarPowerWatts: 6000,
        GridPowerWatts: 0,
        BatteryPowerWatts: 0,
        EvChargerPowerWatts: evWatts,
        EvChargingCurrentAmps: evAmps,
        ActiveCurrentAmps: activeAmps,
        TargetCurrentAmps: targetAmps,
        FromSolarWatts: evWatts,
        FromGridWatts: 0,
        FromBatteryWatts: 0,
        EnergyDeliveredWh: deliveredWh,
        FromSolarWh: deliveredWh,
        FromGridWh: 0,
        FromBatteryWh: 0,
        LoanedWh: 0,
        SurplusWatts: 4200,
        LoanPowerWatts: 0,
        BatteryHoldActive: false,
        ForecastPowerWatts: 6100,
        PlanRemainingPvWh: null,
        PlanFeasibleEvEnergyWh: null,
        PlanRequiredSocFloorPercent: null);

    private static SolarDayPlan Plan() => new(
        RemainingPvWh: 18_000,
        ShoulderEnergyWh: 4000,
        PlateauEnergyWh: 14_000,
        PlateauClaimedByBatteryWh: 0,
        ExpectedHouseWh: 3000,
        BatteryToFullWh: 2000,
        EvBudgetWh: 13_000,
        FeasibleEvEnergyWh: 12_000,
        NextFeasibleWindow: (Noon, Noon.AddHours(4)),
        RequiredSocFloorPercent: 55,
        ShortfallWh: 0,
        EvExpectedTodayWh: 12_000,
        EvTargetWh: 15_000,
        Outlook: DayOutlook.Surplus,
        BiasFactor: 1.0,
        Deadline: Noon.AddHours(7),
        ForecastAsOf: Noon.AddHours(-1),
        IsUsable: true,
        Reason: "Plenty of sun left today.");
}
