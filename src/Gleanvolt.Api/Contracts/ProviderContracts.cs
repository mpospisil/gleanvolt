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
    string? StatusText = null)
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
        status.StatusSaid?.English);
}
