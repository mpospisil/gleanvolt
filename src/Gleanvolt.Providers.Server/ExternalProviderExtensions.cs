using System.Text.Json;
using System.Text.Json.Serialization;
using Gleanvolt.Core.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Gleanvolt.Providers.Server;

/// <summary>Registers and maps the external provider hubs (#386).</summary>
public static class ExternalProviderExtensions
{
    /// <summary>The authorization policy every provider hub requires.</summary>
    public const string Policy = "GleanvoltProvider";

    /// <summary>Where every hub is: <c>/providers/&lt;kind&gt;</c>.</summary>
    public const string PathPrefix = "/providers";

    /// <summary>
    /// Registers <typeparamref name="THub"/> as <paramref name="kind"/>'s hub. The first call also
    /// registers what every kind shares: the registry, the token scheme and the policy.
    /// </summary>
    public static IServiceCollection AddExternalProvider<THub>(this IServiceCollection services, ProviderKind kind)
        where THub : Hub
    {
        ArgumentNullException.ThrowIfNull(kind);
        AddShared(services);

        services.AddSingleton(new ProviderKindRegistration(
            kind,
            typeof(THub),
            (provider, connectionId) => provider.GetRequiredService<IHubContext<THub>>().Clients.Client(connectionId)));
        services.AddSingleton(new HubMapping(kind.HubPath, endpoints => endpoints.MapHub<THub>(kind.HubPath)));

        return services;
    }

    /// <summary>Maps every registered kind's hub. Nothing when none is registered.</summary>
    public static IEndpointRouteBuilder MapExternalProviders(this IEndpointRouteBuilder endpoints)
    {
        var mappings = endpoints.ServiceProvider.GetServices<HubMapping>().ToList();

        foreach (var mapping in mappings)
        {
            mapping.Map(endpoints).RequireAuthorization(Policy);
        }

        if (mappings.Count > 0)
        {
            endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Gleanvolt.Providers").LogInformation(
                "External providers can connect at {Paths}.", string.Join(", ", mappings.Select(mapping => mapping.Path)));
        }

        return endpoints;
    }

    private static void AddShared(IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(ExternalProviderRegistry)))
        {
            return;
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(provider => new ExternalProviderRegistry(
            provider,
            provider.GetRequiredService<ISecretStore>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILoggerFactory>(),
            provider.GetServices<ProviderKindRegistration>()));
        services.AddSingleton<IExternalProviders>(provider => provider.GetRequiredService<ExternalProviderRegistry>());

        // Its own scheme, not the default: the web UI's cookie stays the default for everything else.
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ProviderTokenAuthentication>(ProviderTokenAuthentication.SchemeName, null);
        services.AddAuthorizationBuilder().AddPolicy(Policy, policy => policy
            .AddAuthenticationSchemes(ProviderTokenAuthentication.SchemeName)
            .RequireClaim(ProviderTokenAuthentication.KindClaim));

        // Enums by name, so a provider in another language sends "low" rather than 0. Numbers are still
        // read, for a client that sends them.
        services.AddSignalR().AddJsonProtocol(options =>
            options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
    }

    private sealed record HubMapping(string Path, Func<IEndpointRouteBuilder, HubEndpointConventionBuilder> Map);
}
