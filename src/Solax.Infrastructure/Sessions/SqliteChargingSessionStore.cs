using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Solax.Core.Enums;
using Solax.Core.Interfaces;
using Solax.Core.Models;

namespace Solax.Infrastructure.Sessions;

/// <summary>
/// Stores charging sessions in a single SQLite file.
///
/// <para><b>Why SQLite.</b> The queries this exists to answer — "sessions in July", "solar share by
/// mode" — want a query engine, and the alternative of a second server process is the wrong trade on a
/// Raspberry Pi already giving most of its memory to Home Assistant. One file also means the whole
/// store is a single artefact to back up or copy off the device.</para>
///
/// <para><b>Kind to the SD card.</b> WAL journalling with <c>synchronous=NORMAL</c>, and the recording
/// worker batches its appends rather than committing per poll. At a 30-second sample cadence a year of
/// daily charging is well under 200k rows, so size is never the constraint — write amplification on
/// flash is.</para>
///
/// <para><b>Timestamps are stored as UTC</b> in ISO-8601, which is both unambiguous and lexically
/// sortable. Local time is not lost: every session carries the IANA zone it ran in, which is what a
/// viewer needs to answer "which evening was this?" — an offset frozen at write time would be wrong
/// for half the year anyway.</para>
/// </summary>
public sealed class SqliteChargingSessionStore : IChargingSessionStore, IDisposable
{
    /// <summary>The schema this build writes, tracked in SQLite's own <c>user_version</c>.</summary>
    public const int SchemaVersion = 1;

    // One writer at a time. SQLite would serialise these itself and hand back SQLITE_BUSY; taking the
    // lock in-process turns a retry-and-hope into a wait, and the only writer is the recording worker.
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly ILogger<SqliteChargingSessionStore> _logger;

    public SqliteChargingSessionStore(string databasePath, ILogger<SqliteChargingSessionStore> logger)
    {
        _databasePath = Path.GetFullPath(databasePath);
        _logger = logger;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
    }

    /// <summary>Where the database file lives, resolved to an absolute path.</summary>
    public string DatabasePath => _databasePath;

