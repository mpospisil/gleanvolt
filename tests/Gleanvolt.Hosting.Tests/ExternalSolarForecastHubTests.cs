using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Hosting.Forecasting;
using Gleanvolt.Hosting.Forecasting.External;
using Gleanvolt.Infrastructure.Solcast;
using Gleanvolt.Plugin.SolarForecastSample;
using Gleanvolt.Providers.Client;
using Gleanvolt.Providers.Client.SolarForecast;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.SolarForecast;
using Gleanvolt.Providers.Server;
using ISolarForecastSource = Gleanvolt.Core.Interfaces.ISolarForecastSource;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The solar forecast as an external provider (#388), end to end: the real hub, store, source
/// selection and refresh worker on a TestServer, and the forecast sample as the provider, connecting the
/// way a provider author's would.
/// </summary>
public sealed class ExternalSolarForecastHubTests : IAsyncLifetime
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    // 10:00 in Prague: the sample's sun is up.
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero));

    private readonly CountingSolcast _solcast = new();

    private WebApplication? _app;
    private string _token = string.Empty;

    private SolarForecastStore Store => _app!.Services.GetRequiredService<SolarForecastStore>();

    private ISolarForecastSourceSelection Selection => _app!.Services.GetRequiredService<ISolarForecastSourceSelection>();

    private SolarForecastRefreshWorker Worker => _app!.Services.GetRequiredService<SolarForecastRefreshWorker>();

    private ExternalProviderRegistry Registry => _app!.Services.GetRequiredService<ExternalProviderRegistry>();

    private ExternalProviderStatus Status => Registry.Find(SolarForecastContract.Kind)!;

    public async Task InitializeAsync()
    {
        _time.SetLocalTimeZone(Prague);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["SolarForecast:Source"] = "external" });
        builder.Services.AddSingleton<TimeProvider>(_time);
        builder.Services.AddSingleton<ISecretStore>(new RecordingSecretStore());
        builder.Services.AddSingleton(new ProviderHostInfo("1.3.0-test"));
        builder.Services.AddSingleton(Site);
        builder.Services.AddSingleton(new SolarForecastStore(_time));
        builder.Services.AddSingleton<ISolarForecastService>(provider => provider.GetRequiredService<SolarForecastStore>());
        builder.Services.AddSingleton<ISolarForecastRefresh>(provider => provider.GetRequiredService<SolarForecastStore>());
        builder.Services.Configure<SolarForecastOptions>(builder.Configuration.GetSection(SolarForecastOptions.SectionName));
        builder.Services.AddSingleton(Options.Create(new SolcastOptions()));
        builder.Services.AddSingleton<ISolarForecastSource>(_solcast);
        builder.Services.AddSingleton<ISolarForecastSource, ExternalSolarForecastSource>();
        builder.Services.AddSingleton<SolarForecastSources>();
        builder.Services.AddSingleton<ISolarForecastSourceSelection, SolarForecastSourceEditor>();
        builder.Services.AddSingleton(provider => new SolarForecastRefreshWorker(
            provider.GetRequiredService<ISolarForecastRefresh>(),
            provider.GetRequiredService<SolarForecastSources>(),
            provider.GetRequiredService<IOptions<SolcastOptions>>(),
            NullLogger<SolarForecastRefreshWorker>.Instance,
            _time));
        builder.Services.AddExternalProvider<SolarForecastHub>(SolarForecastHub.Kind);
        builder.Services.AddHostedService<ExternalSolarForecastSelectionWatcher>();

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapExternalProviders();
        await _app.StartAsync();

        _token = await Registry.CreateTokenAsync(SolarForecastContract.Kind);
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
    public async Task TheSampleConnectsLearnsTheSiteAndItsForecastIsWhatThePlannerReads()
    {
        using var sample = Sample();
        SolarForecastWelcome? welcome = null;
        sample.Welcomed += site => welcome = site;

        await sample.StartAsync(CancellationToken.None);
        await Eventually(() => Store.Current is not null, "the sample's forecast should reach the store");

        Assert.Equal(new PvSite(50.08, 14.42, 5, 180, 35, "Europe/Prague"), welcome!.Site);

        // What every planner reads: the service, not the store.
        var service = _app!.Services.GetRequiredService<ISolarForecastService>();
        var today = service.GetForecastForToday();
        Assert.NotNull(today);
        // From 00:30 to 23:30: the period ending at midnight is tomorrow's, as Solcast's is.
        Assert.Equal(47, today.Periods.Count);
        // Sampled at each period's middle, so a little under the curve's peak at solar noon.
        Assert.InRange(today.PeakPowerWatts, 0.95 * SampleForecast.PeakShare * 5000, SampleForecast.PeakShare * 5000);
        Assert.Equal(0, today.Periods[0].EstimatedPowerWatts);
        Assert.True(service.GetForecast(_time.GetUtcNow(), _time.GetUtcNow().AddHours(1))!.Periods.All(period => period.EstimatedPowerWatts > 0));

        Assert.Equal(new SolarForecastOrigin(SolarForecastSourceIds.External, "Solar forecast sample"), service.Current!.Origin);
        Assert.Equal(ExternalProviderState.Connected, Status.State);
        Assert.Equal(_time.GetUtcNow(), Status.LastDataAt);

        await sample.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TheRefreshWorkerAsksAndTheSampleAnswersQuotingTheRequest()
    {
        using var sample = Sample();
        await sample.StartAsync(CancellationToken.None);
        await Eventually(() => Status.LastDataAt is not null, "the sample should publish after its hello");

        _time.Advance(TimeSpan.FromMinutes(10));
        await Worker.RefreshOnceAsync(CancellationToken.None);

        await Eventually(() => Status.LastDataAt == _time.GetUtcNow(), "the sample should answer the request");
        Assert.Equal(_time.GetUtcNow(), Store.Current!.ReceivedAt);

        _time.Advance(ExternalSolarForecastSource.Timeout + TimeSpan.FromSeconds(1));
        await Task.Delay(100);
        Assert.False(Status.Unanswered);
        Assert.Equal(0, _solcast.Calls);

        await sample.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ARequestWithNoAnswerShowsAsUnansweredAndTheWorkerIsNotHeld()
    {
        await using var silent = await ConnectAsync();
        Assert.True((await silent.InvokeAsync<SolarForecastWelcome>("Hello", Hello())).Accepted);

        await Worker.RefreshOnceAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(Status.Unanswered);
        _time.Advance(ExternalSolarForecastSource.Timeout + TimeSpan.FromSeconds(1));
        await Eventually(() => Status.Unanswered, "the request should time out");
    }

    [Fact]
    public async Task ALateAnswerQuotingTheRequestClearsIt()
    {
        await using var connection = await ConnectAsync();
        RefreshRequest? asked = null;
        connection.On<RefreshRequest>("RefreshRequested", request => asked = request);
        await connection.InvokeAsync<SolarForecastWelcome>("Hello", Hello());

        await Worker.RefreshOnceAsync(CancellationToken.None);
        await Eventually(() => asked is not null, "the worker's request should reach the provider");

        var result = await connection.InvokeAsync<PublishResult>("Publish", Forecast(inReplyTo: asked!.Id));

        Assert.True(result.Accepted, string.Join(" ", result.Problems));
        _time.Advance(ExternalSolarForecastSource.Timeout + TimeSpan.FromSeconds(1));
        await Task.Delay(100);
        Assert.False(Status.Unanswered);
    }

    [Fact]
    public async Task RefusedDataChangesNothingAndSaysWhy()
    {
        await using var connection = await ConnectAsync();
        await connection.InvokeAsync<SolarForecastWelcome>("Hello", Hello());

        // 7 kW on a 5 kWp roof.
        var result = await connection.InvokeAsync<PublishResult>("Publish", Forecast(watts: 7000));

        Assert.False(result.Accepted);
        Assert.Contains("peaks at 7 kW", Assert.Single(result.Problems));
        Assert.Null(Store.Current);
        Assert.Equal("SolarForecast.Peak", Status.LastProblemPhrases![0]!.Key);
    }

    [Fact]
    public async Task TheLengthTravelsAsATimeSpanString()
    {
        await using var connection = await ConnectAsync();
        await connection.InvokeAsync<SolarForecastWelcome>("Hello", Hello());
        var now = _time.GetUtcNow();

        // What a client in another language sends.
        var result = await connection.InvokeAsync<PublishResult>("Publish", new
        {
            issuedAt = now,
            periods = new[] { new { end = now.AddMinutes(30), length = "00:30:00", powerWatts = 1200.0 } },
        });

        Assert.True(result.Accepted, string.Join(" ", result.Problems));
        Assert.Equal(TimeSpan.FromMinutes(30), Store.Current!.Forecast.Periods[0].Period);
        Assert.Equal(1200, Store.Current.Forecast.Periods[0].EstimatedPowerWatts);
    }

    [Fact]
    public async Task AfterADisconnectTheLastForecastStays()
    {
        using var sample = Sample();
        await sample.StartAsync(CancellationToken.None);
        await Eventually(() => Store.Current is not null, "the sample should publish");

        await sample.StopAsync(CancellationToken.None);
        await Eventually(() => Status.State == ExternalProviderState.Disconnected, "the session should end");

        Assert.Equal(SolarForecastSourceIds.External, Store.Current!.Origin.SourceId);
        Assert.NotNull(_app!.Services.GetRequiredService<ISolarForecastService>().GetForecastForToday());

        // Asking with no provider sends nothing, and Solcast is not asked instead.
        await Worker.RefreshOnceAsync(CancellationToken.None);
        Assert.Equal(0, _solcast.Calls);
    }

    [Fact]
    public async Task ChoosingSolcastClosesTheSessionRefusesTheNextHelloAndAsksSolcast()
    {
        await using var connection = await ConnectAsync();
        await connection.InvokeAsync<SolarForecastWelcome>("Hello", Hello());
        var goodbye = new TaskCompletionSource<string>();
        connection.On<string, string>("Goodbye", (code, _) => goodbye.TrySetResult(code));

        Assert.True(Selection.Select(SolarForecastSourceIds.Solcast).Saved);

        Assert.Equal(RefusalCodes.Disabled, await goodbye.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ExternalProviderState.Off, Status.State);
        Assert.Equal(SolarForecastSourceIds.Solcast, Selection.Selected);

        await using var again = await ConnectAsync();
        Assert.Equal(RefusalCodes.Disabled, (await again.InvokeAsync<SolarForecastWelcome>("Hello", Hello())).ReasonCode);

        await Worker.RefreshOnceAsync(CancellationToken.None);
        Assert.Equal(1, _solcast.Calls);
    }

    [Fact]
    public void AnUnknownSourceIsRefusedAndNothingChanges()
    {
        var result = Selection.Select("meteoblue");

        Assert.False(result.Saved);
        Assert.Equal("SolarForecast.UnknownSource", result.ProblemPhrases![0]!.Key);
        Assert.Equal(SolarForecastSourceIds.External, Selection.Selected);
    }

    private static PvSystemInfo Site { get; } = new(
        Id: "test-site",
        Name: "Test site",
        Address: string.Empty,
        Latitude: 50.08,
        Longitude: 14.42,
        AzimuthDegrees: 180,
        TiltDegrees: 35,
        PeakPowerWatts: 5000,
        InverterCapacityWatts: null,
        LossFactor: null,
        InstallDate: null,
        Inverter: new PvDeviceInfo("Inverter", string.Empty, string.Empty, new DeviceConfig { Host = "127.0.0.1" }),
        Chargers: []);

    private SolarForecastData Forecast(double watts = 1000, RequestId? inReplyTo = null)
    {
        var now = _time.GetUtcNow();
        var half = TimeSpan.FromMinutes(30);

        return new SolarForecastData(now, inReplyTo, [.. Enumerable.Range(1, 6).Select(i => new ForecastPeriod(now + half * i, half, watts))]);
    }

    private SolarForecastProviderConnection Sample() => new(
        Options.Create(new ProviderOptions
        {
            Endpoint = _app!.GetTestServer().BaseAddress,
            Token = _token,
            ProviderId = "solar-forecast-sample",
            Name = "Solar forecast sample",
            Version = "1.0",
            InitialRetryDelay = TimeSpan.FromMilliseconds(50),
            MaxRetryDelay = TimeSpan.FromMilliseconds(200),
            RefusedRetryDelay = TimeSpan.FromMilliseconds(100),
            ConfigureHttp = Configure,
        }),
        new SampleForecast(_time),
        NullLogger<SolarForecastProviderConnection>.Instance);

    private async Task<HubConnection> ConnectAsync()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_app!.GetTestServer().BaseAddress, SolarForecastContract.HubPath), Configure)
            .Build();
        await connection.StartAsync();
        return connection;
    }

    private void Configure(Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions options)
    {
        options.HttpMessageHandlerFactory = _ => _app!.GetTestServer().CreateHandler();
        options.Transports = HttpTransportType.LongPolling;
        options.AccessTokenProvider = () => Task.FromResult<string?>(_token);
    }

    private static ProviderHello Hello() => new("raw-client", "Raw client", "0.1", SolarForecastContract.Major, SolarForecastContract.Minor);

    private static async Task Eventually(Func<bool> condition, string because)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(condition(), because);
    }

    /// <summary>A Solcast that only counts: whether the worker asked it.</summary>
    private sealed class CountingSolcast : ISolarForecastSource
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public string Id => SolarForecastSourceIds.Solcast;

        public Task RequestRefreshAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.CompletedTask;
        }
    }
}
