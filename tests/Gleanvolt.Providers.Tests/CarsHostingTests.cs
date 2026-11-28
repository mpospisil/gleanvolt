using System.Runtime.CompilerServices;
using Gleanvolt.Cars;
using Gleanvolt.Cars.Components;
using Gleanvolt.Plugins;
using Gleanvolt.Providers.Client;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Vehicle;
using Gleanvolt.Providers.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Gleanvolt.Providers.Tests;

/// <summary>
/// Gleanvolt.Cars.Hosting (#473), what every car plugin shares: one request to the car's maker however many reasons
/// to read arrive at once, nothing asked while the owner is needed, a failure that is reported and heals, and a clock
/// that is faster while Gleanvolt charges.
/// </summary>
public sealed class CarsHostingTests
{
    private static readonly PluginInfo Plugin = new("test-car", "Test car", "TestCar", 8113);

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    [Fact]
    public async Task ThreeReasonsToReadAtOnceMakeOneRequest()
    {
        var car = new CountingCar(_time) { Hold = new TaskCompletionSource() };

        var reads = new[] { car.ReadAsync(null, default), car.ReadAsync(null, default), car.ReadAsync(null, default) };
        await PluginHarness.Eventually(() => car.Fetches == 1, "the first read reaches the maker");
        car.Hold.SetResult();
        var readings = await Task.WhenAll(reads);

        Assert.Equal(1, car.Fetches);
        Assert.All(readings, reading => Assert.Same(readings[0], reading));
        Assert.NotNull(readings[0]);
    }

    [Fact]
    public async Task AReadWithinTheMinimumIsAnsweredFromTheLastOne()
    {
        var car = new CountingCar(_time);

        var first = await car.ReadAsync(null, default);
        _time.Advance(TimeSpan.FromSeconds(10));
        var second = await car.ReadAsync(null, default);

        Assert.Equal(1, car.Fetches);
        Assert.Same(first, second);

        _time.Advance(TimeSpan.FromSeconds(21));
        await car.ReadAsync(null, default);

        Assert.Equal(2, car.Fetches);
    }

    [Fact]
    public async Task ABlockedCarAsksNothingUntilResumed()
    {
        var car = new CountingCar(_time);

        car.BlockNow(new Words("Sign in again.", "Přihlaste se znovu."), ProviderStatusCodes.NeedsSignIn);
        await car.ReadAsync(null, default);

        Assert.Equal(0, car.Fetches);
        Assert.True(car.IsBlocked);
        Assert.True(car.Status!.NeedsOwner);
        Assert.Equal("needs-sign-in", car.Status.Code);
        Assert.Equal(Timeout.InfiniteTimeSpan, car.NextDelay);

        car.Resume();
        await car.ReadAsync(null, default);

        Assert.Equal(1, car.Fetches);
        Assert.False(car.IsBlocked);
    }

    [Fact]
    public async Task AFailedReadReportsUnreachableAndTheNextOneHeals()
    {
        var car = new CountingCar(_time) { Failure = new HttpRequestException("boom") };

        await car.ReadAsync(null, default);

        Assert.False(car.Status!.Healthy);
        Assert.Equal("unreachable", car.Status.Code);
        Assert.False(car.Status.NeedsOwner);
        Assert.Contains("boom", car.Status.Message, StringComparison.Ordinal);
        Assert.False(car.IsBlocked);

        car.Failure = null;
        _time.Advance(TimeSpan.FromSeconds(31));
        await car.ReadAsync(null, default);

        Assert.True(car.Status.Healthy);
    }

    [Fact]
    public async Task AStopIsNotAProblem()
    {
        var car = new CountingCar(_time);
        await car.ReadAsync(null, default);
        var before = car.Status;
        using var stop = new CancellationTokenSource();
        car.Hold = new TaskCompletionSource();
        _time.Advance(TimeSpan.FromSeconds(31));

        var read = car.ReadAsync(null, stop.Token);
        await PluginHarness.Eventually(() => car.Fetches == 2, "the read reaches the maker");
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Same(before, car.Status);
    }

