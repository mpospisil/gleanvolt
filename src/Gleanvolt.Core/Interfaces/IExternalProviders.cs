using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// The external data providers (#386): processes outside Gleanvolt that connect to its web port and
/// supply data a built-in source would otherwise supply. One entry per kind this build has. Read by
/// Settings, the API and Health; the sessions themselves live in <c>Gleanvolt.Providers.Server</c>.
/// </summary>
public interface IExternalProviders
{
    /// <summary>Every kind this build registers, in registration order.</summary>
    IReadOnlyList<ExternalProviderStatus> All { get; }

    /// <summary>The kind <paramref name="kind"/>, or null when this build does not have it.</summary>
    ExternalProviderStatus? Find(string kind) => All.FirstOrDefault(status => string.Equals(status.Kind, kind, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the provider token exists (#406). Without it, nothing can connect.</summary>
    bool HasToken => All.Any(status => status.HasToken);

    /// <summary>
    /// Makes a new provider token and returns it: one for every kind (#406). Replaces the old token and
    /// closes every session, since the usual reason to regenerate is a token that leaked.
    /// </summary>
    Task<string> CreateTokenAsync();

    /// <summary>
    /// The provider token, or null when there is none. For Settings to reveal to a signed-in owner,
    /// like the API keys; never for the API.
    /// </summary>
    string? ReadToken();

    /// <summary>
    /// Forgets the provider <paramref name="kind"/> remembers (#440), and closes its session if it is connected:
    /// the next provider to say hello is remembered instead. Nothing for a kind that remembers none.
    /// </summary>
    Task ForgetAsync(string kind) => Task.CompletedTask;

    /// <summary>The plugins that knocked and wait for the owner to accept or reject them, oldest first.</summary>
    IReadOnlyList<PairingRequestView> Pairing { get; }

    /// <summary>
    /// Accepts the pairing request <paramref name="id"/>: the plugin collects the provider token, which is made
    /// first when there is none. False when the request is gone or its <see cref="PairingRequestView.Block"/> is not none.
    /// </summary>
    Task<bool> AcceptPairingAsync(string id);

    /// <summary>Rejects the pairing request <paramref name="id"/>; its plugin is told so when it collects.</summary>
    void RejectPairing(string id);

    /// <summary>Raised when any kind's status changes: a session, its data, the token, or the pairing list.</summary>
    event Action? Changed;
}

/// <summary>Why the owner cannot accept a pairing request now.</summary>
public enum PairingBlock
{
    /// <summary>Nothing: the request can be accepted.</summary>
    None,

    /// <summary>Another plugin from another address claims the same provider id; two cannot both be it.</summary>
    Duplicate,

    /// <summary>The owner has not turned the kind on.</summary>
    KindOff,

    /// <summary>The kind remembers a different provider (#440); the owner must forget it first.</summary>
    AnotherProvider,
}

/// <summary>A plugin waiting for the owner's answer, as the owner sees it.</summary>
/// <param name="Id">Names the request; what Accept and Reject take.</param>
/// <param name="Kind">The kind's id the plugin knocked for.</param>
/// <param name="ProviderId">The plugin's own id.</param>
/// <param name="ProviderName">Its name.</param>
/// <param name="Version">Its version.</param>
/// <param name="Source">The address the knock came from.</param>
/// <param name="OnThisMachine">Whether it runs on the machine Gleanvolt runs on.</param>
/// <param name="Code">The code the owner compares with the one the plugin shows.</param>
/// <param name="Since">When the plugin first knocked.</param>
/// <param name="Block">Why it cannot be accepted now, or none.</param>
public sealed record PairingRequestView(
    string Id,
    string Kind,
    string ProviderId,
    string ProviderName,
    string Version,
    string Source,
    bool OnThisMachine,
    string Code,
    DateTimeOffset Since,
    PairingBlock Block);

/// <summary>Where a kind's session is.</summary>
public enum ExternalProviderState
{
    /// <summary>The owner has not turned the kind on.</summary>
    Off,

    /// <summary>On, and no provider has connected since the start.</summary>
    Waiting,

    /// <summary>A provider is attached.</summary>
    Connected,

    /// <summary>A provider was attached and has gone. Its last data is kept.</summary>
    Disconnected,
}

/// <summary>One kind of external provider as the owner sees it.</summary>
/// <param name="Kind">The kind's id, e.g. <c>tariff</c>.</param>
/// <param name="Name">The kind's name in English.</param>
/// <param name="HubPath">Where its hub is on the web port.</param>
/// <param name="HasToken">Whether the provider token exists; the same for every kind since #406. Without it, nothing can connect.</param>
/// <param name="State">The session.</param>
/// <param name="ProviderId">The attached or last provider's id.</param>
/// <param name="ProviderName">Its name.</param>
/// <param name="ProviderVersion">Its version.</param>
/// <param name="Since">When the current state began: connected since, disconnected since.</param>
/// <param name="LastDataAt">When the last data was accepted.</param>
/// <param name="LastProblem">The last refused data's problems, in English, or null.</param>
/// <param name="LastProblemPhrases">The same, for the web UI.</param>
/// <param name="Healthy">What the provider last reported about itself, or null when it has not.</param>
/// <param name="StatusMessage">Its message with that report.</param>
/// <param name="Unanswered">True when the host's last request got no answer in time.</param>
/// <param name="NeedsOwner">True when the provider said only the owner can fix what is wrong: a login, a consent screen.</param>
/// <param name="PageUrl">The attached or last provider's own page (#410), as it said in its hello, or null.</param>
/// <param name="StatusCode">The code with that report (#415), as sent: one Gleanvolt words, or one it doesn't know.</param>
/// <param name="StatusMessages">The report's detail in more languages (#415), language tag to text, or null.</param>
/// <param name="RememberedProviderId">
/// For a kind that remembers its provider (the charger, #440): the id it remembers, which is the only one let in
/// until the owner forgets it. Null for any other kind, and before the first provider said hello.
/// </param>
/// <param name="LastRefusal">The last hello this kind refused since the start, or null; cleared when one is accepted.</param>
public sealed record ExternalProviderStatus(
    string Kind,
    string Name,
    string HubPath,
    bool HasToken,
    ExternalProviderState State,
    string? ProviderId = null,
    string? ProviderName = null,
    string? ProviderVersion = null,
    DateTimeOffset? Since = null,
    DateTimeOffset? LastDataAt = null,
    IReadOnlyList<string>? LastProblem = null,
    IReadOnlyList<Phrase?>? LastProblemPhrases = null,
    bool? Healthy = null,
    string? StatusMessage = null,
    bool Unanswered = false,
    bool NeedsOwner = false,
    Uri? PageUrl = null,
    string? StatusCode = null,
    IReadOnlyDictionary<string, string>? StatusMessages = null,
    string? RememberedProviderId = null,
    ExternalProviderRefusal? LastRefusal = null)
{
    /// <summary>Whether the kind remembers a provider the owner can forget.</summary>
    public bool CanForget => RememberedProviderId is not null;

    /// <summary>
    /// What the provider last said about itself, worded for every language (#415): Gleanvolt's own words
    /// for <see cref="StatusCode"/>, then the detail in the viewer's language. The English is for the log,
    /// the API and Home Assistant. Null with nothing to say.
    /// </summary>
    public (Phrase? Phrase, string English)? StatusSaid => ProviderStatusText.Of(StatusCode, StatusMessages, StatusMessage);

    /// <summary>
    /// Where the web UI opens the provider's page (#410): a Gleanvolt address behind the web login that
    /// redirects to <see cref="PageUrl"/> with a fresh ticket. The ticket is never in a page's HTML.
    /// </summary>
    public string PagePath => PagePathFor(Kind);

    /// <summary>Whether the provider's page can be opened now: one is attached and has a page.</summary>
    public bool HasPage => State == ExternalProviderState.Connected && PageUrl is not null;

    /// <summary>The page path of <paramref name="kind"/>.</summary>
    public static string PagePathFor(string kind) => "/external/" + kind + "/page";
}

/// <summary>A hello the kind refused (#440).</summary>
/// <param name="ProviderId">The refused provider's id, as it said.</param>
/// <param name="ProviderName">Its name.</param>
/// <param name="Code">The refusal code, as the provider was told.</param>
/// <param name="Reason">Why, in English, as the provider was told.</param>
/// <param name="Phrases">The same, for the web UI: one per sentence of <paramref name="Reason"/>, or null to show the English.</param>
/// <param name="At">When.</param>
public sealed record ExternalProviderRefusal(
    string ProviderId,
    string ProviderName,
    string Code,
    string Reason,
    IReadOnlyList<Phrase?>? Phrases,
    DateTimeOffset At);
