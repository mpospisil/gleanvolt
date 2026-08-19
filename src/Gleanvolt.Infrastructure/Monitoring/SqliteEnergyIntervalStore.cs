using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Infrastructure.Monitoring;

/// <summary>
/// Stores the site's energy history in a SQLite file of its own.
///
/// <para><b>Its own file, beside the session database rather than inside it.</b> The two stores have
/// separate writers, separate retention and separate reasons to fail, and
/// <see cref="Sessions.SqliteChargingSessionStore"/> serialises its writes on an in-process lock —
/// a second writer holding a different lock over the same file would turn that arrangement back into
/// the <c>SQLITE_BUSY</c> retry loop it exists to avoid. Nothing is lost by the split: an analysis
/// that wants both opens one and <c>ATTACH</c>es the other, and joins across them as if they had
/// always been one database.</para>
///
/// <para><b>One row per interval, written as the interval closes.</b> At the 15-minute default that is
/// 96 writes a day and about 35,000 rows a year — small enough that the batching the session recorder
/// needs would buy nothing here, and rare enough to be kind to the SD card without it.</para>
///
/// <para><b>Appends merge.</b> The primary key is the window's start and a second write for the same
/// window adds to the first rather than replacing it, which is what makes a restart mid-window
/// non-destructive: the stopping process flushes the minutes it saw, the starting one contributes the
/// rest, and the row ends up whole. See <see cref="IEnergyIntervalStore.AppendAsync"/>.</para>
///
/// <para>Timestamps are UTC ISO-8601, unambiguous and lexically sortable, with the IANA zone and the
/// local calendar day carried alongside — the same arrangement, and the same reasoning, as the
/// session store's.</para>
/// </summary>
public sealed class SqliteEnergyIntervalStore : IEnergyIntervalStore, IDisposable
{
    /// <summary>The schema this build writes, tracked in SQLite's own <c>user_version</c>.</summary>
    public const int SchemaVersion = 1;

    // One writer at a time, exactly as in the session store: the monitor worker is the only writer,
    // so taking the lock in-process turns a retry-and-hope into a wait.
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly ILogger<SqliteEnergyIntervalStore> _logger;

    public SqliteEnergyIntervalStore(string databasePath, ILogger<SqliteEnergyIntervalStore> logger)
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

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await MigrateAsync(connection, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Energy interval store ready at {Path} (schema v{Version}).", _databasePath, SchemaVersion);
    }

