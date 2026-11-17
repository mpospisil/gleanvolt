using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Gleanvolt.Core.Configuration;
using Gleanvolt.Hosting.Weather;
using Gleanvolt.Plugin.OpenWeather.OpenWeather;
using Gleanvolt.Providers.Client;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Weather;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The OpenWeather plugin's own parts (#424): the copied mapping, checked against Gleanvolt's validator;
/// the key check; and the statuses the feed tells Gleanvolt, with the calls it spends.
/// </summary>
public sealed class OpenWeatherPluginTests : IDisposable
{
    private static readonly WeatherSite Brno = new(49.2678, 16.5295, "Europe/Prague");

    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(OpenWeatherFixture.ObservedAt).AddMinutes(10));
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gleanvolt-openweather-" + Guid.NewGuid().ToString("N"));
    private readonly OpenWeatherOptions _options;

    public OpenWeatherPluginTests() => _options = new OpenWeatherOptions { DataDirectory = _directory };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task TheReadingIsMappedFieldForFieldAndPassesGleanvoltsChecks()
    {
        var (feed, _) = Feed(HttpStatusCode.OK);

        var data = await feed.GetReadingAsync(Brno, CancellationToken.None);

        Assert.Equal(new WeatherData(
            DateTimeOffset.FromUnixTimeSeconds(OpenWeatherFixture.ObservedAt),
            TemperatureCelsius: 19.62,
            PressurePascals: 101_700,
            HumidityPercent: 51,
            CloudsPercent: 5,
            VisibilityMetres: 10_000,
            Condition: "Clear",
            ConditionDescription: "clear sky",
            Sunrise: DateTimeOffset.FromUnixTimeSeconds(1787370925),
            Sunset: DateTimeOffset.FromUnixTimeSeconds(1787421524)), data);

        Assert.Empty(WeatherReadingValidator.Check(WeatherHub.ToReading(data!), null, _time.GetUtcNow()));
        Assert.True(feed.Status!.Healthy);
    }

    [Fact]
    public async Task ItAsksForMetricUnitsAtTheSiteWithTheKey()
    {
        var (feed, stub) = Feed(HttpStatusCode.OK);

        await feed.GetReadingAsync(Brno, CancellationToken.None);

        var uri = stub.LastRequest!.RequestUri!.ToString();
        Assert.Contains("data/2.5/weather?lat=49.2678&lon=16.5295&units=metric&appid=owm-test-key", uri);
    }

    [Fact]
    public async Task ASecondRequestWithinTenMinutesReusesTheReading()
    {
        var (feed, stub) = Feed(HttpStatusCode.OK);

        await feed.GetReadingAsync(Brno, CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(9));
        await feed.GetReadingAsync(Brno, CancellationToken.None);

        Assert.Equal(1, stub.Calls);

        _time.Advance(TimeSpan.FromMinutes(2));
        await feed.GetReadingAsync(Brno, CancellationToken.None);

        Assert.Equal(2, stub.Calls);
    }

    [Fact]
    public async Task AnotherSiteIsReadAtOnce()
    {
        var (feed, stub) = Feed(HttpStatusCode.OK);

        await feed.GetReadingAsync(Brno, CancellationToken.None);
        await feed.GetReadingAsync(Brno with { Latitude = 50.08, Longitude = 14.42 }, CancellationToken.None);

        Assert.Equal(2, stub.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ProviderStatusCodes.Refused, true)]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderStatusCodes.RateLimited, false)]
    [InlineData(HttpStatusCode.BadGateway, ProviderStatusCodes.Unreachable, false)]
    public async Task AFailureIsAStatusAndOnlyARefusedKeyNeedsTheOwner(HttpStatusCode status, string code, bool needsOwner)
    {
        var (feed, _) = Feed(status);

        Assert.Null(await feed.GetReadingAsync(Brno, CancellationToken.None));

        Assert.False(feed.Status!.Healthy);
        Assert.Equal(code, feed.Status.Code);
        Assert.Equal(needsOwner, feed.Status.NeedsOwner);
        Assert.Equal(["en", "cs"], feed.Status.Messages!.Keys);
    }

    [Fact]
    public async Task AnAnswerItCannotReadSaysTheServiceChanged()
    {
        var stub = new OpenWeatherFixture.Stub(HttpStatusCode.OK, """{ "cod": 200 }""");
        var feed = NewFeed(stub, "owm-test-key");

        Assert.Null(await feed.GetReadingAsync(Brno, CancellationToken.None));

        Assert.Equal(ProviderStatusCodes.ServiceChanged, feed.Status!.Code);
    }

    [Fact]
    public async Task WithNoKeyOrNoLocationNothingIsCalledAndTheOwnerIsNeeded()
    {
        var stub = new OpenWeatherFixture.Stub(HttpStatusCode.OK, OpenWeatherFixture.Sample);
        var feed = NewFeed(stub, key: null);

        Assert.Null(await feed.GetReadingAsync(Brno, CancellationToken.None));
        Assert.Equal(ProviderStatusCodes.NotSetUp, feed.Status!.Code);
        Assert.True(feed.Status.NeedsOwner);

        var keyed = NewFeed(stub, "owm-test-key");
        Assert.Null(await keyed.GetReadingAsync(Brno with { Latitude = null }, CancellationToken.None));
        Assert.Equal(ProviderStatusCodes.NotSetUp, keyed.Status!.Code);
        Assert.Contains("location", keyed.Status.Message);

        Assert.Equal(0, stub.Calls);
    }

    [Fact]
    public async Task AKeySavedOnThePageWinsAndIsReadAgainAtOnce()
    {
        var stub = new OpenWeatherFixture.Stub(HttpStatusCode.OK, OpenWeatherFixture.Sample);
        var key = new ApiKeyStore(new ProviderSecretStore(_directory), Configuration("from-config"));
        var feed = new OpenWeatherFeed(Client(stub), key, _options, _time, NullLogger<OpenWeatherFeed>.Instance);

        await feed.GetReadingAsync(Brno, CancellationToken.None);
        Assert.Equal(ApiKeySource.Environment, key.Source);

        Assert.True(key.Save(" from-page "));
        feed.KeyChanged();
        await feed.GetReadingAsync(Brno, CancellationToken.None);

        Assert.Equal(ApiKeySource.Page, key.Source);
        Assert.Contains("appid=from-page", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(2, stub.Calls);

        // Kept across a restart, in the data directory.
        Assert.Equal("from-page", new ApiKeyStore(new ProviderSecretStore(_directory), Configuration(null)).Current);

        key.Clear();
        Assert.Equal("from-config", key.Current);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, KeyCheck.Accepted)]
    [InlineData(HttpStatusCode.Unauthorized, KeyCheck.Refused)]
    [InlineData(HttpStatusCode.TooManyRequests, KeyCheck.Failed)]
    public async Task ACheckSaysWhatOpenWeatherMapMadeOfTheKey(HttpStatusCode status, KeyCheck expected)
    {
        var stub = new OpenWeatherFixture.Stub(status, OpenWeatherFixture.Sample);

        Assert.Equal(expected, await Client(stub).CheckKeyAsync(" new-key ", null, null, CancellationToken.None));
        Assert.Contains("lat=0&lon=0", stub.LastRequest!.RequestUri!.ToString());
        Assert.Contains("appid=new-key", stub.LastRequest.RequestUri.ToString());
    }

    private (OpenWeatherFeed Feed, OpenWeatherFixture.Stub Stub) Feed(HttpStatusCode status)
    {
        var stub = new OpenWeatherFixture.Stub(status, OpenWeatherFixture.Sample);
        return (NewFeed(stub, "owm-test-key"), stub);
    }

    private OpenWeatherFeed NewFeed(OpenWeatherFixture.Stub stub, string? key) =>
        new(Client(stub), new ApiKeyStore(new ProviderSecretStore(Path.Combine(_directory, Guid.NewGuid().ToString("N"))), Configuration(key)),
            _options, _time, NullLogger<OpenWeatherFeed>.Instance);

    private OpenWeatherClient Client(OpenWeatherFixture.Stub stub) =>
        new(new HttpClient(stub, disposeHandler: false) { BaseAddress = new Uri(_options.BaseUrl) }, _options);

    private static IConfiguration Configuration(string? key) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["OpenWeather:ApiKey"] = key }).Build();
}
