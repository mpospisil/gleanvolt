using System.Net;
using System.Text.RegularExpressions;
using Gleanvolt.Core.Models;
using Gleanvolt.Providers.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace Gleanvolt.Providers.Server;

/// <summary>
/// What every kind's hub does (#386): the hello, the status report and the disconnect. A kind derives
/// from it and adds its own methods, which check <see cref="IsAttached"/> first.
///
/// <para>Hub methods only receive: they check, store and return. Nothing here waits for the provider,
/// and a request to it goes out from a host-side service through <c>IHubContext</c>.</para>
/// </summary>
/// <typeparam name="TClient">What the kind's hub may send its provider.</typeparam>
/// <typeparam name="TWelcome">What the kind answers a hello with.</typeparam>
public abstract partial class ProviderHub<TClient, TWelcome> : Hub<TClient>, IProviderHub<TWelcome>
    where TClient : class, IProviderClient
    where TWelcome : Welcome
{
    private readonly ExternalProviderRegistry _registry;
    private readonly ProviderHostInfo _host;

    protected ProviderHub(ExternalProviderRegistry registry, ProviderHostInfo host)
    {
        _registry = registry;
        _host = host;
    }

    /// <summary>This kind's session.</summary>
    protected ProviderSessions Sessions => _registry.For(GetType());

    /// <summary>Whether this connection is the attached session.</summary>
    protected bool IsAttached => Sessions.IsCurrent(Context.ConnectionId);

    /// <summary>The kind's own part of an accepted welcome.</summary>
    protected abstract TWelcome CreateWelcome();

    /// <summary>
    /// The kind's own checks of a hello that passed the common ones (#440): null to go on, else a refusal code, its
    /// English reason, and the reason for the web UI (one phrase per sentence, or null to show the English).
    /// </summary>
    protected virtual (string? Code, string? Reason, IReadOnlyList<Phrase?>? Phrases) Admit(ProviderHello hello) => (null, null, null);

    /// <summary>After a hello was accepted and its session attached: for a kind that keeps something from the hello.</summary>
    protected virtual void OnAttached(ProviderHello hello)
    {
    }

    public Task<TWelcome> Hello(ProviderHello hello)
    {
        var kind = Sessions.Kind;
        var welcome = CreateWelcome() with
        {
            HostVersion = _host.Version,
            ContractMajor = kind.ContractMajor,
            ContractMinor = kind.ContractMinor,
        };

        var (code, reason) = Check(hello, kind);
        IReadOnlyList<Phrase?>? phrases = null;

        if (code is null)
        {
            (code, reason, phrases) = Admit(hello);
        }

        if (code is null)
        {
            Phrase? said;
            (code, reason, said) = Sessions.Attach(Context, hello, PageOnThisHost(hello));
            phrases = said is null ? null : [said];
        }

        if (code is null)
        {
            OnAttached(hello);
        }

        if (code is not null)
        {
            // Not a hello at all, or a kind that is off: nothing the owner needs to see beside the session.
            if (hello?.ProviderId is not null && hello.Name is not null && code is not RefusalCodes.Disabled)
            {
                Sessions.Refused(hello, code, reason ?? string.Empty, phrases);
            }

            // The answer is written before the connection closes, so the provider can log why.
            _ = CloseSoonAsync();
            return Task.FromResult(welcome with { Accepted = false, ReasonCode = code, Reason = reason });
        }

        return Task.FromResult(welcome with { Accepted = true });
    }

    public Task ReportStatus(ProviderStatus status)
    {
        RequireAttached();
        Sessions.Reported(status);
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        Sessions.Detach(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Throws to the provider when it has not said hello on this connection.</summary>
    protected void RequireAttached()
    {
        if (!IsAttached)
        {
            throw new HubException("Say Hello first: this connection has no attached session.");
        }
    }

    private (string? Code, string? Reason) Check(ProviderHello hello, ProviderKind kind)
    {
        if (hello is null || string.IsNullOrWhiteSpace(hello.Name) || hello.ProviderId is null || !ProviderIdPattern().IsMatch(hello.ProviderId))
        {
            return (RefusalCodes.Invalid, "The hello needs a name and an id of lowercase letters, digits and dashes.");
        }

        if (hello.PageUrl is { } page && (!page.IsAbsoluteUri || (page.Scheme != Uri.UriSchemeHttp && page.Scheme != Uri.UriSchemeHttps)))
        {
            return (RefusalCodes.Invalid, "The page URL must be an absolute http or https address.");
        }

        if (Context.User?.FindFirst(ProviderTokenAuthentication.KindClaim)?.Value is not { } claimed
            || !string.Equals(claimed, kind.Id, StringComparison.OrdinalIgnoreCase))
        {
            return (RefusalCodes.WrongKind, $"The connection was not authenticated for the {kind.Name} hub.");
        }

        if (!_registry.IsEnabled(kind))
        {
            return (RefusalCodes.Disabled, $"The {kind.Name} provider is turned off in Gleanvolt.");
        }

        if (hello.ContractMajor != kind.ContractMajor)
        {
            return (RefusalCodes.ContractMajor,
                $"The provider speaks contract {hello.ContractMajor}.{hello.ContractMinor}; Gleanvolt speaks {kind.ContractMajor}.{kind.ContractMinor}.");
        }

        return (null, null);
    }

    /// <summary>
    /// Whether the provider runs on Gleanvolt's own machine and gave a loopback page (#426): it connected
    /// from a loopback address, so its <c>localhost</c> is Gleanvolt's.
    /// </summary>
    private bool PageOnThisHost(ProviderHello hello) =>
        hello.PageUrl is { IsLoopback: true }
        && Context.GetHttpContext()?.Connection.RemoteIpAddress is { } remote
        && IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote);

    private async Task CloseSoonAsync()
    {
        var context = Context;
        await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
        context.Abort();
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex ProviderIdPattern();
}
