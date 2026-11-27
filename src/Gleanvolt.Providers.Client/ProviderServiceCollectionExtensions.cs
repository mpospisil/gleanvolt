using Gleanvolt.Providers.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Gleanvolt.Providers.Client;

/// <summary>Registration for a kind without a ready-made extension, such as a test kind.</summary>
public static class ProviderServiceCollectionExtensions
{
    /// <summary>Runs <typeparamref name="TConnection"/> as a hosted service, and as itself for publishing.</summary>
    public static IServiceCollection AddGleanvoltProvider<TConnection, TWelcome>(this IServiceCollection services, Action<ProviderOptions> configure)
        where TConnection : ProviderConnection<TWelcome>
        where TWelcome : Welcome
    {
        services.Configure(configure);
        services.TryAddSingleton<ProviderCredentials>();
        services.AddSingleton<TConnection>();
        services.AddHostedService(provider => provider.GetRequiredService<TConnection>());
        services.AddGleanvoltPairing(provider => provider.GetRequiredService<TConnection>().HubPath);
        return services;
    }
}
