using System.Net;
using Bunit;
using Gleanvolt.Plugin.Volkswagen.Volkswagen;
using Gleanvolt.Providers.Client;
using Gleanvolt.Providers.Client.Vehicle;
using Gleanvolt.Providers.Contracts.Vehicle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Gleanvolt.Web.Tests;
using VolkswagenHome = Gleanvolt.Plugin.Volkswagen.Components.Pages.Home;
using OpenWeatherHome = Gleanvolt.Plugin.OpenWeather.Components.Pages.Home;
using Gleanvolt.Plugin.OpenWeather.OpenWeather;
using Gleanvolt.Providers.Client.Weather;
using Gleanvolt.Providers.Contracts.Weather;
using Microsoft.Extensions.Configuration;

namespace Gleanvolt.Web.Screenshots;

/// <summary>
/// The Volkswagen plugin's own page (#410), rendered by bUnit as the plugin renders it and wrapped in its
/// own stylesheet: what Gleanvolt's Car page shows in its frame, for the manual. A plugin that has just
/// read the car: an account entered, a session saved, the car parked and plugged in.
/// </summary>
internal static class ProviderPageScreenshot
{
    public static string Volkswagen(DateTimeOffset now)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gleanvolt-screenshot-vw-{Guid.NewGuid():N}");

        try
        {
            using var context = new BunitContext();
            context.JSInterop.Mode = JSRuntimeMode.Loose;

            var options = new VolkswagenOptions { DataDirectory = directory };
            var files = new ProviderSecretStore(options.DataDirectory);
            var account = new AccountStore(files);
            account.Save("owner@example.com", "not-a-real-password", "WVGZZZE2ZPE000001");
            var session = new VwWebsiteSessionStore(files);
            session.Save(new CookieContainer());
            var time = new FixedTimeProvider(now, TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague"));
            var client = new VwWebsiteClient(options, account, session, time: time);
            var feed = new VolkswagenFeed(options, account, client, time, NullLogger<VolkswagenFeed>.Instance);
            feed.Seed(new VehicleReading(
                now.AddMinutes(-9),
                SocPercent: 64,
                RangeMetres: 268_000,
                ChargeTimeRemaining: TimeSpan.Zero,
                ChargeState: CarChargeState.Idle,
                PlugState: CarPlugState.Connected), now.AddMinutes(-2));
            var connection = new VehicleProviderConnection(
                Options.Create(new ProviderOptions { ProviderId = "volkswagen", Name = "Volkswagen" }),
                feed,
                NullLogger<VehicleProviderConnection>.Instance);

            context.Services.AddSingleton(account);
            context.Services.AddSingleton(session);
            context.Services.AddSingleton(new VwWebsiteSignIn(account, client));
            context.Services.AddSingleton(feed);
            context.Services.AddSingleton(new VolkswagenPoller(connection, feed, NullLogger<VolkswagenPoller>.Instance));
            context.Services.AddSingleton(connection);
            context.Services.AddSingleton<TimeProvider>(time);

            var markup = context.Render<VolkswagenHome>().Markup;

            // The data directory is a temporary one here: say where the real one is.
            markup = markup.Replace(Path.GetFullPath(directory), "/data", StringComparison.Ordinal);

            var css = File.ReadAllText(Path.Combine(RepositoryRoot(), "plugins", "Gleanvolt.Plugin.Volkswagen", "wwwroot", "app.css"));
            return $"""
                <!DOCTYPE html>
                <html lang="en" data-theme="light">
                <head><meta charset="utf-8" /><style>{css}</style></head>
                <body>{markup}</body>
                </html>
                """;
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// The OpenWeather plugin's own page (#424): a key saved on the page, the site from Gleanvolt, and a
    /// reading taken a few minutes ago.
    /// </summary>
    public static string OpenWeather(DateTimeOffset now)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gleanvolt-screenshot-owm-{Guid.NewGuid():N}");

        try
        {
            using var context = new BunitContext();
            context.JSInterop.Mode = JSRuntimeMode.Loose;

            var options = new OpenWeatherOptions { DataDirectory = directory };
            var key = new ApiKeyStore(new ProviderSecretStore(directory), new ConfigurationBuilder().Build());
            key.Save("3f9c2e41d8b6057a");
            var time = new FixedTimeProvider(now, TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague"));
            var observed = now.AddMinutes(-14);
            var reading = new WeatherData(
                observed,
                TemperatureCelsius: 23.4,
                PressurePascals: 101_600,
                HumidityPercent: 46,
                CloudsPercent: 20,
                VisibilityMetres: 10_000,
                Condition: "Clouds",
                ConditionDescription: "few clouds",
                Sunrise: new DateTimeOffset(now.Date.AddHours(4).AddMinutes(51), now.Offset),
                Sunset: new DateTimeOffset(now.Date.AddHours(21).AddMinutes(10), now.Offset));
            var client = new OpenWeatherClient(new HttpClient(new Answers(reading)) { BaseAddress = new Uri(options.BaseUrl) }, options);
            var feed = new OpenWeatherFeed(client, key, options, time, NullLogger<OpenWeatherFeed>.Instance);
            var site = new WeatherSite(50.0755, 14.4378, "Europe/Prague");
            feed.GetReadingAsync(site, CancellationToken.None).GetAwaiter().GetResult();

            var connection = new WeatherProviderConnection(
                Options.Create(new ProviderOptions { ProviderId = "openweather", Name = "OpenWeather" }),
                feed,
                NullLogger<WeatherProviderConnection>.Instance);

            context.Services.AddSingleton(key);
            context.Services.AddSingleton(client);
            context.Services.AddSingleton(feed);
            context.Services.AddSingleton(connection);
            context.Services.AddSingleton<TimeProvider>(time);

            var markup = context.Render<OpenWeatherHome>().Markup;

            // Not connected in a picture: show the site Gleanvolt would have given it.
            markup = markup.Replace(
                $">{Gleanvolt.Plugin.OpenWeather.OpenWeather.PageText.NotConnected}<", ">50.0755, 14.4378<", StringComparison.Ordinal);

            var css = File.ReadAllText(Path.Combine(RepositoryRoot(), "plugins", "Gleanvolt.Plugin.OpenWeather", "wwwroot", "app.css"));
            return $"""
                <!DOCTYPE html>
                <html lang="en" data-theme="light">
                <head><meta charset="utf-8" /><style>{css}</style></head>
                <body>{markup}</body>
                </html>
                """;
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    // OpenWeatherMap's answer for the picture: the reading, in its own JSON.
    private sealed class Answers(WeatherData reading) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                weather = new[] { new { main = reading.Condition, description = reading.ConditionDescription } },
                main = new { temp = reading.TemperatureCelsius, pressure = reading.PressurePascals / 100, humidity = reading.HumidityPercent },
                visibility = reading.VisibilityMetres,
                clouds = new { all = reading.CloudsPercent },
                dt = reading.ObservedAt.ToUnixTimeSeconds(),
                sys = new { sunrise = reading.Sunrise!.Value.ToUnixTimeSeconds(), sunset = reading.Sunset!.Value.ToUnixTimeSeconds() },
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Gleanvolt.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Gleanvolt.slnx not found above " + AppContext.BaseDirectory);
    }
}
