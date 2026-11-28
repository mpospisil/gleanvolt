using Gleanvolt.Providers.Client.Charger;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Gleanvolt.Chargers;

/// <summary>
/// The host every charger plugin runs in: the shared plugin host (<see cref="PluginWebHost"/>: configuration, the page
/// Gleanvolt frames, pairing, the data directory) with the charger's own parts on it: the charger provider, the status
/// reporter, the session's limits and the recording for testers.
/// </summary>
public static class PluginHost
{
    /// <summary>
    /// A builder for the plugin <paramref name="plugin"/> whose charger is <typeparamref name="TCharger"/>. The plugin
    /// adds its own services (its transport, its search) before <see cref="Build"/>.
    /// </summary>
    public static WebApplicationBuilder CreateBuilder<TCharger>(string[] args, PluginInfo plugin)
        where TCharger : PluginCharger
    {
        var builder = PluginWebHost.CreateBuilder(args, plugin,
            (services, configure) => services.AddGleanvoltChargerProvider<TCharger>(configure));

        builder.Services.AddSingleton<DeviceRecorder>();
        builder.Services.AddSingleton<GleanvoltSession>();
        builder.Services.AddTransient<RecordingHandler>();

        // The charger the SDK commands is the one the page shows.
        builder.Services.AddSingleton(provider => (TCharger)provider.GetRequiredService<IChargerSource>());
        builder.Services.AddSingleton<PluginCharger>(provider => provider.GetRequiredService<TCharger>());
        builder.Services.AddHostedService<StatusReporter>();

        return builder;
    }

    /// <summary>The plugin's application: the page, guarded by Gleanvolt's ticket, and the recording's download.</summary>
    public static WebApplication Build(WebApplicationBuilder builder) =>
        PluginWebHost.Build(builder, app =>
        {
            var plugin = app.Services.GetRequiredService<PluginInfo>();

            // No extension in the path: the page guard admits a GET with one without the ticket, as a static file.
            app.MapGet("/recording", (DeviceRecorder recorder, PluginSettings settings) =>
                Results.File(
                    System.Text.Encoding.UTF8.GetBytes(recorder.ToJson(plugin.ProviderId, plugin.Version, new { address = settings.Address.Current })),
                    "application/json",
                    $"{plugin.ProviderId}-recording.json"));
        });
}
