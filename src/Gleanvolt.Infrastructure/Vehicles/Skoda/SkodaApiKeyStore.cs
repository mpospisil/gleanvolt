using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gleanvolt.Infrastructure.Vehicles.Skoda;

/// <summary>
/// The pasted MyŠkoda API key, in memory and on disk (issue #193).
///
/// <para><b>One holder, shared.</b> The sign-in writes it and the feed reads it on every fetch, so a
/// key submitted on the page is in use by the next fetch without a restart. <see cref="Version"/>
/// moves on every save and every clear, which is how the feed tells "the key it was refused" from "a
/// new key the owner has just pasted" without keeping a second copy of the secret.</para>
///
/// <para><b>Treated as a secret, because it is one.</b> The key is not read-only — it starts and stops
/// charging — so the file is written owner-only (the <c>VwWebsiteSessionStore</c> arrangement), never
/// logged, never rendered, and a failure to save is not allowed to take the process down: the key in
/// memory still works until the process ends.</para>
/// </summary>
public sealed class SkodaApiKeyStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private volatile SkodaApiKey? _current;
    private int _version;

    public SkodaApiKeyStore(string path)
    {
        _path = path;
        _current = Load(path);
    }

    public string Path => _path;

    /// <summary>The key in use, or null when none has been pasted.</summary>
    public SkodaApiKey? Current => _current;

    /// <summary>Moves on every save and clear, so a reader can tell a new key from the one it had.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>
    /// Makes this the key in use and writes it to disk. Returns whether the file stuck — the key is in
    /// use either way, and the caller says a restart will want it again when it did not.
    /// </summary>
    public bool Save(SkodaApiKey key)
    {
        _current = key;
        Interlocked.Increment(ref _version);
        return Write(key);
    }

    /// <summary>
    /// Records a newer expiry for the key already in use, as the API reports it on every response. Does
    /// not move <see cref="Version"/>: it is the same key.
    /// </summary>
    public void Refresh(DateTimeOffset expiresAt)
    {
        if (_current is not { } key || key.ExpiresAt == expiresAt)
        {
            return;
        }

        var refreshed = key with { ExpiresAt = expiresAt };
        _current = refreshed;
        Write(refreshed);
    }

    /// <summary>Forgets the key, in memory and on disk. It still works at Škoda until revoked in the app.</summary>
    public void Clear()
    {
        _current = null;
        Interlocked.Increment(ref _version);

        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing useful to do: the key is already out of use in this process.
        }
    }

    /// <summary>
    /// The saved key, or none. A file that cannot be read is not worth stopping for: it means the page
    /// asks for a key, which it already knows how to do.
    /// </summary>
    private static SkodaApiKey? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var saved = JsonSerializer.Deserialize<SkodaApiKey>(File.ReadAllText(path), Json);
            return string.IsNullOrWhiteSpace(saved?.Key) ? null : saved;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool Write(SkodaApiKey key)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var open = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };

            // Owner-only before the secret is written, rather than restricted after: there is then no
            // moment at which the key sits in a readable file. The mode on creation covers a new file,
            // the explicit set one left over from a looser umask. Windows has no such concept.
            if (!OperatingSystem.IsWindows())
            {
                open.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using var stream = new FileStream(_path, open);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            JsonSerializer.Serialize(stream, key, Json);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>A pasted key and what Škoda said about it when it was checked.</summary>
/// <param name="Key">The secret. Never logged, never rendered, never published.</param>
/// <param name="ExpiresAt">From <c>X-API-Key-Expires-At</c>, when Škoda sent it.</param>
/// <param name="VehicleName">The car's name as the app knows it, for the page to confirm the right car.</param>
public sealed record SkodaApiKey(string Key, DateTimeOffset? ExpiresAt, string? VehicleName)
{
    /// <summary>Kept out of <c>ToString</c>, which a log template or a debugger would otherwise print whole.</summary>
    public override string ToString() => $"SkodaApiKey {{ ExpiresAt = {ExpiresAt:u}, VehicleName = {VehicleName} }}";
}
