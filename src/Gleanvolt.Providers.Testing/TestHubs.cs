using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gleanvolt.Providers.Client;
using Gleanvolt.Providers.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gleanvolt.Providers.Testing;

/// <summary>
/// What every kind's test hub does as Gleanvolt does: a loopback web server, the token checked on every request
/// but pairing's, SignalR with Gleanvolt's message size and enum names, and pairing's two endpoints.
/// </summary>
internal static class TestHubs
{
    /// <summary>The largest message a hub takes, as Gleanvolt: 1 MiB.</summary>
    public const long MaximumMessageBytes = 1024 * 1024;

    /// <summary>A builder on a free loopback port, with SignalR set up as Gleanvolt's.</summary>
    public static WebApplicationBuilder CreateBuilder()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR(hub => hub.MaximumReceiveMessageSize = MaximumMessageBytes)
            .AddJsonProtocol(json => json.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
        return builder;
    }

    /// <summary>A random token, or the one the test chose.</summary>
    public static string Token(string? chosen) => chosen ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// The token check. The pairing paths pass it whether or not they are mapped: unmapped, they answer 404 as an older
    /// Gleanvolt does, not 401.
    /// </summary>
    public static void UseToken(WebApplication app, string token) =>
        app.Use((context, next) => Authorised(context.Request, token) || IsPairing(context.Request)
            ? next(context)
            : Refuse(context.Response));

    /// <summary>The address the started app listens at.</summary>
    public static Uri Address(WebApplication app) =>
        new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());

    /// <summary>
    /// Gleanvolt's two pairing endpoints, without its waiting list: every knock is accepted at once. The secret is still
    /// checked against the hash the plugin knocked with, so a plugin that collects with the wrong one is found out.
    /// </summary>
    public static void MapPairing(WebApplication app, Func<PairingDesk> desk)
    {
        app.MapPost(PairingPaths.Knock, async (HttpContext context) =>
            await ReadAsync<PairingKnock>(context.Request).ConfigureAwait(false) is { } knock
                ? Results.Json(desk().Knocked(knock), PairingJson)
                : Results.BadRequest());

        app.MapPost("/providers/pairing/{id}/collect", async (string id, HttpContext context) =>
            await ReadAsync<PairingCollect>(context.Request).ConfigureAwait(false) is { } collect
                ? Results.Json(desk().Collected(id, collect.Secret), PairingJson)
                : Results.BadRequest());
    }

    private static async Task<T?> ReadAsync<T>(HttpRequest request)
        where T : class
    {
        try
        {
            return await request.ReadFromJsonAsync<T>(PairingJson).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsPairing(HttpRequest request) =>
        request.Path.StartsWithSegments("/providers/pairing", StringComparison.Ordinal);

    private static readonly JsonSerializerOptions PairingJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // As Gleanvolt: the token in the Authorization header, or as access_token for a WebSocket that can't set one.
    private static bool Authorised(HttpRequest request, string token)
    {
        var header = request.Headers.Authorization.ToString();
        var offered = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : request.Query["access_token"].ToString();

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(offered), Encoding.UTF8.GetBytes(token));
    }

    private static Task Refuse(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}

/// <summary>A test hub's pairing: the knocks it was sent, and the token it hands over to a collect that proves the secret.</summary>
internal sealed class PairingDesk(string token)
{
    private readonly Lock _gate = new();
    private readonly List<PairingKnock> _knocks = [];
    private readonly Dictionary<string, string> _hashes = new(StringComparer.Ordinal);

    public IReadOnlyList<PairingKnock> Knocks
    {
        get
        {
            lock (_gate)
            {
                return [.. _knocks];
            }
        }
    }

    public PairingTicket Knocked(PairingKnock knock)
    {
        var id = Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            _knocks.Add(knock);
            _hashes[id] = knock.SecretHash;
        }

        return new PairingTicket(id);
    }

    public PairingAnswer Collected(string id, string? secret)
    {
        lock (_gate)
        {
            // As Gleanvolt: a collect that does not prove the secret learns nothing, not even that the request exists.
            return secret is not null && _hashes.TryGetValue(id, out var hash) && hash == PairingSecret.Hash(secret)
                ? new PairingAnswer(PairingState.Accepted, token)
                : new PairingAnswer(PairingState.Unknown);
        }
    }
}