    public async Task<int> InitializeAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await MigrateAsync(connection, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Charging session store ready at {Path} (schema v{Version}).", _databasePath, SchemaVersion);

        return await RecoverInterruptedSessionsAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public async Task StartSessionAsync(ChargingSession session, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO sessions (
                    id, started_at, ended_at, time_zone_id, start_mode, end_mode, end_reason,
                    start_soc_percent, end_soc_percent, energy_delivered_wh, from_solar_wh,
                    from_grid_wh, from_battery_wh, loaned_wh, peak_power_watts, start_plan_json,
                    forecast_remaining_at_start_wh, controlled)
                VALUES (
                    $id, $started_at, NULL, $time_zone_id, $start_mode, $end_mode, NULL,
                    $start_soc, NULL, 0, 0, 0, 0, 0, 0, $start_plan, $forecast_remaining, $controlled);
                """;

            command.Parameters.AddWithValue("$id", session.Id.ToString());
            command.Parameters.AddWithValue("$started_at", Utc(session.StartedAt));
            command.Parameters.AddWithValue("$time_zone_id", session.TimeZoneId);
            command.Parameters.AddWithValue("$start_mode", session.StartMode.ToString());
            command.Parameters.AddWithValue("$end_mode", session.EndMode.ToString());
            command.Parameters.AddWithValue("$start_soc", session.StartBatterySocPercent);
            command.Parameters.AddWithValue("$start_plan", (object?)SerializePlan(session.StartPlan) ?? DBNull.Value);
            command.Parameters.AddWithValue("$forecast_remaining", (object?)session.ForecastRemainingAtStartWh ?? DBNull.Value);
            command.Parameters.AddWithValue("$controlled", session.Controlled ? 1 : 0);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task AppendAsync(
        IReadOnlyList<ChargingSessionSample> samples,
        IReadOnlyList<ChargingSessionEvent> events,
        CancellationToken cancellationToken)
    {
        if (samples.Count == 0 && events.Count == 0)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            if (samples.Count > 0)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO session_samples (
                        session_id, timestamp, mode, state, charger_status, battery_soc_percent,
                        solar_power_watts, grid_power_watts, battery_power_watts,
                        ev_charger_power_watts, ev_charging_current_amps, active_current_amps,
                        target_current_amps, from_solar_watts, from_grid_watts, from_battery_watts,
                        energy_delivered_wh, from_solar_wh, from_grid_wh, from_battery_wh, loaned_wh,
                        surplus_watts, loan_power_watts, battery_hold_active, forecast_power_watts,
                        plan_remaining_pv_wh, plan_feasible_ev_energy_wh, plan_required_soc_floor_percent)
                    VALUES (
                        $session_id, $timestamp, $mode, $state, $charger_status, $soc,
                        $solar, $grid, $battery,
                        $ev_power, $ev_amps, $active_amps,
                        $target_amps, $from_solar_w, $from_grid_w, $from_battery_w,
                        $delivered_wh, $from_solar_wh, $from_grid_wh, $from_battery_wh, $loaned_wh,
                        $surplus, $loan_power, $hold, $forecast_power,
                        $plan_remaining, $plan_feasible, $plan_floor);
                    """;

                // Parameters are created once and rebound per row: SQLite prepares the statement on
                // first execution and reuses it, which is what makes a few hundred inserts one commit
                // rather than a few hundred round trips.
                var p = command.Parameters;
                p.Add("$session_id", SqliteType.Text);
                p.Add("$timestamp", SqliteType.Text);
                p.Add("$mode", SqliteType.Text);
                p.Add("$state", SqliteType.Text);
                p.Add("$charger_status", SqliteType.Text);
                p.Add("$soc", SqliteType.Real);
                p.Add("$solar", SqliteType.Real);
                p.Add("$grid", SqliteType.Real);
                p.Add("$battery", SqliteType.Real);
                p.Add("$ev_power", SqliteType.Real);
                p.Add("$ev_amps", SqliteType.Integer);
                p.Add("$active_amps", SqliteType.Integer);
                p.Add("$target_amps", SqliteType.Integer);
                p.Add("$from_solar_w", SqliteType.Real);
                p.Add("$from_grid_w", SqliteType.Real);
                p.Add("$from_battery_w", SqliteType.Real);
                p.Add("$delivered_wh", SqliteType.Real);
                p.Add("$from_solar_wh", SqliteType.Real);
                p.Add("$from_grid_wh", SqliteType.Real);
                p.Add("$from_battery_wh", SqliteType.Real);
                p.Add("$loaned_wh", SqliteType.Real);
                p.Add("$surplus", SqliteType.Real);
                p.Add("$loan_power", SqliteType.Real);
                p.Add("$hold", SqliteType.Integer);
                p.Add("$forecast_power", SqliteType.Real);
                p.Add("$plan_remaining", SqliteType.Real);
                p.Add("$plan_feasible", SqliteType.Real);
                p.Add("$plan_floor", SqliteType.Real);

                foreach (var sample in samples)
                {
                    p["$session_id"].Value = sample.SessionId.ToString();
                    p["$timestamp"].Value = Utc(sample.Timestamp);
                    p["$mode"].Value = sample.Mode.ToString();
                    p["$state"].Value = sample.State.ToString();
                    p["$charger_status"].Value = sample.ChargerStatus.ToString();
                    p["$soc"].Value = sample.BatterySocPercent;
                    p["$solar"].Value = sample.SolarPowerWatts;
                    p["$grid"].Value = sample.GridPowerWatts;
                    p["$battery"].Value = sample.BatteryPowerWatts;
                    p["$ev_power"].Value = sample.EvChargerPowerWatts;
                    p["$ev_amps"].Value = sample.EvChargingCurrentAmps;
                    p["$active_amps"].Value = (object?)sample.ActiveCurrentAmps ?? DBNull.Value;
                    p["$target_amps"].Value = (object?)sample.TargetCurrentAmps ?? DBNull.Value;
                    p["$from_solar_w"].Value = sample.FromSolarWatts;
                    p["$from_grid_w"].Value = sample.FromGridWatts;
                    p["$from_battery_w"].Value = sample.FromBatteryWatts;
                    p["$delivered_wh"].Value = sample.EnergyDeliveredWh;
                    p["$from_solar_wh"].Value = sample.FromSolarWh;
                    p["$from_grid_wh"].Value = sample.FromGridWh;
                    p["$from_battery_wh"].Value = sample.FromBatteryWh;
                    p["$loaned_wh"].Value = sample.LoanedWh;
                    p["$surplus"].Value = (object?)sample.SurplusWatts ?? DBNull.Value;
                    p["$loan_power"].Value = sample.LoanPowerWatts;
                    p["$hold"].Value = sample.BatteryHoldActive ? 1 : 0;
                    p["$forecast_power"].Value = (object?)sample.ForecastPowerWatts ?? DBNull.Value;
                    p["$plan_remaining"].Value = (object?)sample.PlanRemainingPvWh ?? DBNull.Value;
                    p["$plan_feasible"].Value = (object?)sample.PlanFeasibleEvEnergyWh ?? DBNull.Value;
                    p["$plan_floor"].Value = (object?)sample.PlanRequiredSocFloorPercent ?? DBNull.Value;

                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            if (events.Count > 0)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO session_events (session_id, timestamp, kind, detail)
                    VALUES ($session_id, $timestamp, $kind, $detail);
                    """;

                var p = command.Parameters;
                p.Add("$session_id", SqliteType.Text);
                p.Add("$timestamp", SqliteType.Text);
                p.Add("$kind", SqliteType.Text);
                p.Add("$detail", SqliteType.Text);

                foreach (var sessionEvent in events)
                {
                    p["$session_id"].Value = sessionEvent.SessionId.ToString();
                    p["$timestamp"].Value = Utc(sessionEvent.Timestamp);
                    p["$kind"].Value = sessionEvent.Kind.ToString();
                    p["$detail"].Value = sessionEvent.Detail;

                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task CompleteSessionAsync(ChargingSession session, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE sessions SET
                    ended_at = $ended_at,
                    end_mode = $end_mode,
                    end_reason = $end_reason,
                    end_soc_percent = $end_soc,
                    energy_delivered_wh = $delivered,
                    from_solar_wh = $from_solar,
                    from_grid_wh = $from_grid,
                    from_battery_wh = $from_battery,
                    loaned_wh = $loaned,
                    peak_power_watts = $peak,
                    controlled = $controlled
                WHERE id = $id;
                """;

            command.Parameters.AddWithValue("$id", session.Id.ToString());
            command.Parameters.AddWithValue("$ended_at", (object?)(session.EndedAt is { } e ? Utc(e) : null) ?? DBNull.Value);
            command.Parameters.AddWithValue("$end_mode", session.EndMode.ToString());
            command.Parameters.AddWithValue("$end_reason", (object?)session.EndReason?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$end_soc", (object?)session.EndBatterySocPercent ?? DBNull.Value);
            command.Parameters.AddWithValue("$delivered", session.EnergyDeliveredWh);
            command.Parameters.AddWithValue("$from_solar", session.FromSolarWh);
            command.Parameters.AddWithValue("$from_grid", session.FromGridWh);
            command.Parameters.AddWithValue("$from_battery", session.FromBatteryWh);
            command.Parameters.AddWithValue("$loaned", session.LoanedWh);
            command.Parameters.AddWithValue("$peak", session.PeakChargingPowerWatts);
            command.Parameters.AddWithValue("$controlled", session.Controlled ? 1 : 0);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<ChargingSession>> GetSessionsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SessionColumns} FROM sessions
            WHERE started_at >= $from AND started_at < $to
            ORDER BY started_at DESC;
            """;
        command.Parameters.AddWithValue("$from", Utc(from));
        command.Parameters.AddWithValue("$to", Utc(to));

        var sessions = new List<ChargingSession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sessions.Add(ReadSession(reader));
        }

        return sessions;
    }

    public async Task<ChargingSessionDocument?> ExportAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        ChargingSession? session;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {SessionColumns} FROM sessions WHERE id = $id;";
            command.Parameters.AddWithValue("$id", sessionId.ToString());

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            session = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSession(reader) : null;
        }

        if (session is null)
        {
            return null;
        }

        var samples = await ReadSamplesAsync(connection, sessionId, cancellationToken).ConfigureAwait(false);
        var events = await ReadEventsAsync(connection, sessionId, cancellationToken).ConfigureAwait(false);

        return ChargingSessionDocument.Create(session, samples, events);
    }

    public async Task<int> PruneAsync(TimeSpan retention, CancellationToken cancellationToken)
    {
        if (retention <= TimeSpan.Zero)
        {
            return 0;
        }

        var cutoff = Utc(DateTimeOffset.UtcNow - retention);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            // Children first, and only ever for sessions that are already closed: an open session is
            // being written to right now, and no retention policy should be able to delete it.
            const string Filter = "SELECT id FROM sessions WHERE ended_at IS NOT NULL AND ended_at < $cutoff";

            async Task<int> ExecuteAsync(string sql)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = sql;
                command.Parameters.AddWithValue("$cutoff", cutoff);
                return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await ExecuteAsync($"DELETE FROM session_samples WHERE session_id IN ({Filter});").ConfigureAwait(false);
            await ExecuteAsync($"DELETE FROM session_events WHERE session_id IN ({Filter});").ConfigureAwait(false);

            // Counted from the parent delete only: this is "how many sessions went", not how many rows.
            var removed = await ExecuteAsync("DELETE FROM sessions WHERE ended_at IS NOT NULL AND ended_at < $cutoff;")
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            if (removed > 0)
            {
                _logger.LogInformation("Pruned {Count} charging session(s) older than {Days:F0} days.", removed, retention.TotalDays);
            }

            return removed;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        _writeLock.Dispose();

        // Pooling keeps the native handle to the file open behind the scenes even after every
        // SqliteConnection using it has been disposed. Unix lets you delete a file out from under an
        // open handle; Windows does not, so a caller that deletes the database right after disposing
        // the store (as the test suite does) would otherwise get "file in use". Clearing the pool
        // here releases it deterministically instead of waiting on finalization.
        using var connection = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(connection);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var pragma = connection.CreateCommand();

        // journal_mode is persisted in the file; synchronous is per-connection and has to be set every
        // time. NORMAL rather than FULL: with WAL it still survives an application crash, and only a
        // sudden power loss can cost the last commit — a fair trade for a fraction of the SD-card
        // writes on a device that is powered off by pulling the plug.
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return connection;
    }

    private static async Task MigrateAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var version = await ReadUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (version >= SchemaVersion)
        {
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Migrations are an ordered, idempotent list: each step moves the file forward one version and
        // a fresh file simply runs all of them. `user_version` is SQLite's own integer for exactly
        // this, so no bookkeeping table is needed.
        foreach (var (target, sql) in Migrations)
        {
            if (version >= target)
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var setVersion = connection.CreateCommand())
        {
            setVersion.Transaction = (SqliteTransaction)transaction;

            // PRAGMA doesn't take parameters; the value is a compile-time constant of ours, not input.
            setVersion.CommandText = $"PRAGMA user_version = {SchemaVersion};";
            await setVersion.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadUserVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Closes anything the previous run left open. The session is ended at its last recorded sample
    /// rather than at startup — the service was not charging in between — and its totals are taken
    /// from that sample, which is exactly why the running totals are carried on every row.
    /// </summary>
    private async Task<int> RecoverInterruptedSessionsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var open = new List<ChargingSession>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {SessionColumns} FROM sessions WHERE ended_at IS NULL;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                open.Add(ReadSession(reader));
            }
        }

        if (open.Count == 0)
        {
            return 0;
        }

        foreach (var session in open)
        {
            var samples = await ReadSamplesAsync(connection, session.Id, cancellationToken).ConfigureAwait(false);
            var last = samples.Count > 0 ? samples[^1] : null;

            var recovered = session with
            {
                EndedAt = last?.Timestamp ?? session.StartedAt,
                EndReason = ChargingSessionEndReason.Interrupted,
                EndMode = last?.Mode ?? session.StartMode,
                EndBatterySocPercent = last?.BatterySocPercent ?? session.StartBatterySocPercent,
                EnergyDeliveredWh = last?.EnergyDeliveredWh ?? 0,
                FromSolarWh = last?.FromSolarWh ?? 0,
                FromGridWh = last?.FromGridWh ?? 0,
                FromBatteryWh = last?.FromBatteryWh ?? 0,
                LoanedWh = last?.LoanedWh ?? 0,
                PeakChargingPowerWatts = samples.Count > 0 ? samples.Max(s => s.EvChargerPowerWatts) : 0,
            };

            await CompleteSessionAsync(recovered, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning(
            "Recovered {Count} charging session(s) left open by a previous run; each was ended at its last sample.",
            open.Count);

        return open.Count;
    }

    private static async Task<List<ChargingSessionSample>> ReadSamplesAsync(
        SqliteConnection connection,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT timestamp, mode, state, charger_status, battery_soc_percent, solar_power_watts,
                   grid_power_watts, battery_power_watts, ev_charger_power_watts,
                   ev_charging_current_amps, active_current_amps, target_current_amps,
                   from_solar_watts, from_grid_watts, from_battery_watts, energy_delivered_wh,
                   from_solar_wh, from_grid_wh, from_battery_wh, loaned_wh, surplus_watts,
                   loan_power_watts, battery_hold_active, forecast_power_watts, plan_remaining_pv_wh,
                   plan_feasible_ev_energy_wh, plan_required_soc_floor_percent
            FROM session_samples WHERE session_id = $id ORDER BY timestamp, rowid;
            """;
        command.Parameters.AddWithValue("$id", sessionId.ToString());

        var samples = new List<ChargingSessionSample>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            samples.Add(new ChargingSessionSample(
                SessionId: sessionId,
                Timestamp: Instant(reader.GetString(0)),
                Mode: Enum.Parse<ChargeControlMode>(reader.GetString(1)),
                State: Enum.Parse<ChargeControlState>(reader.GetString(2)),
                ChargerStatus: Enum.Parse<EvChargerStatus>(reader.GetString(3)),
                BatterySocPercent: reader.GetDouble(4),
                SolarPowerWatts: reader.GetDouble(5),
                GridPowerWatts: reader.GetDouble(6),
                BatteryPowerWatts: reader.GetDouble(7),
                EvChargerPowerWatts: reader.GetDouble(8),
                EvChargingCurrentAmps: reader.GetInt32(9),
                ActiveCurrentAmps: NullableInt(reader, 10),
                TargetCurrentAmps: NullableInt(reader, 11),
                FromSolarWatts: reader.GetDouble(12),
                FromGridWatts: reader.GetDouble(13),
                FromBatteryWatts: reader.GetDouble(14),
                EnergyDeliveredWh: reader.GetDouble(15),
                FromSolarWh: reader.GetDouble(16),
                FromGridWh: reader.GetDouble(17),
                FromBatteryWh: reader.GetDouble(18),
                LoanedWh: reader.GetDouble(19),
                SurplusWatts: NullableDouble(reader, 20),
                LoanPowerWatts: reader.GetDouble(21),
                BatteryHoldActive: reader.GetInt64(22) != 0,
                ForecastPowerWatts: NullableDouble(reader, 23),
                PlanRemainingPvWh: NullableDouble(reader, 24),
                PlanFeasibleEvEnergyWh: NullableDouble(reader, 25),
                PlanRequiredSocFloorPercent: NullableDouble(reader, 26)));
        }

        return samples;
    }

    private static async Task<List<ChargingSessionEvent>> ReadEventsAsync(
        SqliteConnection connection,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT timestamp, kind, detail FROM session_events
            WHERE session_id = $id ORDER BY timestamp, rowid;
            """;
        command.Parameters.AddWithValue("$id", sessionId.ToString());

        var events = new List<ChargingSessionEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new ChargingSessionEvent(
                sessionId,
                Instant(reader.GetString(0)),
                Enum.Parse<ChargingSessionEventKind>(reader.GetString(1)),
                reader.GetString(2)));
        }

        return events;
    }

    private const string SessionColumns = """
        id, started_at, ended_at, time_zone_id, start_mode, end_mode, end_reason, start_soc_percent,
        end_soc_percent, energy_delivered_wh, from_solar_wh, from_grid_wh, from_battery_wh, loaned_wh,
        peak_power_watts, start_plan_json, forecast_remaining_at_start_wh, controlled
        """;

    private static ChargingSession ReadSession(SqliteDataReader reader) => new(
        Id: Guid.Parse(reader.GetString(0)),
        StartedAt: Instant(reader.GetString(1)),
        EndedAt: reader.IsDBNull(2) ? null : Instant(reader.GetString(2)),
        TimeZoneId: reader.GetString(3),
        StartMode: Enum.Parse<ChargeControlMode>(reader.GetString(4)),
        EndMode: Enum.Parse<ChargeControlMode>(reader.GetString(5)),
        EndReason: reader.IsDBNull(6) ? null : Enum.Parse<ChargingSessionEndReason>(reader.GetString(6)),
        StartBatterySocPercent: reader.GetDouble(7),
        EndBatterySocPercent: NullableDouble(reader, 8),
        EnergyDeliveredWh: reader.GetDouble(9),
        FromSolarWh: reader.GetDouble(10),
        FromGridWh: reader.GetDouble(11),
        FromBatteryWh: reader.GetDouble(12),
        LoanedWh: reader.GetDouble(13),
        PeakChargingPowerWatts: reader.GetDouble(14),
        StartPlan: reader.IsDBNull(15) ? null : DeserializePlan(reader.GetString(15)),
        ForecastRemainingAtStartWh: NullableDouble(reader, 16),
        Controlled: reader.GetInt64(17) != 0);

    private static double? NullableDouble(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static int? NullableInt(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static string? SerializePlan(SolarDayPlan? plan) =>
        plan is null ? null : JsonSerializer.Serialize(plan, ChargingSessionJson.Options);

    private static SolarDayPlan? DeserializePlan(string json) =>
        JsonSerializer.Deserialize<SolarDayPlan>(json, ChargingSessionJson.Options);

    // Round-trip ("O") on the UTC instant: unambiguous, and lexically sortable, which a stored local
    // offset would not be across a daylight-saving change.
    private static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Instant(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static readonly (int Version, string Sql)[] Migrations =
    [
        (1, """
            CREATE TABLE IF NOT EXISTS sessions (
                id                            TEXT PRIMARY KEY,
                started_at                    TEXT NOT NULL,
                ended_at                      TEXT NULL,
                time_zone_id                  TEXT NOT NULL,
                start_mode                    TEXT NOT NULL,
                end_mode                      TEXT NOT NULL,
                end_reason                    TEXT NULL,
                start_soc_percent             REAL NOT NULL,
                end_soc_percent               REAL NULL,
                energy_delivered_wh           REAL NOT NULL DEFAULT 0,
                from_solar_wh                 REAL NOT NULL DEFAULT 0,
                from_grid_wh                  REAL NOT NULL DEFAULT 0,
                from_battery_wh               REAL NOT NULL DEFAULT 0,
                loaned_wh                     REAL NOT NULL DEFAULT 0,
                peak_power_watts              REAL NOT NULL DEFAULT 0,
                start_plan_json               TEXT NULL,
                forecast_remaining_at_start_wh REAL NULL,
                controlled                    INTEGER NOT NULL DEFAULT 1,
                -- Reserved for the cloud uploader: a closed session is immutable, so "has this one
                -- been shipped?" is the only state an upload ever needs to keep.
                synced_at                     TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_sessions_started_at ON sessions (started_at DESC);
            CREATE INDEX IF NOT EXISTS ix_sessions_unsynced ON sessions (synced_at) WHERE ended_at IS NOT NULL;

            CREATE TABLE IF NOT EXISTS session_samples (
                session_id                      TEXT NOT NULL,
                timestamp                       TEXT NOT NULL,
                mode                            TEXT NOT NULL,
                state                           TEXT NOT NULL,
                charger_status                  TEXT NOT NULL,
                battery_soc_percent             REAL NOT NULL,
                solar_power_watts               REAL NOT NULL,
                grid_power_watts                REAL NOT NULL,
                battery_power_watts             REAL NOT NULL,
                ev_charger_power_watts          REAL NOT NULL,
                ev_charging_current_amps        INTEGER NOT NULL,
                active_current_amps             INTEGER NULL,
                target_current_amps             INTEGER NULL,
                from_solar_watts                REAL NOT NULL,
                from_grid_watts                 REAL NOT NULL,
                from_battery_watts              REAL NOT NULL,
                energy_delivered_wh             REAL NOT NULL,
                from_solar_wh                   REAL NOT NULL,
                from_grid_wh                    REAL NOT NULL,
                from_battery_wh                 REAL NOT NULL,
                loaned_wh                       REAL NOT NULL,
                surplus_watts                   REAL NULL,
                loan_power_watts                REAL NOT NULL,
                battery_hold_active             INTEGER NOT NULL,
                forecast_power_watts            REAL NULL,
                plan_remaining_pv_wh            REAL NULL,
                plan_feasible_ev_energy_wh      REAL NULL,
                plan_required_soc_floor_percent REAL NULL
            );

            CREATE INDEX IF NOT EXISTS ix_samples_session ON session_samples (session_id, timestamp);

            CREATE TABLE IF NOT EXISTS session_events (
                session_id TEXT NOT NULL,
                timestamp  TEXT NOT NULL,
                kind       TEXT NOT NULL,
                detail     TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_events_session ON session_events (session_id, timestamp);
            """),
    ];
}
