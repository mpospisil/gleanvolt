using System.Net;
using System.Runtime.CompilerServices;
using Gleanvolt.Providers.Client;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task AGleanvoltThatAlwaysForgetsIsKnockedOnWithAGrowingBackoff()
    {
        // As two copies of one plugin see it: each replaces the other's request, so every collect is Unknown.
        var gleanvolt = new ForgetfulGleanvolt(_time);
        var log = new RecordingLogger();
        _finder.Candidates = [new Uri("http://192.0.2.5:8090")];
        await StartPluginAsync(http: gleanvolt, logger: log, defaultDelays: true);

        for (var wait = 0; wait < 100 && gleanvolt.Knocks.Count < 8; wait++)
        {
            await AdvanceThroughWaitAsync(wait);
        }

        // Each knock is a 3 s poll that hears Unknown, then the backoff: 2 s doubling up to a minute.
        var spacings = gleanvolt.Knocks.Zip(gleanvolt.Knocks.Skip(1), (first, next) => (next - first).TotalSeconds).Take(7);
        Assert.Equal([5.0, 7, 11, 19, 35, 63, 63], spacings);
        Assert.Single(log.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains("no longer knows", StringComparison.Ordinal));
        Assert.True(log.Entries.Count(entry => entry.Message.Contains("no longer knows", StringComparison.Ordinal)) >= 7);
        // The first round's waiting and Unknown; every round after it is quiet.
        Assert.Equal(2, log.Entries.Count(entry => entry.Level >= LogLevel.Information));
    }

    [Fact]
    public async Task ARefusedKnockIsSaidOnceAsARefusal()
    {
        var log = new RecordingLogger();
        _finder.Candidates = [new Uri("http://192.0.2.5:8090")];
        await StartPluginAsync(http: new Answering(HttpStatusCode.BadRequest), logger: log);

        await Until(() => log.Entries.Count(entry => entry.Message.Contains("refused", StringComparison.Ordinal)) >= 3,
            "the plugin knocked a few times", TimeSpan.FromSeconds(1));

        var warning = Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("refused the pairing request (400)", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(log.Entries, entry => entry.Message.Contains("Found no Gleanvolt", StringComparison.Ordinal));
    }

    // Moves the clock to the end of the plugin's wait number index (from 0), once it has started it: a poll or a
    // backoff, so the plugin runs on the clock alone and never on how fast this machine is.
    private async Task AdvanceThroughWaitAsync(int index)
    {
        for (var i = 0; i < 400 && _time.Started <= index; i++)
        {
            await Task.Delay(5);
        }

        Assert.True(_time.Started > index, "the plugin started a wait");
        _time.Advance(_time.Due(index) - _time.GetUtcNow());
    }

    private async Task StartGleanvoltAsync()
    {
        await _host.StartAsync();
        _http.Target = _host.Server;
        _finder.Candidates = [_host.Server.BaseAddress];
    }

    private async Task<Plugin> StartPluginAsync(string token = "", HttpMessageHandler? http = null, ILogger<ProviderPairing>? logger = null,
        bool defaultDelays = false)
    {
        var options = new ProviderOptions
        {
            Token = token,
            ProviderId = "echo-pairing",
            Name = "Echo pairing",
            Version = "1.0",
            InitialRetryDelay = defaultDelays ? TimeSpan.FromSeconds(2) : TimeSpan.FromMilliseconds(50),
            MaxRetryDelay = defaultDelays ? TimeSpan.FromMinutes(1) : TimeSpan.FromMilliseconds(100),
            ConfigureHttp = http =>
            {
                http.HttpMessageHandlerFactory = _ => _host.Server.CreateHandler();
                http.Transports = HttpTransportType.LongPolling;
            },
        };
        var store = new ProviderSecretStore(_directory);
        var credentials = new ProviderCredentials(Options.Create(options), store);
        var pairing = new ProviderPairing(options, credentials, _finder, store, new Clients(http ?? _http), _time,
            logger ?? NullLogger<ProviderPairing>.Instance, EchoHub.Path);
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

    /// <summary>Takes every knock and has forgotten it by the first collect, noting when each knock came.</summary>
    private sealed class ForgetfulGleanvolt(TimeProvider time) : HttpMessageHandler
    {
        private readonly List<DateTimeOffset> _knocks = [];

        public IReadOnlyList<DateTimeOffset> Knocks
        {
            get
            {
                lock (_knocks)
                {
                    return [.. _knocks];
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/collect", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("""{"state":"unknown"}"""));
            }

            lock (_knocks)
            {
                _knocks.Add(time.GetUtcNow());
            }

            return Task.FromResult(Json("""{"requestId":"r1"}"""));
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
    }

    private sealed class Answering(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class RecordingLogger : ILogger<ProviderPairing>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    /// <summary>A fake clock that also says which waits were started on it.</summary>
    private sealed class WatchedTime : FakeTimeProvider
    {
        private readonly List<TimeSpan> _created = [];
        private readonly List<DateTimeOffset> _due = [];

        /// <summary>How many waits were started.</summary>
        public int Started
        {
            get
            {
                lock (_created)
                {
                    return _created.Count;
                }
            }
        }

        /// <summary>When the wait number <paramref name="index"/> (from 0) ends.</summary>
        public DateTimeOffset Due(int index)
        {
            lock (_created)
            {
                return _due[index];
            }
        }

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
                _due.Add(GetUtcNow() + dueTime);
            }

            return base.CreateTimer(callback, state, dueTime, period);
        }
    }
}
