using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Infrastructure.OpenWeather;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// The weather client, against a stubbed transport. What matters here is not the happy path — it is
/// that <b>nothing</b> this service does can reach the session recorder as an exception, because the
/// caller has a session header to write whatever OpenWeatherMap is doing.
/// </summary>
public sealed class OpenWeatherMapServiceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    // A real response from api.openweathermap.org/data/2.5/weather, trimmed to the fields recorded.
    private const string SampleResponse = """
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

    [Fact]
    public async Task ReadsEveryFigureASessionRecords()
    {
        var handler = new StubHandler(HttpStatusCode.OK, SampleResponse);
        var service = NewService(handler);

        var reading = await service.GetCurrentAsync(Ct);

        Assert.NotNull(reading);
        var observation = reading!.Observation;
        Assert.Equal(19.62, observation.TemperatureCelsius, 2);
        Assert.Equal(101_700, observation.PressurePascals, 2);
        Assert.Equal(51, observation.HumidityPercent, 2);
        Assert.Equal(5, observation.CloudsPercent, 2);
        Assert.Equal(10_000, observation.VisibilityMetres);
        Assert.Equal("Clear", observation.Condition);
        Assert.Equal("clear sky", observation.ConditionDescription);

        // The provider's own stamp, not ours: the reading is typically a few minutes old.
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1787417210), observation.ObservedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1787370925), reading.Sunrise);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1787421524), reading.Sunset);
    }

    [Fact]
    public async Task AskedForTheConfiguredSiteInMetricUnits()
    {
        var handler = new StubHandler(HttpStatusCode.OK, SampleResponse);
        var service = NewService(handler);

        await service.GetCurrentAsync(Ct);

        var uri = handler.LastRequest!.RequestUri!.ToString();
        Assert.Contains("lat=49.267803", uri);
        Assert.Contains("lon=16.529486", uri);
        Assert.Contains("units=metric", uri);
    }

    [Fact]
    public async Task WithoutAKeyItIsInertRatherThanFailing()
    {
        var handler = new StubHandler(HttpStatusCode.OK, SampleResponse);
        var service = NewService(handler, options => new WeatherOptions { ApiKey = string.Empty });

        Assert.False(service.IsConfigured);
        Assert.Null(await service.GetCurrentAsync(Ct));

        // The point of IsConfigured: an unconfigured feature costs no network at all.
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task WithoutCoordinatesItIsInertToo()
    {
        // 0,0 is a real place in the Atlantic, which is why the coordinates are nullable rather than
        // defaulted -- an unset site must record no weather, not the weather in the Gulf of Guinea.
        // They come from the PV system now (issue #111), so "no coordinates" is a site without them.
        var handler = new StubHandler(HttpStatusCode.OK, SampleResponse);
        var service = NewService(handler, site: SiteAt(null, null));

        Assert.False(service.IsConfigured);
        Assert.Null(await service.GetCurrentAsync(Ct));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ARefusedKeyIsANullReadingRatherThanAnException()
    {
        // The 401 One Call 3.0 answers with, which is how the endpoint choice was settled.
        var handler = new StubHandler(HttpStatusCode.Unauthorized, """{"cod":401,"message":"Invalid API key."}""");
        var service = NewService(handler);

        Assert.Null(await service.GetCurrentAsync(Ct));
    }

    [Fact]
    public async Task AProviderOutageIsANullReadingRatherThanAnException()
    {
        var service = NewService(new ThrowingHandler());

        Assert.Null(await service.GetCurrentAsync(Ct));
    }

    [Fact]
    public async Task ASlowProviderIsAbandonedRatherThanHoldingUpTheSession()
    {
        // This call sits between a session ending and its totals being written. A provider that
        // accepts the connection and then says nothing must cost a null column, not the wait.
        var service = NewService(
            new HangingHandler(),
            options => new WeatherOptions
            {
                ApiKey = options.ApiKey,
                RequestTimeout = TimeSpan.FromMilliseconds(50),
            });

        var started = DateTimeOffset.UtcNow;
        Assert.Null(await service.GetCurrentAsync(Ct));
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AResponseWithNoConditionsInItIsNotAReading()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"cod":200,"dt":1787417210}""");
        var service = NewService(handler);

        Assert.Null(await service.GetCurrentAsync(Ct));
    }

    [Fact]
    public async Task AKeySavedOnSettingsIsUsedAtOnceAndWinsOverTheEnvironment()
    {
        var handler = new StubHandler(HttpStatusCode.OK, SampleResponse);
        var secrets = new MemorySecrets();
        var key = new WeatherKeyStore(secrets, Options.Create(Configured()));
        var service = NewService(handler, key: key);

        Assert.Equal(WeatherKeySource.Environment, key.Source);

        Assert.True(key.Save("  page-key "));
        await service.GetCurrentAsync(Ct);

        Assert.Equal(WeatherKeySource.Page, key.Source);
        Assert.Contains("appid=page-key", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("page-key", secrets.Read(SecretNames.WeatherApiKey));

        // Kept across a restart: a new store reads it back.
        Assert.Equal("page-key", new WeatherKeyStore(secrets, Options.Create(Configured())).Current);

        key.Clear();
        await service.GetCurrentAsync(Ct);

        Assert.Contains("appid=test-key", handler.LastRequest!.RequestUri!.ToString());
        Assert.Null(secrets.Read(SecretNames.WeatherApiKey));
    }

    [Fact]
    public async Task AKeySavedWithNoneInTheEnvironmentSwitchesTheWeatherOn()
    {
        var key = new WeatherKeyStore(new MemorySecrets(), Options.Create(new WeatherOptions()));
        var service = NewService(new StubHandler(HttpStatusCode.OK, SampleResponse), key: key);

        Assert.False(service.IsConfigured);

        key.Save("page-key");

        Assert.True(service.IsConfigured);
        Assert.NotNull(await service.GetCurrentAsync(Ct));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, WeatherKeyCheck.Accepted)]
    [InlineData(HttpStatusCode.Unauthorized, WeatherKeyCheck.Refused)]
    [InlineData(HttpStatusCode.TooManyRequests, WeatherKeyCheck.Failed)]
    public async Task ACheckSaysWhatOpenWeatherMapMadeOfTheKey(HttpStatusCode status, WeatherKeyCheck expected)
    {
        var handler = new StubHandler(status, SampleResponse);

        Assert.Equal(expected, await NewService(handler).CheckKeyAsync("new-key", Ct));
        Assert.Contains("appid=new-key", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task ACheckWorksBeforeTheSiteHasALocation()
    {
        // In setup mode the location saved a moment ago is not in use yet; any place answers for a key.
        var handler = new StubHandler(HttpStatusCode.OK, SampleResponse);

        Assert.Equal(WeatherKeyCheck.Accepted, await NewService(handler, site: SiteAt(null, null)).CheckKeyAsync("k", Ct));
        Assert.Contains("lat=0&lon=0", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task ACheckThatCannotReachTheProviderSaysSoRatherThanThrowing()
    {
        Assert.Equal(WeatherKeyCheck.Failed, await NewService(new ThrowingHandler()).CheckKeyAsync("k", Ct));
    }

    private static WeatherOptions Configured() => new() { ApiKey = "test-key" };

    // The site the reading is about. Only its coordinates matter here -- the provider is asked about a
    // place, and the place is a property of the PV system rather than of the weather integration.
    private static PvSystemInfo SiteAt(double? latitude, double? longitude) => new(
        Id: "test-site",
        Name: "Test site",
        Address: string.Empty,
        Latitude: latitude,
        Longitude: longitude,
        AzimuthDegrees: null,
        TiltDegrees: null,
        PeakPowerWatts: null,
        InverterCapacityWatts: null,
        LossFactor: null,
        InstallDate: null,
        Inverter: new PvDeviceInfo("Inverter", string.Empty, string.Empty, new DeviceConfig { Host = "127.0.0.1" }),
        Chargers: []);

    private static OpenWeatherMapService NewService(
        HttpMessageHandler handler,
        Func<WeatherOptions, WeatherOptions>? configure = null,
        PvSystemInfo? site = null,
        WeatherKeyStore? key = null)
    {
        var options = configure is null ? Configured() : configure(Configured());
        return new OpenWeatherMapService(
            new SingleClientFactory(handler, options.BaseUrl),
            Options.Create(options),
            key ?? new WeatherKeyStore(new MemorySecrets(), Options.Create(options)),
            site ?? SiteAt(49.2678029, 16.5294859),
            NullLogger<OpenWeatherMapService>.Instance);
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler, string baseUrl) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri(baseUrl) };
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = [];

        public string? Read(string name) => _values.GetValueOrDefault(name);

        public bool Write(string name, string value)
        {
            _values[name] = value;
            return true;
        }

        public bool Delete(string name) => _values.Remove(name) || true;

        public string Describe() => "memory";
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Name or service not known.");
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        }
    }
}
