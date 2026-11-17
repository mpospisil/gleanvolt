using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gleanvolt.Hosting.Resume;

/// <summary>
/// The running mode and what it needs, kept on disk so a restart can take it up again (#413). The poll
/// loop hands it the mode every cycle; it writes when the mode or its parameters change, and every
/// <see cref="RefreshInterval"/> while a mode runs, so the delivered energy and the last moment
/// Gleanvolt was up are never far behind. A clean shutdown writes once more.
///
/// <para>Generalises <see cref="Forecasting.UntilFullSessionFile"/>, which still counts the days of an
/// until-full session but no longer decides the resume on its own.</para>
///
/// <para>A file that cannot be read is logged and treated as nothing saved: the restart comes back Off,
/// as every restart did before. A failed write is logged and the mode runs on; it only loses the
/// resume.</para>
/// </summary>
public sealed class ChargeModeFile
{
    /// <summary>
    /// How often a running mode is written although nothing about it changed. Short enough that a power
    /// cut loses little delivered energy (about a kWh at 11 kW), long enough to leave an SD card alone.
    /// </summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILogger<ChargeModeFile> _logger;
    private readonly Lock _gate = new();
    private SavedChargeMode? _written;
    private SavedChargeMode? _latest;

    /// <param name="path">The absolute path of the file, whether or not it exists yet.</param>
    /// <param name="logger">Where reads and failed writes are recorded.</param>
    public ChargeModeFile(string path, ILogger<ChargeModeFile> logger)
    {
        Path = path;
        _logger = logger;
        Saved = Load();
    }

    /// <summary>Where the mode is kept.</summary>
    public string Path { get; }

    /// <summary>What the file held when the service started, or null when it held nothing usable.</summary>
    public SavedChargeMode? Saved { get; }

    /// <summary>
    /// Called every poll with the mode as it now stands. Writes when it differs from what was last
    /// written, or when a running mode was last written <see cref="RefreshInterval"/> ago or more.
    /// </summary>
    public void Track(SavedChargeMode current)
    {
        ArgumentNullException.ThrowIfNull(current);

        lock (_gate)
        {
            _latest = current;
            var due = current.Mode != Core.Enums.ChargeControlMode.Off
                && _written is { } written
                && current.SavedAt - written.SavedAt >= RefreshInterval;

            if (current.DiffersFrom(_written) || due)
            {
                Write(current);
            }
        }
    }

    /// <summary>
    /// On a clean shutdown: writes what was last tracked once more, with the delivered energy of the last
    /// poll and stamped <paramref name="now"/>, so the absence is measured from the stop rather than from
    /// the last refresh. Does nothing when nothing was tracked, which leaves a file whose resume was
    /// still pending exactly as it was.
    /// </summary>
    public void Flush(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_latest is { } latest && latest.Mode != Core.Enums.ChargeControlMode.Off)
            {
                Write(latest with { SavedAt = now });
            }
        }
    }

    private SavedChargeMode? Load()
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        try
        {
            if (JsonSerializer.Deserialize<SavedChargeMode>(File.ReadAllText(Path), Json) is { } saved)
            {
                return saved;
            }

            _logger.LogWarning("{Path} names no mode, so nothing is resumed.", Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or NotSupportedException)
        {
            _logger.LogWarning("{Path} could not be read ({Reason}), so nothing is resumed.", Path, ex.Message);
        }

        return null;
    }

    // Atomically, as TariffSettingsFile writes: a temporary file beside it, then a rename.
    private void Write(SavedChargeMode value)
    {
        _written = value;

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
                "Could not write {Path} ({Reason}); {Mode} runs on, but a restart may not resume it.",
                Path,
                ex.Message,
                value.Mode);
        }
    }
}
