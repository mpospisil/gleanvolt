using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gleanvolt.Hosting.Resume;

/// <summary>
/// The runtime numbers that tune the charge modes, as somebody last set them (#421). Each is null until
/// it is changed from the web UI, MQTT or the API, and a null value keeps following its configuration:
/// only a value somebody chose outlives a restart, and a config edit still reaches every value nobody
/// touched.
/// </summary>
public sealed record SavedChargeSettings
{
    /// <summary>Forecasted's minimum battery SOC, in percent.</summary>
    public double? MinBatterySocFloorPercent { get; init; }

    /// <summary>Forecasted's SOC resume margin above that floor, in percent.</summary>
    public double? FloorResumeMarginPercent { get; init; }

    /// <summary>SolarGrid's minimum solar surplus, in watts.</summary>
    public double? MinSolarSurplusWatts { get; init; }

    /// <summary>Low tariff only for FastNoBattery.</summary>
    public bool? FastNoBatteryLowTariffOnly { get; init; }

    /// <summary>Low tariff only for SolarGrid.</summary>
    public bool? SolarGridLowTariffOnly { get; init; }
}

/// <summary>
/// Keeps <see cref="SavedChargeSettings"/> on disk beside the mode (<see cref="ChargeModeFile"/>), so a
/// resumed mode runs with the numbers it ran with before (#421). Separate from the mode's file because
/// the settings are independent of it: they hold with no mode running, and a mode that is not resumed
/// keeps them all the same. Written on every change, which happens only when somebody changes one.
///
/// <para>A file that cannot be read is logged and treated as nothing saved: every value comes from
/// configuration. A failed write is logged and the new value applies until the next restart.</para>
/// </summary>
public sealed class ChargeSettingsFile
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<ChargeSettingsFile> _logger;
    private readonly Lock _gate = new();
    private SavedChargeSettings _current;

    /// <param name="path">The absolute path of the file, whether or not it exists yet.</param>
    /// <param name="logger">Where reads and failed writes are recorded.</param>
    public ChargeSettingsFile(string path, ILogger<ChargeSettingsFile> logger)
    {
        Path = path;
        _logger = logger;
        Saved = Load();
        _current = Saved;
    }

    /// <summary>Where the settings are kept.</summary>
    public string Path { get; }

    /// <summary>What the file held when the service started; all null when it held nothing usable.</summary>
    public SavedChargeSettings Saved { get; }

    /// <summary>Applies <paramref name="change"/> to what is kept and writes the result.</summary>
    public void Update(Func<SavedChargeSettings, SavedChargeSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        lock (_gate)
        {
            var next = change(_current);
            if (next == _current)
            {
                return;
            }

            _current = next;
            Write(next);
        }
    }

    private SavedChargeSettings Load()
    {
        if (!File.Exists(Path))
        {
            return new SavedChargeSettings();
        }

        try
        {
            if (JsonSerializer.Deserialize<SavedChargeSettings>(File.ReadAllText(Path), Json) is { } saved)
            {
                return saved;
            }

            _logger.LogWarning("{Path} holds no settings, so the configured values apply.", Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or NotSupportedException)
        {
            _logger.LogWarning("{Path} could not be read ({Reason}), so the configured values apply.", Path, ex.Message);
        }

        return new SavedChargeSettings();
    }

    // Atomically, as ChargeModeFile writes: a temporary file beside it, then a rename.
    private void Write(SavedChargeSettings value)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
            File.Move(temporary, Path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                "Could not write {Path} ({Reason}); the change applies, but a restart goes back to the earlier value.",
                Path,
                ex.Message);
        }
    }
}
