using Gleanvolt.Providers.Client.Vehicle;
using Gleanvolt.Providers.Contracts.Vehicle;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gleanvolt.Cars;

/// <summary>
/// The host every car plugin runs in: the shared plugin host (<see cref="PluginWebHost"/>: configuration, the page
/// Gleanvolt frames, pairing, the data directory) with the car's own parts on it: the vehicle provider, the car's
/// clock and its options, and the status reporter.
/// </summary>
public static class CarPluginHost
{
    /// <summary>
    /// A builder for the plugin <paramref name="plugin"/> whose car is <typeparamref name="TCar"/>. The plugin adds its
    /// own services (its client for the maker, its account) before <see cref="Build"/>.
    /// </summary>
    public static WebApplicationBuilder CreateBuilder<TCar>(string[] args, PluginInfo plugin)
        where TCar : PluginCar
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var builder = PluginWebHost.CreateBuilder(args, plugin,
            (services, configure) => services.AddGleanvoltVehicleProvider<TCar>(configure));

        builder.Services.AddSingleton(builder.Configuration.GetSection(plugin.Section).Get<CarPollOptions>() ?? new CarPollOptions());

        // The car the SDK asks is the one the page shows and the clock reads.
        builder.Services.AddSingleton(provider => (TCar)provider.GetRequiredService<IVehicleSource>());
        builder.Services.AddSingleton<PluginCar>(provider => provider.GetRequiredService<TCar>());
        builder.Services.AddSingleton<CarPoller>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<CarPoller>());
        builder.Services.AddHostedService(provider => new PluginStatusReporter<VehicleWelcome>(
            provider.GetRequiredService<VehicleProviderConnection>(),
            provider.GetRequiredService<PluginCar>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger<PluginStatusReporter<VehicleWelcome>>()));

        return builder;
    }

    /// <summary>The plugin's application: the page, guarded by Gleanvolt's ticket.</summary>
    public static WebApplication Build(WebApplicationBuilder builder) => PluginWebHost.Build(builder);
}
