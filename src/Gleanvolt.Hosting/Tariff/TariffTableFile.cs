using System.Text.Json;
using System.Text.Json.Serialization;
using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Hosting.Tariff;

/// <summary>
/// The owner's tariff table in <c>data/tariff.json</c> (issue #265): checked by
/// <see cref="TariffRules"/> before anything is written, and held in memory between saves so that
/// <see cref="TariffSchedule"/> never reads the disk on a poll.
///
/// <para><b>Never "always low".</b> A missing file is a tariff that was never set up, and an
/// unreadable one is logged as a warning; both are <see cref="TariffTable.None"/>. A table that could
/// not be read must turn into no restriction, never into a whole day of cheap grid.</para>
/// </summary>
public sealed class TariffTableFile : ITariffEditor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly ILogger<TariffTableFile> _logger;

    // Two browser tabs, or a tab and the API, saving at once must not interleave a check and a write.
    private readonly Lock _gate = new();

    private TariffTable _current;

    /// <param name="path">The absolute path of the file, whether or not it exists yet.</param>
    /// <param name="currency">What the prices are in, from <c>Tariff:Currency</c>.</param>
    /// <param name="logger">Where loads and saves are recorded.</param>
    public TariffTableFile(string path, string currency, ILogger<TariffTableFile> logger)
    {
        _path = path;
        Currency = currency;
        _logger = logger;
        _current = Load();
    }

    public string Currency { get; }

    /// <summary>Where the table is kept.</summary>
    public string Path => _path;

    public event Action? Saved;

    public TariffTable Read()
    {
        lock (_gate)
        {
            return _current;
        }
    }

    public TariffSaveResult Save(TariffTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var problems = TariffRules.Check(table);

        if (problems.Any(problem => !problem.IsWarning))
        {
            return new TariffSaveResult(false, problems);
        }

        var normalized = TariffRules.Normalize(table);

        lock (_gate)
        {
            try
            {
                Write(normalized);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new TariffSaveResult(
                    false, [.. problems, new TariffProblem(TariffField.Table, null, $"Could not write {_path}: {ex.Message}")]);
            }

            _current = normalized;
        }

        _logger.LogInformation(
            "Tariff saved to {Path}: provider {Provider}, {Weekday} weekday and {Weekend} weekend low-tariff periods.",
            _path,
            normalized.Provider,
            normalized.Weekday.Count,
            normalized.Weekend.Count);

        Saved?.Invoke();
        return new TariffSaveResult(true, problems);
    }

    private TariffTable Load()
    {
        if (!File.Exists(_path))
        {
            _logger.LogInformation("No tariff at {Path}; the grid costs the same at every hour until one is saved.", _path);
            return TariffTable.None;
        }

        try
        {
            var table = FromStored(JsonSerializer.Deserialize<StoredTable>(File.ReadAllText(_path), Json));

            if (TariffRules.Check(table).FirstOrDefault(problem => !problem.IsWarning) is { } problem)
            {
                throw new InvalidDataException(problem.Row is { } row
                    ? $"{problem.Field} row {row + 1}: {problem.Message}"
                    : problem.Message);
            }

            return TariffRules.Normalize(table);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            _logger.LogWarning(
                "The tariff at {Path} could not be read ({Reason}), so there is no tariff until it is saved again.",
                _path,
                ex.Message);
            return TariffTable.None;
        }
    }

    private static TariffTable FromStored(StoredTable? stored) =>
        stored is null
            ? throw new InvalidDataException("The file is empty.")
            : new TariffTable(
                stored.Provider,
                [.. stored.Weekday ?? []],
                [.. stored.Weekend ?? []],
                stored.LowPricePerKwh,
                stored.HighPricePerKwh);

    // Atomically, as PvSystemOverrides writes: a temporary file beside it, then a rename, so a power
    // cut mid-write leaves the old table or the new one and never half of one.
    private void Write(TariffTable table)
    {
        var directory = System.IO.Path.GetDirectoryName(_path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = _path + ".tmp";
        var stored = new StoredTable(table.Provider, [.. table.Weekday], [.. table.Weekend], table.LowPricePerKwh, table.HighPricePerKwh);
        File.WriteAllText(temporary, JsonSerializer.Serialize(stored, Json));

        try
        {
            File.Move(temporary, _path, overwrite: true);
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            // A scanner or the indexer holding the target open for a moment; see PvSystemOverrides.Write.
            Thread.Sleep(TimeSpan.FromMilliseconds(250));
            File.Move(temporary, _path, overwrite: true);
        }
    }

    // The file's shape, apart from the domain record so that a missing list reads as empty and the
    // property order on disk is the one the README shows.
    private sealed record StoredTable(
        TariffProvider Provider,
        List<TariffPeriod>? Weekday,
        List<TariffPeriod>? Weekend,
        decimal? LowPricePerKwh,
        decimal? HighPricePerKwh);
}
