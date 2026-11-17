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
