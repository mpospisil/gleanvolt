using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Providers.Contracts;
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
    /// The largest message a provider may send, 1 MiB: SignalR's default of 32 KB is less than a week
    /// of half-hour solar forecast periods (#388), and a message over the limit closes the connection
    /// rather than failing the call. 14 days of 5-minute periods with both bands fit. Only these hubs,
    /// which a token guards; the web UI's circuit keeps the default.
    /// </summary>
    public const long MaximumMessageBytes = 1024 * 1024;

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
        services.AddSignalR().AddHubOptions<THub>(options => options.MaximumReceiveMessageSize = MaximumMessageBytes);

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

    /// <summary>
    /// Maps <c>/external/{kind}/page</c> (#410): a redirect to the kind's provider page with a fresh ticket,
    /// the theme and the viewer's culture. The caller puts it behind the web UI's login; the ticket is
    /// what lets the provider trust the browser that arrives, so whoever can open this can use the page.
    /// 404 while no provider with a page is attached.
    /// </summary>
    public static RouteHandlerBuilder MapExternalProviderPages(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(ExternalProviderStatus.PagePathFor("{kind}"), (string kind, HttpContext context, ExternalProviderRegistry registry, TimeProvider time) =>
            registry.PageLink(kind, "auto", CultureInfo.CurrentUICulture.Name, time.GetUtcNow(), context.Request.Host.Host) is { } link
                ? Results.Redirect(link.AbsoluteUri)
                : Results.NotFound());

    /// <summary>
    /// Maps the pairing endpoints: a plugin with no token knocks, the owner accepts in the UI, and the plugin
    /// collects the token. Anonymous, since the plugin has no token yet; what stands in for one is the owner's
    /// accept, the secret only the plugin knows, a rate limit and a 4 KiB body limit. Nothing here may answer 500
    /// to what a stranger sends.
    /// </summary>
    public static IEndpointRouteBuilder MapProviderPairing(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(PairingPaths.Knock, async (HttpContext context, ExternalProviderRegistry registry) =>
        {
            var (knock, refusal) = await ReadBodyAsync<PairingKnock>(context.Request).ConfigureAwait(false);
            if (knock is null)
            {
                return refusal!;
            }

            if (!IsValid(knock) || registry.KindAtExactly(knock.HubPath) is not { } kind)
            {
                return Results.BadRequest();
            }

            // One plugin reaches us as ::ffff:a.b.c.d or a.b.c.d depending on the listener; it must be one
            // address, or it is a duplicate of itself and its rate-limit bucket is split.
            // An unknown address (a unix socket, some proxies) is not this machine: None is not loopback, so the
            // owner is still shown the code to check.
            var source = context.Connection.RemoteIpAddress ?? IPAddress.None;
            source = source.IsIPv4MappedToIPv6 ? source.MapToIPv4() : source;

            try
            {
                return registry.Requests.Knock(kind, knock, source) is { } entry
                    ? Results.Json(new PairingTicket(entry.Id), PairingJson)
                    : Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }
            catch (FormatException)
            {
                // A hash the pattern let through that does not decode: still the sender's mistake, not ours.
                return Results.BadRequest();
            }
        }).AllowAnonymous();

        endpoints.MapPost("/providers/pairing/{id}/collect", async (string id, HttpContext context, ExternalProviderRegistry registry) =>
        {
            var (collect, refusal) = await ReadBodyAsync<PairingCollect>(context.Request).ConfigureAwait(false);
            if (collect is null)
            {
                return refusal!;
            }

            return collect.Secret is { Length: > 0 and <= MaximumSecretLength }
                ? Results.Json(registry.Requests.Collect(id, collect.Secret), PairingJson)
                : Results.BadRequest();
        }).AllowAnonymous();

        return endpoints;
    }

    /// <summary>The most a pairing request body may be.</summary>
    private const int MaximumPairingBodyBytes = 4096;

    private const int MaximumSecretLength = 256;

    private static readonly JsonSerializerOptions PairingJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly System.Text.RegularExpressions.Regex ProviderIdPattern =
        new(@"^[a-z0-9-]{1,64}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // The base64url of a SHA-256: what PairingSecret.Code can read, and the only thing a plugin can send.
    private static readonly System.Text.RegularExpressions.Regex SecretHashPattern =
        new(@"^[A-Za-z0-9_-]{43}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static bool IsValid(PairingKnock knock) =>
        knock.HubPath is { Length: > 0 and <= 200 } hubPath && hubPath[0] == '/'
        && knock.ProviderId is { } id && ProviderIdPattern.IsMatch(id)
        && knock.Name is { Length: > 0 and <= 100 }
        && knock.Version is { Length: <= 64 }
        && (knock.PageUrl is null || (knock.PageUrl.IsAbsoluteUri && knock.PageUrl.Scheme is "http" or "https"))
        && knock.SecretHash is { } hash && SecretHashPattern.IsMatch(hash);

    /// <summary>
    /// Reads at most 4 KiB of JSON from the body. The size is counted as it is read, not taken from
    /// Content-Length, which a chunked request does not have.
    /// </summary>
    private static async Task<(T? Value, IResult? Refusal)> ReadBodyAsync<T>(HttpRequest request)
        where T : class
    {
        if (request.ContentLength > MaximumPairingBodyBytes)
        {
            return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
        }

        var buffer = new byte[MaximumPairingBodyBytes + 1];
        var length = 0;
        int read;
        while (length < buffer.Length
            && (read = await request.Body.ReadAsync(buffer.AsMemory(length)).ConfigureAwait(false)) > 0)
        {
            length += read;
        }

        if (length > MaximumPairingBodyBytes)
        {
            return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
        }

        try
        {
            return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, length), PairingJson) is { } value
                ? (value, null)
                : (null, Results.BadRequest());
        }
        catch (JsonException)
        {
            return (null, Results.BadRequest());
        }
    }

    private static void AddShared(IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(ExternalProviderRegistry)))
        {
            return;
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(provider => new PairingRequests(provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(provider => new ExternalProviderRegistry(
            provider,
            provider.GetRequiredService<ISecretStore>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILoggerFactory>(),
            provider.GetServices<ProviderKindRegistration>(),
            provider.GetRequiredService<PairingRequests>()));
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
