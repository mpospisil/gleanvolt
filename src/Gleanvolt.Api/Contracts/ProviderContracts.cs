using Gleanvolt.Core.Interfaces;

namespace Gleanvolt.Api.Contracts;

/// <summary>
/// One kind of external data provider (#386): a program outside Gleanvolt that connects to its web
/// port and supplies data a built-in source otherwise would. Settings turns each kind on and makes its
/// token; this only reports.
/// </summary>
/// <param name="Kind">The kind's id, e.g. <c>tariff</c>.</param>
/// <param name="Name">The kind's name.</param>
/// <param name="HubPath">Where its SignalR hub is on the web port.</param>
/// <param name="HasToken">Whether the provider token exists: one for every kind (#406), so the same for all. Without it nothing can connect.</param>
/// <param name="State">off, waiting, connected or disconnected.</param>
/// <param name="ProviderId">The connected or last provider's id.</param>
/// <param name="ProviderName">Its name.</param>
/// <param name="ProviderVersion">Its version.</param>
/// <param name="Since">When the current state began.</param>
/// <param name="LastDataAt">When the provider's data was last accepted.</param>
/// <param name="LastProblems">Why its last data was refused, or empty.</param>
/// <param name="Healthy">What the provider last reported about itself, or null.</param>
/// <param name="StatusMessage">Its message with that report.</param>
/// <param name="Unanswered">True when it did not answer Gleanvolt's last request in time.</param>
/// <param name="NeedsOwner">True when the provider said only the owner can fix it: an expired login, a consent screen.</param>
/// <param name="StatusCode">
/// The code with that report (#415), e.g. <c>needs-sign-in</c>, <c>needs-code</c>, <c>unreachable</c>,
/// or null. One of the provider contract's <c>ProviderStatusCodes</c>, or one Gleanvolt doesn't know.
/// </param>
/// <param name="StatusText">
/// That report in English as Gleanvolt words it: its words for the code, then the provider's detail. Null
/// when the provider said nothing.
/// </param>
/// <param name="RememberedProviderId">
/// For the charger (#440): the plugin id Gleanvolt remembers and lets in alone until the owner presses Forget on
/// Settings. Null for every other kind, and before the first plugin connected.
/// </param>
/// <param name="LastRefusal">The last connection this kind refused since the start, or null; cleared when one is accepted.</param>
public sealed record ExternalProviderResponse(
    string Kind,
    string Name,
    string HubPath,
    bool HasToken,
    ExternalProviderState State,
    string? ProviderId,
    string? ProviderName,
    string? ProviderVersion,
    DateTimeOffset? Since,
    DateTimeOffset? LastDataAt,
    IReadOnlyList<string> LastProblems,
    bool? Healthy,
    string? StatusMessage,
    bool Unanswered,
    bool NeedsOwner = false,
    string? StatusCode = null,
    string? StatusText = null,
    string? RememberedProviderId = null,
    ExternalProviderRefusalResponse? LastRefusal = null)
{
    internal static ExternalProviderResponse From(ExternalProviderStatus status) => new(
        status.Kind,
        status.Name,
        status.HubPath,
        status.HasToken,
        status.State,
        status.ProviderId,
        status.ProviderName,
        status.ProviderVersion,
        status.Since,
        status.LastDataAt,
        status.LastProblem ?? [],
        status.Healthy,
        status.StatusMessage,
        status.Unanswered,
        status.NeedsOwner,
        status.StatusCode,
        status.StatusSaid?.English,
        status.RememberedProviderId,
        status.LastRefusal is { } refusal
            ? new ExternalProviderRefusalResponse(refusal.ProviderId, refusal.ProviderName, refusal.Code, refusal.Reason, refusal.At)
            : null);
}

/// <summary>A provider's connection that Gleanvolt refused (#440).</summary>
/// <param name="ProviderId">The refused provider's id.</param>
/// <param name="ProviderName">Its name.</param>
/// <param name="Code">
/// Why, as the provider was told: e.g. <c>limits</c> (the charger can't do what ChargeControl is set to) or
/// <c>another-provider</c> (another provider is connected, or for the charger, remembered).
/// </param>
/// <param name="Reason">Why, in English, naming the setting to change where there is one.</param>
/// <param name="At">When.</param>
public sealed record ExternalProviderRefusalResponse(string ProviderId, string ProviderName, string Code, string Reason, DateTimeOffset At);
