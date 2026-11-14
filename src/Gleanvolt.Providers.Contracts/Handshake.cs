namespace Gleanvolt.Providers.Contracts;

/// <summary>
/// The first thing a provider says after it connects, and again after every reconnect: who it is and
/// which version of the kind's contract it speaks. Nothing else is accepted on a connection until the
/// host has answered with an accepted <see cref="Welcome"/>.
/// </summary>
/// <param name="ProviderId">
/// Stable across restarts and reconnects: the same id reconnecting replaces its old session, a
/// different one is refused while the first is connected. Lowercase letters, digits and dashes.
/// </param>
/// <param name="Name">What Gleanvolt shows the owner, e.g. "Spot prices (OTE)".</param>
/// <param name="Version">The provider's own version, for the owner and the log.</param>
/// <param name="ContractMajor">The kind's contract major the provider was built against. A different major is refused.</param>
/// <param name="ContractMinor">The minor. A higher minor than the host's is accepted; the host ignores what it does not know.</param>
/// <param name="Capabilities">Optional features the provider supports, by name. Empty for now.</param>
public sealed record ProviderHello(
    string ProviderId,
    string Name,
    string Version,
    int ContractMajor,
    int ContractMinor,
    IReadOnlyList<string>? Capabilities = null);

/// <summary>
/// The host's answer to <see cref="ProviderHello"/>. A kind that has more to tell its provider (the
/// tariff's time zone and currency) answers with a derived record.
/// </summary>
public record Welcome
{
    /// <summary>Whether the session is attached. When false, the host closes the connection.</summary>
    public bool Accepted { get; init; }

    /// <summary>Why it was refused: one of <see cref="RefusalCodes"/>, or null when accepted.</summary>
    public string? ReasonCode { get; init; }

    /// <summary>The refusal in English, for the provider's log.</summary>
    public string? Reason { get; init; }

    /// <summary>The Gleanvolt version answering.</summary>
    public string HostVersion { get; init; } = string.Empty;

    /// <summary>The host's major for this kind's contract.</summary>
    public int ContractMajor { get; init; }

    /// <summary>The host's minor for this kind's contract.</summary>
    public int ContractMinor { get; init; }
}

/// <summary>Why a <see cref="ProviderHello"/> was refused.</summary>
public static class RefusalCodes
{
    /// <summary>The owner has not turned this kind on in Gleanvolt.</summary>
    public const string Disabled = "disabled";

    /// <summary>The token belongs to another kind.</summary>
    public const string WrongKind = "wrong-kind";

    /// <summary>The provider speaks another major version of the contract.</summary>
    public const string ContractMajor = "contract-major";

    /// <summary>Another provider is connected for this kind. Only one is active at a time.</summary>
    public const string AnotherProvider = "another-provider";

    /// <summary>The hello itself is not usable: an empty id or name.</summary>
    public const string Invalid = "invalid";
}

/// <summary>
/// Names a request from the host, so the provider's answer can quote it (<c>InReplyTo</c>). A host
/// request is never awaited inside a call; the answer is an ordinary call of its own.
/// </summary>
/// <param name="Value">Opaque to the provider.</param>
public sealed record RequestId(string Value)
{
    /// <summary>A new, unique id.</summary>
    public static RequestId New() => new(Guid.NewGuid().ToString("N"));

    public override string ToString() => Value;
}

/// <summary>A request from the host to send fresh data.</summary>
/// <param name="Id">To quote in the answer.</param>
/// <param name="RequestedAt">When the host asked.</param>
public sealed record RefreshRequest(RequestId Id, DateTimeOffset RequestedAt);

/// <summary>
/// How the provider itself is doing, beside its data: a source that failed, a login that expired. The
/// owner sees it next to the session on Gleanvolt's Settings.
/// </summary>
/// <param name="Healthy">False when the provider cannot currently get its data.</param>
/// <param name="Message">What to show the owner, in English, or null.</param>
public sealed record ProviderStatus(bool Healthy, string? Message = null);

/// <summary>What the host made of published data.</summary>
/// <param name="Accepted">Whether it was stored. Data with any problem is refused as a whole.</param>
/// <param name="Problems">What is wrong with it, in English. Empty when accepted.</param>
public sealed record PublishResult(bool Accepted, IReadOnlyList<string> Problems)
{
    /// <summary>Stored, nothing wrong.</summary>
    public static PublishResult Ok { get; } = new(true, []);

    /// <summary>Refused for <paramref name="problems"/>.</summary>
    public static PublishResult Refused(IReadOnlyList<string> problems) => new(false, problems);
}

/// <summary>
/// What every kind's hub accepts. Hub methods only receive: they check, store and return, and never
/// wait for the provider.
/// </summary>
/// <typeparam name="TWelcome">What this kind answers a hello with.</typeparam>
public interface IProviderHub<TWelcome>
    where TWelcome : Welcome
{
    /// <summary>The handshake. Call it first, and again after every reconnect.</summary>
    Task<TWelcome> Hello(ProviderHello hello);

    /// <summary>Tells the owner how the provider is doing. Accepted only on an attached session.</summary>
    Task ReportStatus(ProviderStatus status);
}

/// <summary>What the host may send every kind's provider.</summary>
public interface IProviderClient
{
    /// <summary>
    /// The host is closing the session: the owner turned the kind off, or another session replaced this
    /// one. The connection closes right after.
    /// </summary>
    /// <param name="reasonCode">One of <see cref="RefusalCodes"/>, or "replaced" or "shutdown".</param>
    /// <param name="reason">In English, for the provider's log.</param>
    Task Goodbye(string reasonCode, string reason);
}

/// <summary>The method names on the wire, for a client that does not use the .NET SDK.</summary>
public static class ProviderMethods
{
    public const string Hello = "Hello";
    public const string ReportStatus = "ReportStatus";
    public const string Publish = "Publish";
    public const string Goodbye = "Goodbye";
    public const string RefreshRequested = "RefreshRequested";
}
