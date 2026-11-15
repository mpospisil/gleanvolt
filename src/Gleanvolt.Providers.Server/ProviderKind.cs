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
    Func<IServiceProvider, bool> IsEnabled);

/// <summary>What the host tells a provider about itself.</summary>
/// <param name="Version">Gleanvolt's version.</param>
public sealed record ProviderHostInfo(string Version);