    [Fact]
    public async Task TheClockIsFasterWhileASessionIsOpenOrTheCarCharges()
    {
        var car = new CountingCar(_time);

        Assert.Equal(TimeSpan.FromMinutes(15), car.NextDelay);

        car.ChargingSessionOpen = true;
        Assert.Equal(TimeSpan.FromMinutes(5), car.NextDelay);

        car.ChargingSessionOpen = false;
        car.Reading = new VehicleReading(_time.GetUtcNow(), SocPercent: 40, ChargeState: CarChargeState.Charging);
        await car.ReadAsync(null, default);
        Assert.Equal(TimeSpan.FromMinutes(5), car.NextDelay);
    }

    [Fact]
    public async Task AnUnpairedPluginReadsButPublishesNothing()
    {
        await using var harness = new CarPluginHarness(Plugin);
        var car = new CountingCar(_time);

        var poller = await harness.StartAsync(car, _time);
        poller.Wake();

        await PluginHarness.Eventually(() => car.Fetches == 1, "a wake reads the car");
        Assert.NotNull(car.LastReading);
        Assert.False(harness.Connection!.IsAttached);
    }

    [Fact]
    public async Task APairedPluginPublishesOnTheClockAndOnASessionOpening()
    {
        await using var harness = new CarPluginHarness(Plugin);
        var car = new CountingCar(_time);

        var hub = await harness.ConnectAsync(car, time: _time);
        await hub.WaitForReadingAsync();
        await hub.WaitForStatusAsync(status => status.Healthy);
        var published = hub.Readings.Count;

        _time.Advance(TimeSpan.FromSeconds(31));
        await hub.SetSessionAsync(new ChargingSessionInfo(true, _time.GetUtcNow(), "Targeted"));

        await PluginHarness.Eventually(() => hub.Readings.Count > published, "a session opening reads the car and publishes");
        Assert.Equal(2, car.Fetches);
        Assert.True(car.ChargingSessionOpen);

        // The poller sets its next wait after it publishes, so the clock is moved until it has: a minute at a time.
        await PluginHarness.Eventually(
            () =>
            {
                _time.Advance(TimeSpan.FromMinutes(1));
                return car.Fetches >= 3;
            },
            "the session's clock reads again");
    }

