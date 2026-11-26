using Gleanvolt.Providers.Client;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Server;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gleanvolt.Providers.Tests;

// A kind that exists only here and never ships (#386): the framework, with no domain in the way.

public interface IEchoClient : IProviderClient
{
    Task Ping(RefreshRequest request);
}

public sealed class EchoHub(ExternalProviderRegistry registry, ProviderHostInfo host) : ProviderHub<IEchoClient, Welcome>(registry, host)
{
    public const string Kind = "test-echo";

    public const string Path = "/providers/test-echo";

    protected override Welcome CreateWelcome() => new();

    public Task<PublishResult> Publish(string text, RequestId? inReplyTo)
    {
        RequireAttached();

        if (text == "bad")
        {
            Sessions.DataRefused(inReplyTo, ["Bad text."], [null]);
            return Task.FromResult(PublishResult.Refused(["Bad text."]));
        }

        Sessions.DataAccepted(inReplyTo);
        return Task.FromResult(PublishResult.Ok);
    }
}

/// <summary>The SDK's connection for the echo kind: answers every ping when <see cref="Answer"/> is on.</summary>
public sealed class EchoConnection(ProviderOptions options, ILogger? logger = null, ProviderCredentials? credentials = null)
    : ProviderConnection<Welcome>(options, EchoHub.Path, 1, 0, logger ?? NullLogger.Instance, credentials)
{
    public bool Answer { get; set; } = true;

    protected override void OnConfigure(HubConnection connection) =>
        connection.On<RefreshRequest>("Ping", request =>
        {
            if (Answer)
            {
                _ = Task.Run(() => Connection.InvokeAsync<PublishResult>("Publish", "pong", request.Id));
            }

            return Task.CompletedTask;
        });
}
