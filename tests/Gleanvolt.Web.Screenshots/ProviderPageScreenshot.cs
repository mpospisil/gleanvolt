using System.Net;
using Bunit;
using System.Text.RegularExpressions;
using Gleanvolt.Cars;
using Gleanvolt.Cars.Components;
using Microsoft.AspNetCore.Components;
using Gleanvolt.Providers.Client;
using Gleanvolt.Providers.Client.Vehicle;
using Gleanvolt.Providers.Contracts.Vehicle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Gleanvolt.Web.Tests;
using OpenWeatherHome = Gleanvolt.Plugin.OpenWeather.Components.Pages.Home;
using Gleanvolt.Plugin.OpenWeather.OpenWeather;
using Gleanvolt.Providers.Client.Weather;
using Gleanvolt.Providers.Contracts.Weather;
using Microsoft.Extensions.Configuration;

namespace Gleanvolt.Web.Screenshots;

/// <summary>
/// The plugins' own pages, rendered by bUnit as the plugins render them and wrapped in their stylesheet: what
/// Gleanvolt's Settings and Car pages show in their frames, for the manual.
/// </summary>
internal static class ProviderPageScreenshot
{
    /// <summary>
    /// A car plugin's own page (#410, #474): the car layer's shared page, as the Volkswagen plugin in gleanvolt-cars
    /// shows it, with a reading taken two minutes ago and a sign-in section of the plugin's own. The plugin itself is
    /// not in this repository, so its section is drawn here in its shape.
    /// </summary>
    public static string CarPlugin(DateTimeOffset now)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gleanvolt-screenshot-car-{Guid.NewGuid():N}");

        try
        {
            using var context = new BunitContext();
            context.JSInterop.Mode = JSRuntimeMode.Loose;

            var time = new FixedTimeProvider(now, TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague"));
            var car = new PictureCar(time, new VehicleReading(
                now.AddMinutes(-9),
                SocPercent: 64,
                RangeMetres: 268_000,
                ChargeTimeRemaining: TimeSpan.Zero,
                ChargeState: CarChargeState.Idle,
                PlugState: CarPlugState.Connected));
            car.ReadAsync(null, CancellationToken.None).GetAwaiter().GetResult();

            var options = Options.Create(new ProviderOptions { ProviderId = "volkswagen", Name = "Volkswagen" });
            var connection = new VehicleProviderConnection(options, car, NullLogger<VehicleProviderConnection>.Instance);

            context.Services.AddSingleton(new Gleanvolt.Plugins.PluginInfo("volkswagen", "Volkswagen", "Volkswagen", 8095));
            context.Services.AddSingleton<PluginCar>(car);
            context.Services.AddSingleton(new CarPoller(connection, car, time, NullLogger<CarPoller>.Instance));
            context.Services.AddSingleton(connection);
            // Accepted on Settings → Plugins, as an owner pairs it.
            var credentials = new ProviderCredentials(options, new ProviderSecretStore(directory));
            credentials.Save(new Uri("http://192.168.2.7:8090"), "gvp_picture");
            context.Services.AddSingleton(credentials);
            context.Services.AddSingleton<TimeProvider>(time);

            var markup = context.Render<CarFrame>(parameters => parameters
                .Add(frame => frame.Settings, (RenderFragment)(builder => builder.AddMarkupContent(0, SignInSection)))).Markup;

            // Not connected in a picture: show the car Gleanvolt would have welcomed it with.
            markup = Regex.Replace(
                markup,
                "<p class=\"muted\" id=\"not-connected\">[^<]*</p>",
                $"<dl id=\"car\"><dt>{CarText.GleanvoltCar.En}</dt><dd>Rosie (Volkswagen ID.4)</dd></dl><p id=\"session\" class=\"muted\">{CarText.SessionClosed.En}</p>");

            var css = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Gleanvolt.Plugins.Hosting", "wwwroot", "app.css"));
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

    // The Volkswagen plugin's sign-in and account sections, in their shape and English words, signed in.
    private const string SignInSection = """
        <section>
            <h2>Sign in to volkswagen.de</h2>
            <p class="muted">A cold sign-in always makes Volkswagen email a one-time code. Once you have entered it, this browser is remembered, so a restart of the plugin should not need another.</p>
            <p id="signin-state" class="ok">Signed in. This browser is remembered, so a restart should not need another code.</p>
            <div class="actions">
                <button type="button" id="signin">Sign in</button>
                <button type="button" id="forget" class="secondary">Forget the session</button>
            </div>
            <p class="muted">A signed-in session is saved. Kept as files in /data, readable only by the plugin's user (0600).</p>
        </section>
        <section>
            <h2>The account</h2>
            <label for="username">VW ID (email)</label>
            <input id="username" type="email" value="owner@example.com" />
            <label for="password">Password</label>
            <input id="password" type="password" placeholder="Saved. Leave empty to keep it." />
            <label for="vin">VIN</label>
            <input id="vin" value="WVGZZZE2ZPE000001" />
            <div class="actions"><button type="button" id="account-save" class="secondary">Save the account</button></div>
        </section>
        """;

    // A car that has just been read, for the picture.
    private sealed class PictureCar(TimeProvider time, VehicleReading reading) : PluginCar(new CarPollOptions(), time)
    {
        protected override Task<VehicleReading?> FetchAsync(VehicleInfo? car, CancellationToken cancellationToken) =>
            Task.FromResult<VehicleReading?>(reading);
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
