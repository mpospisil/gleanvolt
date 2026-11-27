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

    private bool ConnectedToThisGleanvolt() => Registry.Find(EchoHub.Kind)?.State == ExternalProviderState.Connected;

    [Fact]
    public async Task APluginStartedBeforeGleanvoltAttachesOnceGleanvoltIsUp()
    {
        using var plugin = Plugin(out var welcomes);
        await plugin.StartAsync(CancellationToken.None);

        // Nothing is listening yet: the plugin keeps trying, quietly.
        await Task.Delay(TimeSpan.FromMilliseconds(400));
        Assert.False(plugin.IsAttached);
        Assert.Equal(0, welcomes.Count);

        await StartGleanvoltAsync(Instance(0));

        await AttachedToAsync(Instance(0), welcomes, "Gleanvolt once it answers");
        Assert.True(plugin.IsAttached);
        Assert.Single(welcomes);

        await plugin.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task APluginAttachesAgainAfterGleanvoltRestarts()
    {
        await StartGleanvoltAsync(Instance(0));
        using var plugin = Plugin(out var welcomes);
        await plugin.StartAsync(CancellationToken.None);
        await AttachedToAsync(Instance(0), welcomes, "the first Gleanvolt");

        await StopGleanvoltAsync();
        await Eventually.True(() => !plugin.IsAttached, "the plugin should notice that Gleanvolt is gone");

        // Down for longer than a few retries, as an image pull or a reboot is.
        await Task.Delay(TimeSpan.FromSeconds(1));
        await StartGleanvoltAsync(Instance(1));

        await AttachedToAsync(Instance(1), welcomes, "the restarted Gleanvolt");
        Assert.True(plugin.IsAttached);
        Assert.Equal("echo-life", Registry.Find(EchoHub.Kind)!.ProviderId);

        await plugin.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task APluginAttachesAgainAfterSeveralRestarts()
    {
        await StartGleanvoltAsync(Instance(0));
        using var plugin = Plugin(out var welcomes);
        await plugin.StartAsync(CancellationToken.None);

        // Each instance answers with a version of its own, so a welcome says which instance sent it (see
        // AttachedToAsync). The registry alone is not enough: it shows the plugin Connected before the
        // welcome has reached the plugin, and an instance stopped in between never delivers it. Waiting for the instance's own
        // welcome also tells it apart from one sent by the instance just stopped, which a retry can still
        // reach while it shuts down.
        await AttachedToAsync(Instance(0), welcomes, "the first Gleanvolt");

        for (var restart = 1; restart <= 3; restart++)
        {
            await StopGleanvoltAsync();
            await StartGleanvoltAsync(Instance(restart));
            await AttachedToAsync(Instance(restart), welcomes, $"the Gleanvolt after restart {restart}");
        }

        await plugin.StopAsync(CancellationToken.None);
    }

    private static string Instance(int number) => $"1.2.3-test.{number}";

    // Both halves of an attachment, each seen from its own side: the instance's registry shows the plugin
    // Connected, and the plugin has had that instance's welcome. Neither implies the other at the same
    // instant -- the registry is updated before the welcome is sent, and the plugin reports IsAttached a
    // moment before it raises Welcomed -- so a test that read one and then counted the other was a race
    // (it failed on CI with 2 and 3 welcomes of 4).
    private async Task AttachedToAsync(string instance, List<Welcome> welcomes, string which)
    {
        await Eventually.True(() => ConnectedToThisGleanvolt(), $"the plugin should attach to {which}");
        await Eventually.True(
            () =>
            {
                lock (welcomes)
                {
                    return welcomes.Any(welcome => welcome.HostVersion == instance);
                }
            },
            $"the plugin should be welcomed by {which}");
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

    private async Task StartGleanvoltAsync(string version = "1.2.3-test")
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel().UseUrls($"http://127.0.0.1:{_port}");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ISecretStore>(_secrets);
        builder.Services.AddSingleton(new ProviderHostInfo(version));
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