    [Fact]
    public async Task TheCarPageShowsTheLastReading()
    {
        await using var app = await CarApp.StartAsync();
        var car = app.App.Services.GetRequiredService<PageCar>();
        car.Reading = new VehicleReading(
            DateTimeOffset.UtcNow.AddMinutes(-2), SocPercent: 61.5, RangeMetres: 312_000,
            ChargeTimeRemaining: new TimeSpan(1, 20, 0), ChargeState: CarChargeState.Charging, PlugState: CarPlugState.Connected);
        await car.ReadAsync(null, default);

        var body = await app.PageAsync();

        Assert.Contains("id=\"reading\"", body, StringComparison.Ordinal);
        Assert.Contains("61.5 %", body, StringComparison.Ordinal);
        Assert.Contains(">Charging<", body, StringComparison.Ordinal);
        Assert.Contains("Plugged in", body, StringComparison.Ordinal);
        Assert.Contains("312 km", body, StringComparison.Ordinal);
        Assert.Contains("1:20", body, StringComparison.Ordinal);
        Assert.Contains("id=\"read-now\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCarPageIsInCzechForCzech()
    {
        await using var app = await CarApp.StartAsync();
        var car = app.App.Services.GetRequiredService<PageCar>();
        car.Reading = new VehicleReading(DateTimeOffset.UtcNow.AddMinutes(-2), SocPercent: 61.5, PlugState: CarPlugState.Connected);
        await car.ReadAsync(null, default);

        // Blazor writes letters outside ASCII as character references: compared as the browser reads them.
        var body = System.Net.WebUtility.HtmlDecode(await app.PageAsync("&" + ProviderPage.CultureParameter + "=cs"));

        Assert.Contains("Stav nabití", body, StringComparison.Ordinal);
        Assert.Contains("Připojen", body, StringComparison.Ordinal);
        Assert.Contains("61,5 %", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCarPageSaysWhereTheTokenCameFrom()
    {
        await using var app = await CarApp.StartAsync();

        Assert.Contains(HostText.TokenFromPairing.En, await app.PageAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BeforeAnyReadingThePageSaysSo()
    {
        await using var app = await CarApp.StartAsync(readAtStart: false);

        var body = await app.PageAsync();

        Assert.Contains("id=\"reading-none\"", body, StringComparison.Ordinal);
        Assert.Contains(CarText.StatusWaiting.En, body, StringComparison.Ordinal);
    }

    // A car plugin's whole app, as CarPluginHost builds it, on a test server, paired with a Gleanvolt nothing listens on.
    private sealed class CarApp : IAsyncDisposable
    {
        public const string Id = "page-car";

        private readonly string _directory;

        private CarApp(WebApplication app, string directory)
        {
            App = app;
            _directory = directory;
            Client = app.GetTestClient();
        }

        public WebApplication App { get; }

        public HttpClient Client { get; }

        public static async Task<CarApp> StartAsync(bool readAtStart = true)
        {
            var directory = Path.Combine(Path.GetTempPath(), "gleanvolt-plugin-" + Guid.NewGuid().ToString("N"));
            var plugin = new PluginInfo(Id, "Page car", "PageCar", 8114) { PageAssembly = typeof(TestCarPage).Assembly };
            var builder = CarPluginHost.CreateBuilder<PageCar>(
                [$"--DataDirectory={directory}", "--applicationName=Gleanvolt.Plugins.Hosting", "--environment=Development"], plugin);
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IGleanvoltFinder, NoGleanvolt>();
            var app = CarPluginHost.Build(builder);
            app.Services.GetRequiredService<PageCar>().Answers = readAtStart;
            // An endpoint nothing listens on: the connection that starts with the token fails at once and keeps it.
            app.Services.GetRequiredService<ProviderCredentials>().Save(new Uri("http://127.0.0.1:9"), "gvp_paired");
            await app.StartAsync();
            return new CarApp(app, directory);
        }

        public Task<string> PageAsync(string query = "") =>
            Client.GetStringAsync("/car?" + ProviderPage.TicketParameter + "="
                + Uri.EscapeDataString(ProviderPage.CreateTicket("gvp_paired", Id, DateTimeOffset.UtcNow.AddMinutes(2))) + query);

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class NoGleanvolt : IGleanvoltFinder
    {
        public async IAsyncEnumerable<Uri> CandidatesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }
    }

    // A car whose maker answers at once, fails, or waits, on demand.
    private sealed class CountingCar : PluginCar
    {
        private readonly TimeProvider _clock;
        private int _fetches;

        public CountingCar(TimeProvider time)
            : base(new CarPollOptions(), time) => _clock = time;

        public int Fetches => Volatile.Read(ref _fetches);

        public TaskCompletionSource? Hold { get; set; }

        public Exception? Failure { get; set; }

        public VehicleReading? Reading { get; set; }

        public void BlockNow(Words what, string code) => Block(what, code);

        protected override async Task<VehicleReading?> FetchAsync(VehicleInfo? car, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _fetches);
            if (Hold is { } hold)
            {
                await hold.Task.WaitAsync(cancellationToken);
            }

            return Failure is { } failure
                ? throw failure
                : Reading ?? new VehicleReading(_clock.GetUtcNow(), SocPercent: 50 + Fetches);
        }
    }
}

/// <summary>The page test's car: it answers with the reading the test set, or nothing while <see cref="Answers"/> is false.</summary>
public sealed class PageCar(CarPollOptions options, TimeProvider time) : PluginCar(new CarPollOptions { MinimumTimeBetweenReads = TimeSpan.Zero, PollInterval = options.PollInterval }, time)
{
    public bool Answers { get; set; } = true;

    public VehicleReading? Reading { get; set; }

    protected override Task<VehicleReading?> FetchAsync(VehicleInfo? car, CancellationToken cancellationToken) =>
        Task.FromResult(Answers ? Reading : null);
}

/// <summary>The test car plugin's page: the shared frame and nothing of its own.</summary>
[Route("/car")]
public sealed class TestCarPage : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<CarFrame>(0);
        builder.CloseComponent();
    }
}
