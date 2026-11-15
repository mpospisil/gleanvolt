using System.Net;
using System.Net.Sockets;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Providers.Client;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gleanvolt.Providers.Tests;

/// <summary>
/// A plugin and Gleanvolt are separate processes, started and stopped independently (#386): the
/// plugin may start first, and Gleanvolt may restart under it. Both on Kestrel over a WebSocket, on one
/// fixed port, so "Gleanvolt is not running" is a refused TCP connection, as it is in real life.
/// </summary>
public sealed class LifecycleTests : IAsyncDisposable
{
    // The secret store outlives a restart, as the real one does on disk.
    private readonly MemorySecrets _secrets = new();
    private readonly int _port = FreePort();
    private readonly string _token;
    private WebApplication? _gleanvolt;

    public LifecycleTests()
    {
        _token = new ProviderTokens(_secrets).Create()!;
    }

    private ExternalProviderRegistry Registry => _gleanvolt!.Services.GetRequiredService<ExternalProviderRegistry>();

    [Fact]
    public async Task APluginStartedBeforeGleanvoltAttachesOnceGleanvoltIsUp()
    {
        using var plugin = Plugin(out var welcomes);
        await plugin.StartAsync(CancellationToken.None);

        // Nothing is listening yet: the plugin keeps trying, quietly.
        await Task.Delay(TimeSpan.FromMilliseconds(400));
        Assert.False(plugin.IsAttached);
        Assert.Equal(0, welcomes.Count);

        await StartGleanvoltAsync();

        await Eventually.True(() => plugin.IsAttached, "the plugin should attach once Gleanvolt answers");
        Assert.Equal(ExternalProviderState.Connected, Registry.Find(EchoHub.Kind)!.State);
        Assert.Equal(1, welcomes.Count);

        await plugin.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task APluginAttachesAgainAfterGleanvoltRestarts()
    {
        await StartGleanvoltAsync();
        using var plugin = Plugin(out var welcomes);
        await plugin.StartAsync(CancellationToken.None);
        await Eventually.True(() => plugin.IsAttached, "the plugin should attach");

        await StopGleanvoltAsync();
        await Eventually.True(() => !plugin.IsAttached, "the plugin should notice that Gleanvolt is gone");

        // Down for longer than a few retries, as an image pull or a reboot is.
        await Task.Delay(TimeSpan.FromSeconds(1));
        await StartGleanvoltAsync();

        await Eventually.True(() => plugin.IsAttached, "the plugin should attach to the restarted Gleanvolt");
        Assert.Equal(ExternalProviderState.Connected, Registry.Find(EchoHub.Kind)!.State);
        Assert.Equal("echo-life", Registry.Find(EchoHub.Kind)!.ProviderId);
        Assert.Equal(2, welcomes.Count);

        await plugin.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task APluginAttachesAgainAfterSeveralRestarts()
    {
        await StartGleanvoltAsync();
        using var plugin = Plugin(out var welcomes);
        await plugin.StartAsync(CancellationToken.None);

        for (var restart = 1; restart <= 3; restart++)
        {
            await Eventually.True(() => plugin.IsAttached, $"the plugin should be attached before restart {restart}");
            await StopGleanvoltAsync();
            await StartGleanvoltAsync();
        }

        await Eventually.True(() => plugin.IsAttached, "the plugin should be attached after the last restart");
        Assert.Equal(4, welcomes.Count);

        await plugin.StopAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync() => await StopGleanvoltAsync();

    private EchoConnection Plugin(out List<Welcome> welcomes)
    {
        var received = new List<Welcome>();
        welcomes = received;
        var plugin = new EchoConnection(new ProviderOptions
        {
            Endpoint = new Uri($"http://127.0.0.1:{_port}"),
            Token = _token,
            ProviderId = "echo-life",
            Name = "Echo through restarts",
            Version = "1.0",
            // Short, so a test waits milliseconds where a real plugin waits seconds.
            InitialRetryDelay = TimeSpan.FromMilliseconds(50),
            MaxRetryDelay = TimeSpan.FromMilliseconds(200),
            ConfigureHttp = options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
            },
        });
        plugin.Welcomed += welcome =>
        {
            lock (received)
            {
                received.Add(welcome);
            }
        };
        return plugin;
    }

    private async Task StartGleanvoltAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel().UseUrls($"http://127.0.0.1:{_port}");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ISecretStore>(_secrets);
        builder.Services.AddSingleton(new ProviderHostInfo("1.2.3-test"));
        builder.Services.AddExternalProvider<EchoHub>(new ProviderKind(EchoHub.Kind, "Echo", 1, 0, EchoHub.Path, _ => true));

        _gleanvolt = builder.Build();
        _gleanvolt.UseAuthentication();
        _gleanvolt.UseAuthorization();
        _gleanvolt.MapExternalProviders();
        await _gleanvolt.StartAsync();
    }

    private async Task StopGleanvoltAsync()
    {
        if (_gleanvolt is { } gleanvolt)
        {
            _gleanvolt = null;
            await gleanvolt.StopAsync();
            await gleanvolt.DisposeAsync();
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
