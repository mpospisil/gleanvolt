using System.Text.Json;
using System.Text.Json.Nodes;
using Gleanvolt.Devices;

namespace Gleanvolt.Hosting;

/// <summary>
/// The inverter driver's <see cref="IDriverStateStore"/> on disk (issue #323): a flat JSON object of
/// strings, beside the other state on the data bind mount. What it holds is what makes a lasting change to
/// the inverter undoable after a crash -- Huawei's original maximum discharging power -- so a write is
/// flushed to the disk before <see cref="Set"/> returns, and a write that fails throws, which stops the
/// driver from making the change.
/// </summary>
public sealed class DriverStateFile : IDriverStateStore
{
    private readonly ILogger<DriverStateFile> _logger;
    private readonly Lock _lock = new();

    public DriverStateFile(string path, ILogger<DriverStateFile> logger)
    {
        Path = path;
        _logger = logger;
    }

    /// <summary>The file.</summary>
    public string Path { get; }

    public string? Get(string key)
    {
        lock (_lock)
        {
            return (string?)Read()[key];
        }
    }

    public void Set(string key, string? value)
    {
        lock (_lock)
        {
            var values = Read();
            if (value is null)
            {
                if (!values.Remove(key))
                {
                    return;
                }
            }
            else
            {
                values[key] = value;
            }

            Write(values);
        }
    }

    // A file that cannot be read is reported and read as empty: the driver then finds no original, which
    // makes it leave a register at 0 alone rather than guess.
    private JsonObject Read()
    {
        try
        {
            return File.Exists(Path)
                ? JsonNode.Parse(File.ReadAllText(Path)) as JsonObject ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning("{Path} could not be read ({Reason}); the inverter driver's stored state is unavailable.", Path, ex.Message);
            return [];
        }
    }

    // Atomically and durably: a temporary file flushed to the disk, then a rename. An empty object removes
    // the file, so it exists only while something is stored.
    private void Write(JsonObject values)
    {
        if (values.Count == 0)
        {
            File.Delete(Path);
            return;
        }

        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = Path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, values, new JsonSerializerOptions { WriteIndented = true });
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, Path, overwrite: true);
    }
}
