using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Tariff.External;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Weather;
using Gleanvolt.Providers.Server;

namespace Gleanvolt.Hosting.Weather;

/// <summary>
/// The weather's hub (#424): a provider publishes the conditions at the site, and once they pass
/// <see cref="WeatherReadingValidator"/> <see cref="ExternalWeatherService"/> holds them for the next
/// charging session that opens or closes. Nothing here waits for the provider.
///
/// <para>On while <see cref="WeatherSource.External"/> is the selected weather source: the selection is
/// the switch, and it applies without a restart.</para>
/// </summary>
public sealed class WeatherHub(
    ExternalProviderRegistry registry,
    ProviderHostInfo host,
    ExternalWeatherService weather,
    PvSystemInfo site,
    TimeProvider time)
    : ProviderHub<IWeatherClient, WeatherWelcome>(registry, host), IWeatherHub
{
    /// <summary>The kind, as registered.</summary>
    public static ProviderKind Kind { get; } = new(
        WeatherContract.Kind,
        "Weather",
        WeatherContract.Major,
        WeatherContract.Minor,
        WeatherContract.HubPath,
        services => services.GetRequiredService<IWeatherSourceSelection>().Selected == WeatherSource.External);

    /// <summary>The site as a provider is told it.</summary>
    public static WeatherSite Site(PvSystemInfo site, TimeZoneInfo zone) =>
        new(site.Latitude, site.Longitude, TariffHub.IanaId(zone));

    protected override WeatherWelcome CreateWelcome() => new() { Site = Site(site, time.LocalTimeZone) };

    public Task<PublishResult> Publish(WeatherData data)
    {
        RequireAttached();

        if (data is null)
        {
            var empty = new ConfigurationProblems("The reading is empty.", Phrase.Of("Weather.Empty"));
            Sessions.DataRefused(null, empty, empty.Phrases);
            return Task.FromResult(PublishResult.Refused([.. empty]));
        }

        var reading = ToReading(data);
        var problems = WeatherReadingValidator.Check(reading, weather.Current, time.GetUtcNow());

        if (problems.Count > 0)
        {
            Sessions.DataRefused(data.InReplyTo, problems, problems.Phrases);
            weather.Refused(data.InReplyTo);
            return Task.FromResult(PublishResult.Refused([.. problems]));
        }

        weather.Accepted(data.InReplyTo, reading);
        Sessions.DataAccepted(data.InReplyTo);
        return Task.FromResult(PublishResult.Ok);
    }

    /// <summary>A published reading as Gleanvolt records it on a session.</summary>
    internal static WeatherReading ToReading(WeatherData data) => new(
        new WeatherObservation(
            data.ObservedAt,
            data.TemperatureCelsius,
            data.PressurePascals,
            data.HumidityPercent,
            data.CloudsPercent,
            data.VisibilityMetres,
            data.Condition?.Trim() ?? string.Empty,
            data.ConditionDescription?.Trim() ?? string.Empty),
        data.Sunrise,
        data.Sunset);
}
