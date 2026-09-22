using System.Text.Json;
using System.Text.Json.Serialization;
using Gleanvolt.Core.Interfaces;

namespace Gleanvolt.Infrastructure.Vehicles.Skoda;

/// <summary>
/// The pasted MyŠkoda API key, in memory and — through <see cref="ISecretStore"/> — on disk (issue #193).
///
/// <para><b>One holder, shared.</b> The sign-in writes it and the feed reads it on every fetch, so a
/// key submitted on the page is in use by the next fetch without a restart. <see cref="Version"/>
/// moves on every save and every clear, which is how the feed tells "the key it was refused" from "a
/// new key the owner has just pasted" without keeping a second copy of the secret.</para>
///
/// <para><b>Treated as a secret, because it is one.</b> The key is not read-only — it starts and stops
/// charging — so it is never logged and never rendered, and a failure to save is not allowed to take
/// the process down: the key in memory still works until the process ends. <b>Where</b> it is kept and
/// what protects it is no longer this type's business (issue #215): it keeps the key's shape and its
/// semantics — an expiring key, a version that moves — and the store keeps the bytes.</para>
/// </summary>
public sealed class SkodaApiKeyStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ISecretStore _secrets;
    private volatile SkodaApiKey? _current;
    private int _version;

    public SkodaApiKeyStore(ISecretStore secrets)
    {
        _secrets = secrets;
        _current = Load(secrets);
    }

    /// <summary>What protects the saved key, for a sentence that has to mention it. Never the key itself.</summary>
    public string Protection => _secrets.Describe();

    /// <summary>The key in use, or null when none has been pasted.</summary>
    public SkodaApiKey? Current => _current;

    /// <summary>Moves on every save and clear, so a reader can tell a new key from the one it had.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>
    /// Makes this the key in use and writes it to the secret store. Returns whether it stuck — the key
    /// is in use either way, and the caller says a restart will want it again when it did not.
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
        _secrets.Delete(SecretNames.SkodaApiKey);
    }

    /// <summary>
    /// The saved key, or none. A stored value that cannot be read back is not worth stopping for: it
    /// means the page asks for a key, which it already knows how to do.
    /// </summary>
    private static SkodaApiKey? Load(ISecretStore secrets)
    {
        if (secrets.Read(SecretNames.SkodaApiKey) is not { } stored)
        {
            return null;
        }

        try
        {
            var saved = JsonSerializer.Deserialize<SkodaApiKey>(stored, Json);
            return string.IsNullOrWhiteSpace(saved?.Key) ? null : saved;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private bool Write(SkodaApiKey key) =>
        _secrets.Write(SecretNames.SkodaApiKey, JsonSerializer.Serialize(key, Json));
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
