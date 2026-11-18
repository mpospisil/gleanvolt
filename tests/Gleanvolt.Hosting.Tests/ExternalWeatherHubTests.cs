using System.Net;
using System.Text;
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
using Gleanvolt.Hosting.Weather;
using Gleanvolt.Plugin.OpenWeather.OpenWeather;
using Gleanvolt.Providers.Client;
using Gleanvolt.Providers.Client.Weather;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Weather;
using Gleanvolt.Providers.Server;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The weather as an external provider (#424), end to end: the real hub, selection and services on a
/// TestServer, and the OpenWeather plugin as the provider, with OpenWeatherMap's answers faked. What the
/// session recorder gets is what <see cref="IWeatherService"/> answers, so that is what is checked.
/// </summary>
public sealed class ExternalWeatherHubTests : IAsyncLifetime, IDisposable
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    // Ten minutes after the sample's own observation time.
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(OpenWeatherFixture.ObservedAt).AddMinutes(10));

    private readonly OpenWeatherFixture.Stub _openWeather = new(HttpStatusCode.OK, OpenWeatherFixture.Sample);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gleanvolt-weather-" + Guid.NewGuid().ToString("N"));

    private WebApplication? _app;
    private string _token = string.Empty;

    private IWeatherService Weather => _app!.Services.GetRequiredService<IWeatherService>();

    private IWeatherSourceSelection Selection => _app!.Services.GetRequiredService<IWeatherSourceSelection>();

    private ExternalProviderRegistry Registry => _app!.Services.GetRequiredService<ExternalProviderRegistry>();

    private ExternalProviderStatus Status => Registry.Find(WeatherContract.Kind)!;

    public async Task InitializeAsync()
    {
        _time.SetLocalTimeZone(Prague);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Weather:Source"] = "external",
            // As after the upgrade that removed the built-in (#425): the notice stands until a provider connects.
            ["Weather:BuiltInRetired"] = "true",
        });
        builder.Services.AddSingleton<TimeProvider>(_time);
        builder.Services.AddSingleton<ISecretStore>(new RecordingSecretStore());
        builder.Services.AddSingleton(new ProviderHostInfo("1.4.0-test"));
        builder.Services.AddSingleton(Site);
        builder.Services.Configure<WeatherOptions>(builder.Configuration.GetSection(WeatherOptions.SectionName));
        builder.Services.AddSingleton<WeatherSourceSelection>();
        builder.Services.AddSingleton<IWeatherSourceSelection>(provider => provider.GetRequiredService<WeatherSourceSelection>());
        builder.Services.AddSingleton<ExternalWeatherService>();
        builder.Services.AddSingleton<IWeatherService, SelectedWeatherService>();
        builder.Services.AddExternalProvider<WeatherHub>(WeatherHub.Kind);
        builder.Services.AddHostedService<ExternalWeatherSelectionWatcher>();

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapExternalProviders();
        await _app.StartAsync();

        _token = await Registry.CreateTokenAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task ThePluginLearnsTheSiteAndItsReadingIsWhatASessionIsRecordedWith()
    {
        using var plugin = Plugin(out var feed);
        WeatherWelcome? welcome = null;
        plugin.Welcomed += hello => welcome = hello;

        await plugin.StartAsync(CancellationToken.None);
        await Eventually(() => Status.LastDataAt is not null, "the plugin should publish after its hello");

        Assert.Equal(new WeatherSite(49.2678, 16.5295, "Europe/Prague"), welcome!.Site);
        Assert.Contains("lat=49.2678&lon=16.5295", _openWeather.LastRequest!.RequestUri!.ToString());
        Assert.Equal(ExternalProviderState.Connected, Status.State);
        Assert.Equal("OpenWeather", Status.ProviderName);
        Assert.True(Status.HasPage);

        Assert.True(Weather.IsConfigured);
        Assert.Equal(WeatherSource.External, Weather.Source);

        var reading = await Weather.GetCurrentAsync();

        Assert.NotNull(reading);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(OpenWeatherFixture.ObservedAt), reading.Observation.ObservedAt);
        Assert.Equal(19.62, reading.Observation.TemperatureCelsius);
        Assert.Equal(101_700, reading.Observation.PressurePascals);
        Assert.Equal(5, reading.Observation.CloudsPercent);
        Assert.Equal("clear sky", reading.Observation.ConditionDescription);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1787370925), reading.Sunrise);

        // Fresh: the hello's reading was used, and OpenWeatherMap was asked once.
        Assert.Equal(1, _openWeather.Calls);
        Assert.True(feed.Status!.Healthy);

        await plugin.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AReadingOlderThanTenMinutesIsAskedForAgainAndTheAnswerQuotesTheRequest()
    {
        using var plugin = Plugin(out _);
        await plugin.StartAsync(CancellationToken.None);
        await Eventually(() => Status.LastDataAt is not null, "the plugin should publish after its hello");

        _time.Advance(TimeSpan.FromMinutes(11));
        var reading = await Weather.GetCurrentAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(reading);
        Assert.Equal(2, _openWeather.Calls);
        Assert.Equal(_time.GetUtcNow(), Status.LastDataAt);
        Assert.False(Status.Unanswered);

        await plugin.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AProviderThatDoesNotAnswerMeansNoWeatherAndShowsAsUnanswered()
    {
        await using var silent = await ConnectAsync();
        RefreshRequest? asked = null;
        silent.On<RefreshRequest>("RefreshRequested", request => asked = request);
        Assert.True((await silent.InvokeAsync<WeatherWelcome>("Hello", Hello())).Accepted);

        var pending = Weather.GetCurrentAsync();
        await Eventually(() => asked is not null, "the request should reach the provider");
        Assert.False(pending.IsCompleted);

        _time.Advance(TimeSpan.FromSeconds(16));

        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await Eventually(() => Status.Unanswered, "the request should show as unanswered");
    }

    [Fact]
    public async Task WithNoProviderConnectedThereIsNoWeatherAndNothingWaits()
    {
        Assert.True(Weather.IsConfigured);

        Assert.Null(await Weather.GetCurrentAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, _openWeather.Calls);
    }

    [Fact]
    public async Task ARefusedReadingChangesNothingAndSaysWhy()
    {
        await using var connection = await ConnectAsync();
        await connection.InvokeAsync<WeatherWelcome>("Hello", Hello());

        // Fahrenheit.
        var result = await connection.InvokeAsync<PublishResult>("Publish",
            new WeatherData(_time.GetUtcNow(), TemperatureCelsius: 95, Condition: "Clear"));

        Assert.False(result.Accepted);
        Assert.Contains("95 °C", Assert.Single(result.Problems));
        Assert.Equal("Weather.Temperature", Status.LastProblemPhrases![0]!.Key);
        Assert.Null(_app!.Services.GetRequiredService<ExternalWeatherService>().Current);
    }

    [Fact]
    public async Task AReadingWithoutCloudCoverIsHeldWithTheCloudsUnknown()
    {
        await using var connection = await ConnectAsync();
        await connection.InvokeAsync<WeatherWelcome>("Hello", Hello());

        // What a client in another language sends: only what its station measures.
        var result = await connection.InvokeAsync<PublishResult>("Publish", new
        {
            observedAt = _time.GetUtcNow().AddMinutes(-2),
            temperatureCelsius = 3.5,
            humidityPercent = 88,
        });

        Assert.True(result.Accepted, string.Join(" ", result.Problems));
        var reading = await Weather.GetCurrentAsync();
        Assert.Equal(3.5, reading!.Observation.TemperatureCelsius);
        Assert.Null(reading.Observation.CloudsPercent);
        Assert.Null(reading.Observation.PressurePascals);
        Assert.Equal(string.Empty, reading.Observation.Condition);
    }

    [Fact]
    public async Task WithNoKeyThePluginSaysTheOwnerIsNeeded()
    {
        using var plugin = Plugin(out var feed, key: null);
        await plugin.StartAsync(CancellationToken.None);

        await Eventually(() => Status.NeedsOwner, "the plugin should report that it has no key");

        Assert.Equal(ProviderStatusCodes.NotSetUp, Status.StatusCode);
        Assert.Null(Status.LastDataAt);
        Assert.Equal(0, _openWeather.Calls);
        Assert.False(feed.Status!.Healthy);

        await plugin.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AProviderConnectingEndsTheNoticeAboutTheBuiltIn()
    {
        Assert.True(Selection.BuiltInRetired);

        await using var connection = await ConnectAsync();
        await connection.InvokeAsync<WeatherWelcome>("Hello", Hello());

        await Eventually(() => !Selection.BuiltInRetired, "the notice should go once a weather provider connects");
    }

    [Fact]
    public async Task ChoosingNotProvidedClosesTheSessionAndRefusesTheNextHello()
    {
        await using var connection = await ConnectAsync();
        await connection.InvokeAsync<WeatherWelcome>("Hello", Hello());
        var goodbye = new TaskCompletionSource<string>();
        connection.On<string, string>("Goodbye", (code, _) => goodbye.TrySetResult(code));

        Assert.True(Selection.Select(WeatherSource.NotProvided).Saved);

        Assert.Equal(RefusalCodes.Disabled, await goodbye.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ExternalProviderState.Off, Status.State);
        Assert.Equal(WeatherSource.NotProvided, Weather.Source);
        Assert.False(Weather.IsConfigured);
        Assert.Null(await Weather.GetCurrentAsync());

        await using var again = await ConnectAsync();
        Assert.Equal(RefusalCodes.Disabled, (await again.InvokeAsync<WeatherWelcome>("Hello", Hello())).ReasonCode);
    }

    [Fact]
    public async Task ChoosingNotProvidedMeansNoWeather()
    {
        Assert.True(Selection.Select(WeatherSource.NotProvided).Saved);

        Assert.Equal(WeatherSource.NotProvided, Weather.Source);
        Assert.False(Weather.IsConfigured);
        Assert.Null(await Weather.GetCurrentAsync());
    }

    private static PvSystemInfo Site { get; } = new(
        Id: "test-site",
        Name: "Test site",
        Address: string.Empty,
        Latitude: 49.2678,
        Longitude: 16.5295,
        AzimuthDegrees: 180,
        TiltDegrees: 35,
        PeakPowerWatts: 5000,
        InverterCapacityWatts: null,
        LossFactor: null,
        InstallDate: null,
        Inverter: new PvDeviceInfo("Inverter", string.Empty, string.Empty, new DeviceConfig { Host = "127.0.0.1" }),
        Chargers: []);

    // The plugin as Program.cs wires it, on this TestServer and this clock.
    private WeatherProviderConnection Plugin(out OpenWeatherFeed feed, string? key = "owm-test-key")
    {
        var options = new OpenWeatherOptions { DataDirectory = _directory };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OpenWeather:ApiKey"] = key })
            .Build();

        feed = new OpenWeatherFeed(
            new OpenWeatherClient(new HttpClient(_openWeather, disposeHandler: false) { BaseAddress = new Uri(options.BaseUrl) }, options),
            new ApiKeyStore(new ProviderSecretStore(_directory), configuration),
            options,
            _time,
            NullLogger<OpenWeatherFeed>.Instance);

        var connection = new WeatherProviderConnection(
            Options.Create(new ProviderOptions
            {
                Endpoint = _app!.GetTestServer().BaseAddress,
                Token = _token,
                ProviderId = "openweather",
                Name = "OpenWeather",
                Version = "1.0",
                PageUrl = new Uri("http://localhost:8096/"),
                InitialRetryDelay = TimeSpan.FromMilliseconds(50),
                MaxRetryDelay = TimeSpan.FromMilliseconds(200),
                RefusedRetryDelay = TimeSpan.FromMilliseconds(100),
                ConfigureHttp = Configure,
            }),
            feed,
            NullLogger<WeatherProviderConnection>.Instance);

        _ = new StatusReporter(connection, feed, NullLogger<StatusReporter>.Instance);
        return connection;
    }

    private async Task<HubConnection> ConnectAsync()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_app!.GetTestServer().BaseAddress, WeatherContract.HubPath), Configure)
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

    private static ProviderHello Hello() => new("raw-client", "Raw client", "0.1", WeatherContract.Major, WeatherContract.Minor);

    private static async Task Eventually(Func<bool> condition, string because)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(condition(), because);
    }
}

/// <summary>OpenWeatherMap's answer, as the built-in's tests and the plugin's share it.</summary>
internal static class OpenWeatherFixture
{
    /// <summary>The sample's <c>dt</c>.</summary>
    public const long ObservedAt = 1787417210;

    // A real response from api.openweathermap.org/data/2.5/weather, trimmed to the fields recorded; the
    // same one the built-in read before #425.
    public const string Sample = """
        {
          "weather": [{ "id": 800, "main": "Clear", "description": "clear sky", "icon": "01d" }],
          "main": { "temp": 19.62, "feels_like": 18.97, "pressure": 1017, "humidity": 51 },
          "visibility": 10000,
          "wind": { "speed": 6.69, "deg": 320 },
          "clouds": { "all": 5 },
          "dt": 1787417210,
          "sys": { "country": "CZ", "sunrise": 1787370925, "sunset": 1787421524 },
          "timezone": 7200,
          "cod": 200
        }
        """;

    /// <summary>Answers every request with one status and body, and counts them.</summary>
    public sealed class Stub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public HttpRequestMessage? LastRequest { get; private set; }

        public HttpStatusCode Status { get; set; } = status;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
