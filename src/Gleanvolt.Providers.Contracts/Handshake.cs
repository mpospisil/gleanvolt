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
/// <param name="PageUrl">
/// The provider's own web page, for the owner (#410): where they sign in to the provider's account, enter a
/// one-time code or pair a device. Gleanvolt shows it in a frame and links to it, adding a short-lived
/// ticket and the viewer's theme and culture (<see cref="ProviderPage"/>). Absolute, http or https, and
/// reachable from the owner's browser, not only from Gleanvolt. Null when the provider has no page. Added
/// in tariff 1.2, solar-forecast 1.2 and vehicle 1.1; an older host ignores it.
/// </param>
public sealed record ProviderHello(
    string ProviderId,
    string Name,
    string Version,
    int ContractMajor,
    int ContractMinor,
    IReadOnlyList<string>? Capabilities = null,
    Uri? PageUrl = null);

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
/// owner sees it next to the session on Gleanvolt's Settings, and for the car on the Car page and in the
/// band asking for the owner.
///
/// <para>One status goes to every viewer, each in their own language (#415). Say what happened with a
/// <see cref="Code"/> from <see cref="ProviderStatusCodes"/>, which Gleanvolt words itself, and add any
/// detail in <see cref="Messages"/>, one text per language. <see cref="Message"/> stays the whole
/// sentence in English for a host older than the minor that added these. A host that knows the code
/// shows its own words and <see cref="Messages"/>, not <see cref="Message"/>; without a code it shows
/// <see cref="Messages"/> in the viewer's language, or <see cref="Message"/>.</para>
/// </summary>
/// <param name="Healthy">False when the provider cannot currently get its data.</param>
/// <param name="Message">What to show the owner, in English, or null.</param>
/// <param name="NeedsOwner">
/// True when only the owner can fix it: an expired login, a consent screen, a code sent by email.
/// Gleanvolt then asks the owner to act rather than waiting for it to clear. Added in tariff 1.1 and
/// solar-forecast 1.1, and in vehicle from 1.0; a host that does not know it reads the status as unhealthy.
/// </param>
/// <param name="Code">
/// What happened, from <see cref="ProviderStatusCodes"/>, or null. Gleanvolt words it in the viewer's
/// language, with the detail after it. A code it doesn't know is shown as no code. Added in tariff 1.3,
/// solar-forecast 1.3 and vehicle 1.3.
/// </param>
/// <param name="Messages">
/// The detail in more languages: a language tag (<c>en</c>, <c>cs</c>, or with a region, <c>cs-CZ</c>)
/// to the text. Gleanvolt shows the viewer's language, then <c>en</c>, then <see cref="Message"/>. At most
/// 16 languages of 1 000 characters each; the rest is dropped. Added with <see cref="Code"/>.
/// </param>
public sealed record ProviderStatus(
    bool Healthy,
    string? Message = null,
    bool NeedsOwner = false,
    string? Code = null,
    IReadOnlyDictionary<string, string>? Messages = null);

/// <summary>
/// The status codes Gleanvolt words itself, in every language it speaks (#415). Use the one that fits and
/// put anything more specific in the detail.
/// </summary>
public static class ProviderStatusCodes
{
    /// <summary>The provider has no account or settings yet: the owner enters them on its page.</summary>
    public const string NotSetUp = "not-set-up";

    /// <summary>The owner has to sign in on the provider's page.</summary>
    public const string NeedsSignIn = "needs-sign-in";

    /// <summary>The service sent the owner a code (email, text message) to enter on the provider's page.</summary>
    public const string NeedsCode = "needs-code";

    /// <summary>The service can't be reached: no network, DNS, a timeout, a server error.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>The service is limiting requests; the provider tries again later.</summary>
    public const string RateLimited = "rate-limited";

    /// <summary>The service refused the account or the request: a wrong password, a revoked key, consent withdrawn.</summary>
    public const string Refused = "refused";

    /// <summary>The service answered in a way the provider doesn't understand: it may need an update.</summary>
    public const string ServiceChanged = "service-changed";

    /// <summary>Every code, in the order above.</summary>
    public static IReadOnlyList<string> All { get; } = [NotSetUp, NeedsSignIn, NeedsCode, Unreachable, RateLimited, Refused, ServiceChanged];
}

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
    public const string SiteChanged = "SiteChanged";
    public const string VehicleChanged = "VehicleChanged";
    public const string ChargingSessionChanged = "ChargingSessionChanged";
}
