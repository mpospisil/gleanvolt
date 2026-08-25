using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Infrastructure.OpenWeather;

/// <summary>
/// <see cref="IWeatherService"/> backed by OpenWeatherMap's current-weather endpoint
/// (<c>data/2.5/weather</c>).
///
/// <para><b>Why 2.5 and not One Call 3.0.</b> One Call needs its own "One Call by Call" subscription
/// and answers 401 without one, while this endpoint is on the standard free plan and returns every
/// figure a session records — temperature, pressure, humidity, cloud cover, visibility, the condition
/// and its description, and the day's sunrise and sunset. There is nothing to gain here from the
/// forecast horizons One Call adds: a session is recorded against what the sky <em>did</em>.</para>
///
/// <para><b>No cache, no worker.</b> Two calls per charging session, on the recording worker's own
/// loop. See <see cref="IWeatherService"/>.</para>
/// </summary>
public sealed class OpenWeatherMapService : IWeatherService
{
    /// <summary>Name of the configured <see cref="HttpClient"/> used to reach OpenWeatherMap.</summary>
    public const string HttpClientName = "OpenWeather";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WeatherOptions _options;
    private readonly PvSystemInfo _site;
    private readonly ILogger<OpenWeatherMapService> _logger;

    // A provider that is down is down for hours, and this is asked twice per session -- so the second
    // and later failures say nothing new. Reset on the next success, so a recovery is visible.
    private bool _failureLogged;

    /// <param name="site">
    /// The installation, for its coordinates. They come from here rather than from
    /// <see cref="WeatherOptions"/> because where the site is is a fact about the site, not about the
    /// weather provider — and because <c>Weather:Latitude</c> is deprecated in favour of
    /// <c>Pv:Latitude</c> (issue #111). The resolved site already carries whichever of the two won, so
    /// this service never has to know there were two.
    /// </param>
    public OpenWeatherMapService(
        IHttpClientFactory httpClientFactory,
        IOptions<WeatherOptions> options,
        PvSystemInfo site,
        ILogger<OpenWeatherMapService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _site = site;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey) && _site.HasLocation;

    public async Task<WeatherReading?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return null;
        }

        try
        {
            // The caller's token still cancels this; the timeout only bounds how long a healthy-looking
            // but slow provider may hold up a session's header write.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.RequestTimeout);

            var client = _httpClientFactory.CreateClient(HttpClientName);
            var response = await client
                .GetFromJsonAsync<CurrentWeatherResponse>(RequestUri(), timeout.Token)
                .ConfigureAwait(false);

            var reading = Map(response);
            if (reading is null)
            {
                Warn("OpenWeatherMap returned a response with no usable current conditions.", null);
                return null;
            }

            _failureLogged = false;
            return reading;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller is shutting down -- not a weather problem, and not worth a warning.
            return null;
        }
        catch (Exception ex)
        {
            // Every failure mode lands here on purpose: a bad key, a rate limit, a timeout, DNS, an
            // outage. None of them may reach the session recorder, which has a header to write.
            Warn("Could not fetch the current weather; the session will be recorded without it.", ex);
            return null;
        }
    }

    private string RequestUri()
    {
        var lat = _site.Latitude!.Value.ToString("0.######", CultureInfo.InvariantCulture);
        var lon = _site.Longitude!.Value.ToString("0.######", CultureInfo.InvariantCulture);

        return $"data/2.5/weather?lat={lat}&lon={lon}"
            + $"&units={Uri.EscapeDataString(_options.Units)}"
            + $"&appid={Uri.EscapeDataString(_options.ApiKey)}";
    }

    private void Warn(string message, Exception? ex)
    {
        if (_failureLogged)
        {
            return;
        }

        _failureLogged = true;

        if (ex is null)
        {
            _logger.LogWarning("{Message}", message);
        }
        else
        {
            _logger.LogWarning(ex, "{Message}", message);
        }
    }

    private static WeatherReading? Map(CurrentWeatherResponse? response)
    {
        if (response?.Main is not { } main)
        {
            return null;
        }

        var condition = response.Weather?.FirstOrDefault();

        var observation = new WeatherObservation(
            ObservedAt: DateTimeOffset.FromUnixTimeSeconds(response.Timestamp),
            TemperatureCelsius: main.Temperature,
            PressureHpa: main.Pressure,
            HumidityPercent: main.Humidity,
            CloudsPercent: response.Clouds?.All ?? 0,
            VisibilityMetres: response.Visibility,
            Condition: condition?.Main ?? string.Empty,
            ConditionDescription: condition?.Description ?? string.Empty);

        return new WeatherReading(
            observation,
            Instant(response.Sys?.Sunrise),
            Instant(response.Sys?.Sunset));
    }

    // The provider reports every instant as Unix seconds in UTC. Zero means "not reported" rather than
    // 1970, which for sunrise and sunset is a distinction worth keeping: a polar day has no sunrise.
    private static DateTimeOffset? Instant(long? unixSeconds) =>
        unixSeconds is > 0 ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value) : null;

    private sealed record CurrentWeatherResponse(
        [property: JsonPropertyName("dt")] long Timestamp,
        [property: JsonPropertyName("main")] MainBlock? Main,
        [property: JsonPropertyName("weather")] IReadOnlyList<ConditionBlock>? Weather,
        [property: JsonPropertyName("clouds")] CloudsBlock? Clouds,
        [property: JsonPropertyName("sys")] SysBlock? Sys,
        [property: JsonPropertyName("visibility")] double? Visibility = null);

    private sealed record MainBlock(
        [property: JsonPropertyName("temp")] double Temperature,
        [property: JsonPropertyName("pressure")] double Pressure,
        [property: JsonPropertyName("humidity")] double Humidity);

    private sealed record ConditionBlock(
        [property: JsonPropertyName("main")] string? Main,
        [property: JsonPropertyName("description")] string? Description);

    private sealed record CloudsBlock([property: JsonPropertyName("all")] double All);

    private sealed record SysBlock(
        [property: JsonPropertyName("sunrise")] long? Sunrise,
        [property: JsonPropertyName("sunset")] long? Sunset);
}
