namespace Gleanvolt.Api;

/// <summary>
/// Configuration for the HTTP API. Bound from the <c>"Api"</c> section.
///
/// <para>Off by default, unlike the web UI. Two of these endpoints write to hardware, and the
/// project's rule for anything that writes is that an operator switches it on knowingly. The UI can
/// afford to default to open because it is a browser on a LAN with a person in front of it; a
/// non-interactive control surface that a program drives cannot.</para>
/// </summary>
public sealed class ApiOptions
{
    public const string SectionName = "Api";

    /// <summary>
    /// Master on/off switch, off by default. While false no route is mapped and no OpenAPI
    /// document is served: there is nothing to find, not merely nothing permitted.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// The keys that may call it, as <c>name → secret</c>. The name is not a credential — it is what
    /// reaches the log and the charging session as the source of an action, so
    /// <c>Api__Keys__claude-mcp</c> produces "API (claude-mcp) started Targeted" rather than an
    /// anonymous write. Several clients therefore get several keys.
    ///
    /// <para>Stored as the secret itself, not a hash, and supplied out-of-band through
    /// <c>.env</c> or an environment variable exactly like the broker password and the Solcast key.
    /// The web UI's password is hashed because it is a password a human chose and may have reused;
    /// these are generated, single-purpose and high-entropy, and a slow KDF on every request would buy
    /// nothing against an attacker who can already reach the port. Generate one with
    /// <c>openssl rand -hex 32</c>.</para>
    /// </summary>
    public IDictionary<string, string> Keys { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The widest span a history query may ask for, 31 days by default. A caller that wants a year of
    /// quarter-hours asks for it a month at a time, which is what keeps a single request from
    /// sweeping the whole database into memory on a Raspberry Pi.
    /// </summary>
    public TimeSpan MaxQueryRange { get; init; } = TimeSpan.FromDays(31);

    /// <summary>The most sessions one listing may return, newest first.</summary>
    public int MaxSessions { get; init; } = 500;

    /// <summary>Whether at least one key is configured. Enabled without one is a startup failure.</summary>
    public bool HasKeys => Keys.Values.Any(key => !string.IsNullOrWhiteSpace(key));
}