    public async Task AppendAsync(IReadOnlyList<EnergyInterval> intervals, CancellationToken cancellationToken)
    {
        if (intervals.Count == 0)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = UpsertSql;

            // Parameters created once and rebound per row, so SQLite prepares the statement once and
            // a backlog of intervals costs one commit rather than one round trip each.
            var p = command.Parameters;
            p.Add("$period_start", SqliteType.Text);
            p.Add("$period_end", SqliteType.Text);
            p.Add("$time_zone_id", SqliteType.Text);
            p.Add("$local_date", SqliteType.Text);
            p.Add("$solar", SqliteType.Real);
            p.Add("$forecast_solar", SqliteType.Real);
            p.Add("$grid_import", SqliteType.Real);
            p.Add("$grid_export", SqliteType.Real);
            p.Add("$ev", SqliteType.Real);
            p.Add("$battery_charge", SqliteType.Real);
            p.Add("$battery_discharge", SqliteType.Real);
            p.Add("$soc_start", SqliteType.Real);
            p.Add("$soc_end", SqliteType.Real);
            p.Add("$soc_min", SqliteType.Real);
            p.Add("$soc_max", SqliteType.Real);
            p.Add("$soc_mean", SqliteType.Real);
            p.Add("$covered_seconds", SqliteType.Real);
            p.Add("$sample_count", SqliteType.Integer);

            foreach (var interval in intervals)
            {
                p["$period_start"].Value = Utc(interval.PeriodStart);
                p["$period_end"].Value = Utc(interval.PeriodEnd);
                p["$time_zone_id"].Value = interval.TimeZoneId;
                p["$local_date"].Value = interval.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                p["$solar"].Value = interval.SolarKwh;
                p["$forecast_solar"].Value = (object?)interval.ForecastSolarKwh ?? DBNull.Value;
                p["$grid_import"].Value = interval.GridImportKwh;
                p["$grid_export"].Value = interval.GridExportKwh;
                p["$ev"].Value = interval.EvKwh;
                p["$battery_charge"].Value = interval.BatteryChargeKwh;
                p["$battery_discharge"].Value = interval.BatteryDischargeKwh;
                p["$soc_start"].Value = interval.SocStartPercent;
                p["$soc_end"].Value = interval.SocEndPercent;
                p["$soc_min"].Value = interval.SocMinPercent;
                p["$soc_max"].Value = interval.SocMaxPercent;
                p["$soc_mean"].Value = interval.SocMeanPercent;
                p["$covered_seconds"].Value = interval.Covered.TotalSeconds;
                p["$sample_count"].Value = interval.SampleCount;

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<EnergyInterval>> GetIntervalsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {IntervalColumns} FROM energy_intervals
            WHERE period_start >= $from AND period_start < $to
            ORDER BY period_start;
            """;
        command.Parameters.AddWithValue("$from", Utc(from));
        command.Parameters.AddWithValue("$to", Utc(to));

        var intervals = new List<EnergyInterval>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            intervals.Add(ReadInterval(reader));
        }

        return intervals;
    }

    public async Task<int> PruneAsync(TimeSpan retention, CancellationToken cancellationToken)
    {
        if (retention <= TimeSpan.Zero)
        {
            return 0;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM energy_intervals WHERE period_start < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", Utc(DateTimeOffset.UtcNow - retention));

            var removed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (removed > 0)
            {
                _logger.LogInformation("Pruned {Count} energy interval(s) older than {Retention}.", removed, retention);
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

        // Pooling keeps the native handle open behind the scenes even after every SqliteConnection
        // using it has been disposed, and Windows will not let the file be deleted while it is held.
        // Clearing the pool releases it deterministically instead of waiting on finalization.
        using var connection = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(connection);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var pragma = connection.CreateCommand();

        // journal_mode is persisted in the file; synchronous is per-connection and has to be set every
        // time. NORMAL rather than FULL for the same reason as the session store: with WAL it survives
        // an application crash, and only a sudden power loss can cost the last commit.
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

        // An ordered, idempotent list, as in the session store: each step moves the file forward one
        // version and a fresh file simply runs all of them.
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

    private const string IntervalColumns = """
        period_start, period_end, time_zone_id, local_date, solar_kwh, forecast_solar_kwh,
        grid_import_kwh, grid_export_kwh, ev_kwh, battery_charge_kwh, battery_discharge_kwh,
        soc_start_percent, soc_end_percent, soc_min_percent, soc_max_percent, soc_mean_percent,
        covered_seconds, sample_count
        """;

    private static EnergyInterval ReadInterval(SqliteDataReader reader) => new(
        PeriodStart: Instant(reader.GetString(0)),
        PeriodEnd: Instant(reader.GetString(1)),
        TimeZoneId: reader.GetString(2),
        LocalDate: DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture),
        SolarKwh: reader.GetDouble(4),
        ForecastSolarKwh: reader.IsDBNull(5) ? null : reader.GetDouble(5),
        GridImportKwh: reader.GetDouble(6),
        GridExportKwh: reader.GetDouble(7),
        EvKwh: reader.GetDouble(8),
        BatteryChargeKwh: reader.GetDouble(9),
        BatteryDischargeKwh: reader.GetDouble(10),
        SocStartPercent: reader.GetDouble(11),
        SocEndPercent: reader.GetDouble(12),
        SocMinPercent: reader.GetDouble(13),
        SocMaxPercent: reader.GetDouble(14),
        SocMeanPercent: reader.GetDouble(15),
        Covered: TimeSpan.FromSeconds(reader.GetDouble(16)),
        SampleCount: reader.GetInt32(17));

    // Round-trip ("O") on the UTC instant: unambiguous, and lexically sortable, which a stored local
    // offset would not be across a daylight-saving change.
    private static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Instant(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>
    /// The merging append. Everything additive is summed with what is already stored; the SOC figures
    /// are merged as the statistics they are, and <c>soc_start_percent</c> is left alone because the
    /// row already holds the earlier of the two starts.
    ///
    /// <para>Every unqualified column here reads the <em>existing</em> row and every <c>excluded.</c>
    /// one reads the row being inserted — which is why <c>soc_mean_percent</c> can still weight itself
    /// by the old <c>covered_seconds</c> on the line above the one that grows it: SQLite evaluates the
    /// whole SET list against the pre-update values.</para>
    /// </summary>
    private const string UpsertSql = """
        INSERT INTO energy_intervals (
            period_start, period_end, time_zone_id, local_date, solar_kwh, forecast_solar_kwh,
            grid_import_kwh, grid_export_kwh, ev_kwh, battery_charge_kwh, battery_discharge_kwh,
            soc_start_percent, soc_end_percent, soc_min_percent, soc_max_percent, soc_mean_percent,
            covered_seconds, sample_count)
        VALUES (
            $period_start, $period_end, $time_zone_id, $local_date, $solar, $forecast_solar,
            $grid_import, $grid_export, $ev, $battery_charge, $battery_discharge,
            $soc_start, $soc_end, $soc_min, $soc_max, $soc_mean,
            $covered_seconds, $sample_count)
        ON CONFLICT(period_start) DO UPDATE SET
            period_end            = excluded.period_end,
            time_zone_id          = excluded.time_zone_id,
            local_date            = excluded.local_date,
            solar_kwh             = solar_kwh + excluded.solar_kwh,
            -- Null means "no forecast covered this", which must survive a merge with a half that had
            -- one, and must not be turned into a zero by a half that didn't.
            forecast_solar_kwh    = CASE
                                        WHEN excluded.forecast_solar_kwh IS NULL THEN forecast_solar_kwh
                                        WHEN forecast_solar_kwh IS NULL THEN excluded.forecast_solar_kwh
                                        ELSE forecast_solar_kwh + excluded.forecast_solar_kwh
                                    END,
            grid_import_kwh       = grid_import_kwh + excluded.grid_import_kwh,
            grid_export_kwh       = grid_export_kwh + excluded.grid_export_kwh,
            ev_kwh                = ev_kwh + excluded.ev_kwh,
            battery_charge_kwh    = battery_charge_kwh + excluded.battery_charge_kwh,
            battery_discharge_kwh = battery_discharge_kwh + excluded.battery_discharge_kwh,
            soc_end_percent       = excluded.soc_end_percent,
            soc_min_percent       = MIN(soc_min_percent, excluded.soc_min_percent),
            soc_max_percent       = MAX(soc_max_percent, excluded.soc_max_percent),
            soc_mean_percent      = CASE
                                        WHEN covered_seconds + excluded.covered_seconds > 0
                                        THEN (soc_mean_percent * covered_seconds
                                              + excluded.soc_mean_percent * excluded.covered_seconds)
                                             / (covered_seconds + excluded.covered_seconds)
                                        ELSE excluded.soc_mean_percent
                                    END,
            covered_seconds       = covered_seconds + excluded.covered_seconds,
            sample_count          = sample_count + excluded.sample_count;
        """;

    private static readonly (int Version, string Sql)[] Migrations =
    [
        (1, """
            CREATE TABLE IF NOT EXISTS energy_intervals (
                -- The window's start, in UTC. The primary key, which is what makes an append idempotent
                -- and lets a restart merge into the window it interrupted rather than duplicate it.
                period_start          TEXT PRIMARY KEY,
                period_end            TEXT NOT NULL,
                time_zone_id          TEXT NOT NULL,
                -- The local calendar day, so "group by day" needs no timezone maths in the query.
                local_date            TEXT NOT NULL,
                solar_kwh             REAL NOT NULL DEFAULT 0,
                -- NULL, not 0, when no forecast covered the window: "we didn't know" is not "we
                -- expected nothing", and a chart must be able to break the line rather than draw a dip.
                forecast_solar_kwh    REAL NULL,
                -- Never netted against each other; see Gleanvolt.Core.Models.EnergyInterval.
                grid_import_kwh       REAL NOT NULL DEFAULT 0,
                grid_export_kwh       REAL NOT NULL DEFAULT 0,
                ev_kwh                REAL NOT NULL DEFAULT 0,
                battery_charge_kwh    REAL NOT NULL DEFAULT 0,
                battery_discharge_kwh REAL NOT NULL DEFAULT 0,
                soc_start_percent     REAL NOT NULL,
                soc_end_percent       REAL NOT NULL,
                soc_min_percent       REAL NOT NULL,
                soc_max_percent       REAL NOT NULL,
                -- Time-weighted, not sample-averaged.
                soc_mean_percent      REAL NOT NULL,
                -- How much of the window was actually observed. Short of the full interval, every
                -- energy figure in the row is short by the same fraction -- read this first.
                covered_seconds       REAL NOT NULL DEFAULT 0,
                sample_count          INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS ix_energy_intervals_local_date ON energy_intervals (local_date);
            """),
    ];
}
