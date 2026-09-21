using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration.CommandLine;

namespace Gleanvolt.Hosting.Configuration;

/// <summary>
/// The file the web UI writes the installation's edits to (issue #204), and the configuration source
/// that reads it back on the next start.
///
/// <para><b>Where it is.</b> <c>Pv:OverridesPath</c>, default <c>data/pv-system.json</c>, resolved
/// against the content root like the SQLite stores. A setting rather than a fixed path because every
/// deployment keeps its data somewhere else: the <c>./data</c> bind mount under Docker,
/// <c>/var/lib/gleanvolt</c> under the .deb's systemd unit, where the content root is a read-only
/// <c>/opt</c>.</para>
///
/// <para><b>Where it sits in the order.</b> After environment variables, so it wins over them, and
/// before command-line arguments, so an explicit argument still wins over it. The first half is the
/// point: <c>docker-compose.yml</c> always sets the device addresses from <c>.env</c>, with defaults,
/// so if the environment won, the fields most worth editing could never be edited.</para>
///
/// <para><b>What it holds.</b> Only the keys that were edited, in the shape of <c>appsettings.json</c>,
/// so that a key never touched still comes from where it always did, and the file reads as a list of
/// exceptions rather than as a second copy of the configuration.</para>
/// </summary>
public static class PvSystemOverrides
{
    /// <summary>The setting that says where the file is.</summary>
    public const string PathKey = "Pv:OverridesPath";

    /// <summary>Where the file is when <see cref="PathKey"/> is not set, relative to the content root.</summary>
    public const string DefaultPath = "data/pv-system.json";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Adds the overrides file to <paramref name="configuration"/>, reading <see cref="PathKey"/> from
    /// what is configured so far. Placed before the command line when there is one, and last otherwise.
    /// Returns the file's absolute path.
    /// </summary>
    public static string AddPvSystemOverrides(this IConfigurationManager configuration, string contentRoot)
    {
        var configured = configuration[PathKey];
        var path = ResolvePath(string.IsNullOrWhiteSpace(configured) ? DefaultPath : configured, contentRoot);
        var source = new PvSystemOverridesSource(path);

        var commandLine = configuration.Sources
            .Select((candidate, index) => (candidate, index))
            .LastOrDefault(entry => entry.candidate is CommandLineConfigurationSource);

        if (commandLine.candidate is null)
        {
            configuration.Sources.Add(source);
        }
        else
        {
            configuration.Sources.Insert(commandLine.index, source);
        }

        return path;
    }

    /// <summary>The overrides this process started with, or null when the file is not a configuration source.</summary>
    public static PvSystemOverridesProvider? Find(IConfiguration configuration) =>
        (configuration as IConfigurationRoot)?.Providers.OfType<PvSystemOverridesProvider>().LastOrDefault();

    /// <summary>
    /// What the startup log line appends: which keys the file overrode, and where it is. Empty when
    /// it overrode nothing, which is the usual case and not worth a word.
    /// </summary>
    public static string DescribeLoaded(IConfiguration configuration)
    {
        var provider = Find(configuration);

        if (provider is null || provider.LoadedKeys.Count == 0)
        {
            return string.Empty;
        }

        return $"; overridden by {provider.Path} (saved from the web UI): {string.Join(", ", provider.LoadedKeys)}";
    }

    /// <summary>
    /// The keys in the file, flattened to configuration paths. Empty when there is no file.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file exists but is not JSON configuration.</exception>
    public static Dictionary<string, string> Read(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(path))
        {
            return values;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var parsed = new ConfigurationBuilder().AddJsonStream(stream).Build();

            foreach (var (key, value) in parsed.AsEnumerable())
            {
                // Section nodes come back with a null value; only leaves are settings.
                if (value is not null)
                {
                    values[key] = value;
                }
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidDataException)
        {
            throw new InvalidOperationException(
                $"{path} holds the PV system settings saved from the web UI, and it is not valid JSON "
                + $"({ex.Message}). Fix it, or delete it to go back to .env and appsettings.json.",
                ex);
        }

        return values;
    }

    /// <summary>
    /// Replaces the file with <paramref name="values"/>, atomically: a temporary file beside it, then a
    /// rename, so a power cut mid-write leaves either the old file or the new one and never half of
    /// one. No values deletes the file.
    /// </summary>
    public static void Write(string path, IReadOnlyDictionary<string, string> values)
    {
        if (values.Count == 0)
        {
            File.Delete(path);
            return;
        }

        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + ".tmp";
        var open = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };

        // Owner-only, as SkodaApiKeyStore writes: nothing in it is secret, but device addresses decide
        // where Modbus writes go, and nobody else on the box has a reason to change them. Windows has
        // no such mode.
        if (!OperatingSystem.IsWindows())
        {
            open.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temporary, open))
        {
            JsonSerializer.Serialize(stream, ToJson(values), Indented);
        }

        try
        {
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            // An antivirus scanner or the search indexer can hold the target open for a moment, which
            // Windows reports as a sharing violation. Once more after it has had time to let go.
            Thread.Sleep(TimeSpan.FromMilliseconds(250));
            File.Move(temporary, path, overwrite: true);
        }
    }

    private static string ResolvePath(string path, string contentRoot) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(contentRoot, path));

    // Back into the shape of appsettings.json. An index stays an object key ("Chargers": { "0": … }),
    // which the configuration binder reads exactly as it reads an array, and which needs no guessing
    // about gaps.
    private static JsonObject ToJson(IReadOnlyDictionary<string, string> values)
    {
        var root = new JsonObject();

        foreach (var (key, value) in values.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            var segments = key.Split(ConfigurationPath.KeyDelimiter);
            var node = root;

            foreach (var segment in segments[..^1])
            {
                if (node[segment] is not JsonObject child)
                {
                    child = new JsonObject();
                    node[segment] = child;
                }

                node = child;
            }

            node[segments[^1]] = value;
        }

        return root;
    }
}

/// <summary>The overrides file as a configuration source. Read once: no reload on change, by design.</summary>
public sealed class PvSystemOverridesSource(string path) : IConfigurationSource
{
    public string Path { get; } = path;

    public IConfigurationProvider Build(IConfigurationBuilder builder) => new PvSystemOverridesProvider(Path);
}

/// <summary>
/// Supplies the overrides file's keys. Not reloaded when the file changes: a save writes the
/// configuration the next start will use, and the running process keeps the one it started with.
/// </summary>
public sealed class PvSystemOverridesProvider(string path) : ConfigurationProvider
{
    public string Path { get; } = path;

    /// <summary>The keys the file held when this process started, in file order.</summary>
    public IReadOnlyList<string> LoadedKeys { get; private set; } = [];

    public override void Load()
    {
        var values = PvSystemOverrides.Read(Path);
        Data = values.ToDictionary(entry => entry.Key, entry => (string?)entry.Value, StringComparer.OrdinalIgnoreCase);
        LoadedKeys = [.. values.Keys];
    }
}
