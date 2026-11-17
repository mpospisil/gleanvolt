using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gleanvolt.Providers.Contracts.Weather;

namespace Gleanvolt.Plugin.OpenWeather.OpenWeather;

/// <summary>What OpenWeatherMap made of a key.</summary>
public enum KeyCheck
{
    /// <summary>It answered with the weather.</summary>
    Accepted,

    /// <summary>It answered 401: a wrong key, or one created less than a couple of hours ago.</summary>
    Refused,

    /// <summary>It could not be asked, or answered something else.</summary>
    Failed,
}

/// <summary>Why a read failed, as the owner needs to know it.</summary>
public enum ReadFailure
{
    /// <summary>The key was refused (401).</summary>
    Refused,

    /// <summary>Too many calls (429).</summary>
    RateLimited,

    /// <summary>No answer, a timeout, a server error.</summary>
    Unreachable,

    /// <summary>An answer the plugin can't read.</summary>
    ServiceChanged,
}

/// <summary>A read that failed, and why.</summary>
public sealed class ReadFailedException(ReadFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public ReadFailure Failure { get; } = failure;
}

/// <summary>
/// OpenWeatherMap's current-weather endpoint (<c>data/2.5/weather</c>), copied from Gleanvolt's built-in
/// <c>OpenWeatherMapService</c> (#424). The same endpoint, the same free plan and the same mapping, to
/// the contract's <see cref="WeatherData"/> instead of Gleanvolt's <c>WeatherReading</c>.
///
/// <para>Unlike the built-in it throws: the feed above it turns a failure into a status Gleanvolt shows
/// the owner, which the built-in could only log.</para>
/// </summary>
public sealed class OpenWeatherClient(HttpClient http, OpenWeatherOptions options)
{
    /// <summary>The current conditions at the coordinates.</summary>
    /// <exception cref="ReadFailedException">For every failure: the reason is its <see cref="ReadFailedException.Failure"/>.</exception>
    public async Task<WeatherData> GetCurrentAsync(string apiKey, double latitude, double longitude, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);

        HttpResponseMessage response;

        try
        {
            response = await http.GetAsync(RequestUri(apiKey, latitude, longitude), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new ReadFailedException(ReadFailure.Unreachable, $"OpenWeatherMap could not be reached ({ex.Message}).", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new ReadFailedException(ReadFailure.Refused, "OpenWeatherMap refused the key (401).");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new ReadFailedException(ReadFailure.RateLimited, "OpenWeatherMap is limiting calls (429).");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ReadFailedException(ReadFailure.Unreachable, $"OpenWeatherMap answered {(int)response.StatusCode}.");
            }

            CurrentWeatherResponse? body;

            try
            {
                body = await response.Content.ReadFromJsonAsync<CurrentWeatherResponse>(timeout.Token).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new ReadFailedException(ReadFailure.ServiceChanged, "OpenWeatherMap's answer could not be read.", ex);
            }

            return Map(body) ?? throw new ReadFailedException(
                ReadFailure.ServiceChanged, "OpenWeatherMap answered with no usable current conditions.");
        }
    }

    /// <summary>
    /// Asks once with <paramref name="apiKey"/>, to say on the page whether it works. At the site when
    /// Gleanvolt said where it is; anywhere answers for a key otherwise.
    /// </summary>
    public async Task<KeyCheck> CheckKeyAsync(string apiKey, double? latitude, double? longitude, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RequestTimeout);
            using var response = await http.GetAsync(RequestUri(apiKey.Trim(), latitude ?? 0, longitude ?? 0), timeout.Token).ConfigureAwait(false);

            return response.IsSuccessStatusCode ? KeyCheck.Accepted
                : response.StatusCode == HttpStatusCode.Unauthorized ? KeyCheck.Refused
                : KeyCheck.Failed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return KeyCheck.Failed;
        }
    }

    // Metric always: the contract is in °C, and the built-in's Units option has nothing to choose here.
    private static string RequestUri(string apiKey, double latitude, double longitude)
    {
        var lat = latitude.ToString("0.######", CultureInfo.InvariantCulture);
        var lon = longitude.ToString("0.######", CultureInfo.InvariantCulture);

        return $"data/2.5/weather?lat={lat}&lon={lon}&units=metric&appid={Uri.EscapeDataString(apiKey)}";
    }

    /// <summary>The answer as the contract's reading, or null when it has no current conditions.</summary>
    internal static WeatherData? Map(CurrentWeatherResponse? response)
    {
        if (response?.Main is not { } main)
        {
            return null;
        }

        var condition = response.Weather?.FirstOrDefault();

        return new WeatherData(
            ObservedAt: DateTimeOffset.FromUnixTimeSeconds(response.Timestamp),
            TemperatureCelsius: main.Temperature,
            PressurePascals: main.Pressure * 100, // OpenWeather reports hPa
            HumidityPercent: main.Humidity,
            CloudsPercent: response.Clouds?.All,
            VisibilityMetres: response.Visibility,
            Condition: condition?.Main,
            ConditionDescription: condition?.Description,
            Sunrise: Instant(response.Sys?.Sunrise),
            Sunset: Instant(response.Sys?.Sunset));
    }

    // Every instant comes as Unix seconds in UTC. Zero means "not reported" rather than 1970: a polar
    // day has no sunrise.
    private static DateTimeOffset? Instant(long? unixSeconds) =>
        unixSeconds is > 0 ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value) : null;

    internal sealed record CurrentWeatherResponse(
        [property: JsonPropertyName("dt")] long Timestamp,
        [property: JsonPropertyName("main")] MainBlock? Main,
        [property: JsonPropertyName("weather")] IReadOnlyList<ConditionBlock>? Weather,
        [property: JsonPropertyName("clouds")] CloudsBlock? Clouds,
        [property: JsonPropertyName("sys")] SysBlock? Sys,
        [property: JsonPropertyName("visibility")] double? Visibility = null);

    internal sealed record MainBlock(
        [property: JsonPropertyName("temp")] double Temperature,
        [property: JsonPropertyName("pressure")] double Pressure,
        [property: JsonPropertyName("humidity")] double Humidity);

    internal sealed record ConditionBlock(
        [property: JsonPropertyName("main")] string? Main,
        [property: JsonPropertyName("description")] string? Description);

    internal sealed record CloudsBlock([property: JsonPropertyName("all")] double All);

    internal sealed record SysBlock(
        [property: JsonPropertyName("sunrise")] long? Sunrise,
        [property: JsonPropertyName("sunset")] long? Sunset);
}
