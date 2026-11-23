using Gleanvolt.Core.Interfaces;
using Gleanvolt.Providers.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gleanvolt.Providers.Server;

/// <summary>A kind as registered: the kind, its hub, and how to reach that hub's clients.</summary>
internal sealed record ProviderKindRegistration(ProviderKind Kind, Type HubType, Func<IServiceProvider, string, IClientProxy> Client);

/// <summary>
/// Every kind this host registers and its session (#386), and the one place Settings, the API and the
/// hubs ask about them.
/// </summary>
public sealed class ExternalProviderRegistry : IExternalProviders
{
    private readonly IServiceProvider _services;
    private readonly List<ProviderSessions> _sessions;
    private readonly Dictionary<Type, ProviderSessions> _byHub;

    internal ExternalProviderRegistry(
        IServiceProvider services,
        ISecretStore secrets,
        TimeProvider time,
        ILoggerFactory loggers,
        IEnumerable<ProviderKindRegistration> registrations)
    {
        _services = services;
        var list = registrations.ToList();
        Tokens = new ProviderTokens(secrets, list.Select(registration => registration.Kind.Id));
        var logger = loggers.CreateLogger("Gleanvolt.Providers");

        if (Tokens.AdoptedFrom is { } adopted)
        {
            logger.LogInformation(
                "External providers now share one token (#406): the {Kind} provider's token is it, so that provider keeps connecting; every other provider needs it too.",
                adopted);
        }

        _sessions = [.. list.Select(registration => new ProviderSessions(
            registration.Kind,
            Tokens.Exists,
            time,
            logger,
            connectionId => registration.Client(services, connectionId),
            services.GetService<IRememberedProviders>()))];
        _byHub = list.Zip(_sessions).ToDictionary(pair => pair.First.HubType, pair => pair.Second);

        foreach (var sessions in _sessions)
        {
            sessions.Changed += () => Changed?.Invoke();
        }
    }

    /// <summary>The token every kind shares (#406).</summary>
    public ProviderTokens Tokens { get; }

    public bool HasToken => Tokens.Exists;

    public IReadOnlyList<ExternalProviderStatus> All =>
        [.. _sessions.Select(sessions => IsEnabled(sessions.Kind)
            ? sessions.Status
            : sessions.Status with { State = ExternalProviderState.Off })];

    public event Action? Changed;

    /// <summary>The status of <paramref name="kind"/>, or null when this host does not have it.</summary>
    public ExternalProviderStatus? Find(string kind) =>
        All.FirstOrDefault(status => string.Equals(status.Kind, kind, StringComparison.OrdinalIgnoreCase));

    /// <summary>The session of <paramref name="kind"/>, or null when this host does not have it.</summary>
    public ProviderSessions? For(string kind) =>
        _sessions.FirstOrDefault(sessions => string.Equals(sessions.Kind.Id, kind, StringComparison.OrdinalIgnoreCase));

    /// <summary>The session of the kind whose hub is <paramref name="hubType"/>.</summary>
    internal ProviderSessions For(Type hubType) =>
        _byHub.TryGetValue(hubType, out var sessions)
            ? sessions
            : throw new InvalidOperationException($"{hubType.Name} is not registered with AddExternalProvider.");

    /// <summary>Whether <paramref name="kind"/> is turned on.</summary>
    public bool IsEnabled(ProviderKind kind) => kind.IsEnabled(_services);

    /// <summary>The kind whose hub <paramref name="path"/> is under, or null.</summary>
    internal ProviderKind? KindAt(PathString path) =>
        _sessions.Select(sessions => sessions.Kind).FirstOrDefault(kind => path.StartsWithSegments(kind.HubPath, StringComparison.OrdinalIgnoreCase));

    public string? ReadToken() => Tokens.Read();

    public Task ForgetAsync(string kind) => For(kind)?.ForgetAsync() ?? Task.CompletedTask;

    /// <summary>
    /// The address of <paramref name="kind"/>'s page with a fresh ticket and the viewer's theme and culture
    /// (#410), or null when no provider with a page is attached or there is no token. A provider on this
    /// machine with a loopback page is given <paramref name="viewerHost"/>, the name the browser opened
    /// Gleanvolt at (#426).
    /// </summary>
    public Uri? PageLink(string kind, string theme, string culture, DateTimeOffset now, string? viewerHost = null)
    {
        if (Find(kind) is not { HasPage: true, ProviderId: { } providerId }
            || For(kind)?.PageAddress(viewerHost) is not { } page
            || Tokens.Read() is not { } token)
        {
            return null;
        }

        var ticket = ProviderPage.CreateTicket(token, providerId, now + ProviderPage.TicketLifetime);
        var query = string.Join('&',
            $"{ProviderPage.TicketParameter}={Uri.EscapeDataString(ticket)}",
            $"{ProviderPage.ThemeParameter}={Uri.EscapeDataString(theme)}",
            $"{ProviderPage.CultureParameter}={Uri.EscapeDataString(culture)}");
        var builder = new UriBuilder(page)
        {
            Query = string.IsNullOrEmpty(page.Query) ? query : page.Query.TrimStart('?') + "&" + query,
        };

        return builder.Uri;
    }

    public async Task<string> CreateTokenAsync()
    {
        var token = Tokens.Create()
            ?? throw new InvalidOperationException("The secret store did not keep the token.");

        // One token, so every session used the old one.
        foreach (var sessions in _sessions)
        {
            sessions.TokenChanged(hasToken: true);
            await sessions.CloseAsync("token-replaced", "The owner made a new token; this connection used the old one.").ConfigureAwait(false);
        }

        return token;
    }

    /// <summary>
    /// Closes <paramref name="kind"/>'s session if the kind is now off. For a kind to call when the
    /// setting that turns it on has changed.
    /// </summary>
    public async Task RevalidateAsync(string kind)
    {
        if (For(kind) is not { } sessions)
        {
            return;
        }

        if (!IsEnabled(sessions.Kind))
        {
            await sessions.CloseAsync(RefusalCodes.Disabled, $"The owner turned the {sessions.Kind.Name} provider off.").ConfigureAwait(false);
        }

        Changed?.Invoke();
    }
}
