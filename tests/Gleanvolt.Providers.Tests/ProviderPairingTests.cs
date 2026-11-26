using System.Net;
using System.Runtime.CompilerServices;
using Gleanvolt.Providers.Client;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Gleanvolt.Providers.Tests;

/// <summary>A plugin with no token finds Gleanvolt, knocks, and is paired once the owner accepts.</summary>
public sealed class ProviderPairingTests : IAsyncDisposable
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(3);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gleanvolt-pairing-" + Guid.NewGuid().ToString("N"));
    private readonly WatchedTime _time = new();
    private readonly SwitchingHandler _http = new();
    private readonly FakeFinder _finder = new();
    private readonly List<Microsoft.Extensions.Hosting.BackgroundService> _running = [];
    private TestHost _host = new();

    public async ValueTask DisposeAsync()
    {
        foreach (var service in _running)
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }

        await _host.DisposeAsync();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task APluginWithoutATokenKnocksWithItsCode()
    {
        await StartGleanvoltAsync();
        var plugin = await StartPluginAsync();

        await Until(() => _host.Registry.Pairing.Count == 1 && plugin.Pairing.Status.Phase == PairingPhase.Waiting, "the plugin knocked");

        var request = Assert.Single(_host.Registry.Pairing);
        Assert.Equal(plugin.Pairing.Status.Code, request.Code);
        Assert.Matches("^[0-9]{3} [0-9]{3}$", plugin.Pairing.Status.Code);
        Assert.Equal("echo-pairing", request.ProviderId);
        Assert.Equal(_host.Server.BaseAddress, plugin.Pairing.Status.Gleanvolt);
    }

    [Fact]
    public async Task AcceptingPairsAndConnects()
    {
        await StartGleanvoltAsync();
        var plugin = await StartPluginAsync();
        var connection = new EchoConnection(plugin.Options, credentials: plugin.Credentials);
        await connection.StartAsync(CancellationToken.None);
        _running.Add(connection);
        await Until(() => _host.Registry.Pairing.Count == 1, "the plugin knocked");

        Assert.True(await _host.Registry.AcceptPairingAsync(_host.Registry.Pairing[0].Id));

        await Until(() => plugin.Credentials.Source == CredentialSource.Paired, "the plugin collected the token", Poll);
        Assert.Equal(_host.Server.BaseAddress, plugin.Credentials.Endpoint);
        Assert.Equal(_host.Token, plugin.Credentials.Token);
        await Until(() => plugin.Pairing.Status.Phase == PairingPhase.NotNeeded, "pairing is done");
        Assert.False(File.Exists(Path.Combine(_directory, "gleanvolt-pairing-secret")), "the secret is forgotten once paired");
        await Eventually.True(() => connection.IsAttached, "the connection uses the paired token");
    }

    [Fact]
    public async Task APluginWithAConfiguredTokenDoesNotKnock()
    {
        await StartGleanvoltAsync();
        var plugin = await StartPluginAsync(token: "configured");

        for (var i = 0; i < 10; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(10);
        }

        Assert.Equal(PairingPhase.NotNeeded, plugin.Pairing.Status.Phase);
        Assert.Empty(_host.Registry.Pairing);
        Assert.False(File.Exists(Path.Combine(_directory, "gleanvolt-pairing-secret")), "no secret is made without a need for one");
    }

    [Fact]
    public async Task TheCodeStaysAcrossARestart()
    {
        var first = await StartPluginAsync();
        var code = first.Pairing.Status.Code;
        await first.Pairing.StopAsync(CancellationToken.None);

        var restarted = await StartPluginAsync();

        Assert.NotEqual(string.Empty, code);
        Assert.Equal(code, restarted.Pairing.Status.Code);
    }

    [Fact]
    public async Task ARejectedPluginWaitsAnHour()
    {
        await StartGleanvoltAsync();
        var plugin = await StartPluginAsync();
        await Until(() => _host.Registry.Pairing.Count == 1, "the plugin knocked");

        _host.Registry.RejectPairing(_host.Registry.Pairing[0].Id);

        await Until(() => plugin.Pairing.Status.Phase == PairingPhase.Rejected, "the plugin heard the rejection", Poll);
        await Until(() => _time.Created(TimeSpan.FromHours(1)) > 0, "the plugin waits its hour");
        _time.Advance(TimeSpan.FromMinutes(59));
        await Task.Delay(200);
        Assert.Empty(_host.Registry.Pairing);
        Assert.Equal(PairingPhase.Rejected, plugin.Pairing.Status.Phase);

        _time.Advance(TimeSpan.FromMinutes(2));

        await Until(() => _host.Registry.Pairing.Count == 1, "the plugin knocked again after the hour");
        await Until(() => plugin.Pairing.Status.Phase == PairingPhase.Waiting, "the plugin waits for the owner again");
    }

    [Fact]
    public async Task AnUnknownRequestIsKnockedAgain()
    {
        await StartGleanvoltAsync();
        var plugin = await StartPluginAsync();
        await Until(() => _host.Registry.Pairing.Count == 1, "the plugin knocked");

        // Gleanvolt restarts: its waiting list is in memory, so the new one has never heard of the request.
        await _host.DisposeAsync();
        _host = new TestHost();
        await StartGleanvoltAsync();

        await Until(() => _host.Registry.Pairing.Count == 1, "the plugin knocked on the restarted Gleanvolt", Poll);
        Assert.Equal(plugin.Pairing.Status.Code, _host.Registry.Pairing[0].Code);
    }

    [Fact]
    public async Task NoGleanvoltYetKeepsSearching()
    {
        var plugin = await StartPluginAsync();

        await Until(() => plugin.Pairing.Status.Phase == PairingPhase.Searching, "the plugin looks for Gleanvolt");
        for (var i = 0; i < 20; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(10);
        }

        Assert.Equal(PairingPhase.Searching, plugin.Pairing.Status.Phase);
        Assert.False(plugin.Pairing.ExecuteTask?.IsCompleted, "the loop has not stopped");

        await StartGleanvoltAsync();

        await Until(() => plugin.Pairing.Status.Phase == PairingPhase.Waiting, "Gleanvolt is found once it appears", TimeSpan.FromSeconds(10));
        Assert.Single(_host.Registry.Pairing);
    }

    [Fact]
    public async Task AForgottenTokenPairsAgain()
    {
        await StartGleanvoltAsync();
        var plugin = await StartPluginAsync();
        await Until(() => _host.Registry.Pairing.Count == 1, "the plugin knocked");
        await _host.Registry.AcceptPairingAsync(_host.Registry.Pairing[0].Id);
        await Until(() => plugin.Pairing.Status.Phase == PairingPhase.NotNeeded, "the plugin is paired", Poll);
        Assert.Empty(_host.Registry.Pairing);

        plugin.Credentials.ForgetPaired();

        await Until(() => plugin.Pairing.Status.Phase == PairingPhase.Waiting && _host.Registry.Pairing.Count == 1, "the plugin knocked again");
        Assert.Equal(plugin.Pairing.Status.Code, _host.Registry.Pairing[0].Code);
    }

    [Fact]
    public async Task TheFinderTriesTheConfiguredAddressThenThisMachine()
    {
        var finder = new GleanvoltFinder(Options.Create(new ProviderOptions { Endpoint = new Uri("http://192.168.1.5:8090") }));
        var found = new List<Uri>();

        // Only the first two: the mDNS browse after them is the network's, not this test's.
        await foreach (var candidate in finder.CandidatesAsync(CancellationToken.None))
        {
            found.Add(candidate);
            if (found.Count == 2)
            {
                break;
            }
        }

        Assert.Equal([new Uri("http://192.168.1.5:8090"), new Uri("http://localhost:8090")], found);
    }

    private async Task StartGleanvoltAsync()
    {
        await _host.StartAsync();
        _http.Target = _host.Server;
        _finder.Candidates = [_host.Server.BaseAddress];
    }

    private async Task<Plugin> StartPluginAsync(string token = "")
    {
        var options = new ProviderOptions
        {
            Token = token,
            ProviderId = "echo-pairing",
            Name = "Echo pairing",
            Version = "1.0",
            InitialRetryDelay = TimeSpan.FromMilliseconds(50),
            MaxRetryDelay = TimeSpan.FromMilliseconds(100),
            ConfigureHttp = http =>
            {
                http.HttpMessageHandlerFactory = _ => _host.Server.CreateHandler();
                http.Transports = HttpTransportType.LongPolling;
            },
        };
        var store = new ProviderSecretStore(_directory);
        var credentials = new ProviderCredentials(Options.Create(options), store);
        var pairing = new ProviderPairing(options, credentials, _finder, store, new Clients(_http), _time,
            NullLogger<ProviderPairing>.Instance, EchoHub.Path);
        await pairing.StartAsync(CancellationToken.None);
        _running.Add(pairing);
        return new Plugin(pairing, credentials, options);
    }

    // Waits for the condition, moving the plugin's clock on by step between looks when a step is given: the plugin
    // polls and backs off on that clock, never on real time.
    private async Task Until(Func<bool> condition, string because, TimeSpan? step = null)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            if (step is { } advance)
            {
                _time.Advance(advance);
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), because);
    }

    private sealed record Plugin(ProviderPairing Pairing, ProviderCredentials Credentials, ProviderOptions Options);

    private sealed class FakeFinder : IGleanvoltFinder
    {
        public IReadOnlyList<Uri> Candidates { get; set; } = [];

        public async IAsyncEnumerable<Uri> CandidatesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            foreach (var candidate in Candidates)
            {
                yield return candidate;
            }
        }
    }

    /// <summary>Sends to whichever Gleanvolt runs now, so a restarted one is reached at the same address.</summary>
    private sealed class SwitchingHandler : HttpMessageHandler
    {
        public TestServer? Target { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var target = Target ?? throw new HttpRequestException("Nothing listens there.");
            using var invoker = new HttpMessageInvoker(target.CreateHandler(context => context.Connection.RemoteIpAddress = IPAddress.Loopback));
            return await invoker.SendAsync(request, cancellationToken);
        }
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>A fake clock that also says which waits were started on it.</summary>
    private sealed class WatchedTime : FakeTimeProvider
    {
        private readonly List<TimeSpan> _created = [];

        public int Created(TimeSpan dueTime)
        {
            lock (_created)
            {
                return _created.Count(due => due == dueTime);
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_created)
            {
                _created.Add(dueTime);
            }

            return base.CreateTimer(callback, state, dueTime, period);
        }
    }
}
