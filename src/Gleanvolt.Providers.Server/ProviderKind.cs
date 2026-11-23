namespace Gleanvolt.Providers.Server;

/// <summary>
/// One kind of external provider: what it is called, which contract it speaks, where its hub is, and
/// when it is on. The kind decides that last part itself: the tariff is on while <c>External</c> is the
/// selected tariff, so that choice is the switch and there is no second one.
/// </summary>
/// <param name="Id">Lowercase, e.g. <c>tariff</c>. Also the token's kind.</param>
/// <param name="Name">In English, for the log and the API.</param>
/// <param name="ContractMajor">The host's major: a provider on another one is refused.</param>
/// <param name="ContractMinor">The host's minor, told to the provider.</param>
/// <param name="HubPath">Where the hub is, e.g. <c>/providers/tariff</c>.</param>
/// <param name="IsEnabled">Asked on every hello, and whenever the kind says its setting changed.</param>
public sealed record ProviderKind(
    string Id,
    string Name,
    int ContractMajor,
    int ContractMinor,
    string HubPath,
    Func<IServiceProvider, bool> IsEnabled)
{
    /// <summary>
    /// Whether the kind lets in only the first provider that said hello, until the owner forgets it (#440): the
    /// charger, whose provider writes to a device. Kept by the host's <see cref="IRememberedProviders"/>.
    /// </summary>
    public bool RemembersProvider { get; init; }
}

/// <summary>
/// Where the host keeps the provider a kind remembers (#440), so it survives a restart. Without one registered, a
/// kind remembers its provider until the host stops.
/// </summary>
public interface IRememberedProviders
{
    /// <summary>The provider id <paramref name="kind"/> remembers, or null.</summary>
    string? Find(string kind);

    /// <summary>Remembers <paramref name="providerId"/> for <paramref name="kind"/>.</summary>
    void Remember(string kind, string providerId);

    /// <summary>Forgets what <paramref name="kind"/> remembers.</summary>
    void Forget(string kind);
}

/// <summary>Remembers in memory only: a host with nowhere to keep it, such as a test host.</summary>
internal sealed class InMemoryRememberedProviders : IRememberedProviders
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _ids = new(StringComparer.OrdinalIgnoreCase);

    public string? Find(string kind) => _ids.TryGetValue(kind, out var id) ? id : null;

    public void Remember(string kind, string providerId) => _ids[kind] = providerId;

    public void Forget(string kind) => _ids.TryRemove(kind, out _);
}

/// <summary>What the host tells a provider about itself.</summary>
/// <param name="Version">Gleanvolt's version.</param>
public sealed record ProviderHostInfo(string Version);
