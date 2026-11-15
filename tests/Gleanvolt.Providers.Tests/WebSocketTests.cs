using Gleanvolt.Core.Interfaces;
using Gleanvolt.Providers.Client;
using Gleanvolt.Providers.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gleanvolt.Providers.Tests;

/// <summary>
/// The transport a provider really uses (#386): a WebSocket to Kestrel, with the token in the query
/// string as a WebSocket carries it, rather than the TestServer's long polling.
/// </summary>
public sealed class WebSocketTests : IAsyncLifetime
{
    private readonly MemorySecrets _secrets = new();
    private WebApplication? _app;
    private Uri _address = new("http://127.0.0.1");
    private string _token = string.Empty;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ISecretStore>(_secrets);
        builder.Services.AddSingleton(new ProviderHostInfo("1.2.3-test"));
        builder.Services.AddExternalProvider<EchoHub>(new ProviderKind(EchoHub.Kind, "Echo", 1, 0, EchoHub.Path, _ => true));

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapExternalProviders();
        await _app.StartAsync();

        _address = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        _token = await _app.Services.GetRequiredService<ExternalProviderRegistry>().CreateTokenAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheSdkAttachesOverAWebSocket()
    {
        using var sdk = new EchoConnection(new ProviderOptions
        {
            Endpoint = _address,
            Token = _token,
            ProviderId = "echo-ws",
            Name = "Echo over a WebSocket",
            Version = "1.0",
            ConfigureHttp = options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
            },
        });

        await sdk.StartAsync(CancellationToken.None);
        await Eventually.True(() => sdk.IsAttached, "the SDK should attach over a WebSocket");

        var registry = _app!.Services.GetRequiredService<ExternalProviderRegistry>();
        Assert.Equal(ExternalProviderState.Connected, registry.Find(EchoHub.Kind)!.State);
        Assert.Equal("echo-ws", registry.Find(EchoHub.Kind)!.ProviderId);

        await sdk.StopAsync(CancellationToken.None);
    }
}
