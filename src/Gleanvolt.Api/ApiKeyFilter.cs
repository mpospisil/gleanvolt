using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Gleanvolt.Api;

/// <summary>
/// The API's whole authentication story: a bearer key from <see cref="ApiOptions.Keys"/>, checked on
/// every endpoint in the group before the handler runs.
///
/// <para>An endpoint filter rather than an authentication scheme, deliberately. The API must
/// work with the web UI switched off entirely, and the UI is what registers authentication and
/// authorization services at all — a scheme here would make one surface depend on the other being
/// present. It would also have to share <c>DefaultPolicy</c> with the UI's cookie, whose
/// "no password configured means no login" rule is exactly the rule this must not follow.</para>
/// </summary>
internal sealed class ApiKeyFilter : IEndpointFilter
{
    /// <summary>Where the matched key's name is left for handlers that report who asked.</summary>
    internal const string CallerItemKey = "Gleanvolt.Api.Caller";

    private readonly ApiOptions _options;
    private readonly ILogger _logger;

    internal ApiKeyFilter(ApiOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var presented = Presented(http);

        if (presented is null)
        {
            return Unauthorized(http, "No API key. Present one as 'Authorization: Bearer <key>'.");
        }

        var caller = Match(presented);
        if (caller is null)
        {
            // The path but not the key: a wrong key is either a misconfigured client or somebody
            // trying keys, and both are worth a line in a log an operator reads after the fact.
            _logger.LogWarning(
                "Rejected an API call to {Path} from {RemoteAddress}: the key is not one of the configured ones.",
                http.Request.Path,
                http.Connection.RemoteIpAddress);

            return Unauthorized(http, "That API key is not one of the configured ones.");
        }

        http.Items[CallerItemKey] = caller;

        return await next(context);
    }

    /// <summary>
    /// The key the caller presented, or null when there is nothing usable in the header. Only
    /// <c>Bearer</c> is accepted: a scheme this narrow cannot be got wrong quietly.
    /// </summary>
    private static string? Presented(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();

        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var value = header["Bearer ".Length..].Trim();

        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// The name of the key that matches, or null when none does.
    ///
    /// <para>Compared in fixed time and over every configured key, without an early exit on the first
    /// match: how long the answer takes must not depend on which characters were right, nor on how far
    /// down the list the right key sits.</para>
    /// </summary>
    private string? Match(string presented)
    {
        var offered = Encoding.UTF8.GetBytes(presented);
        string? matched = null;

        foreach (var (name, key) in _options.Keys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (CryptographicOperations.FixedTimeEquals(offered, Encoding.UTF8.GetBytes(key)))
            {
                matched = name;
            }
        }

        return matched;
    }

    private static IResult Unauthorized(HttpContext http, string detail)
    {
        http.Response.Headers.WWWAuthenticate = "Bearer";

        return Results.Problem(
            title: "Unauthorized",
            detail: detail,
            statusCode: StatusCodes.Status401Unauthorized);
    }
}

/// <summary>Reading back who is calling, for the handlers that have to report a source.</summary>
internal static class ApiCaller
{
    /// <summary>
    /// The source string to hand to <see cref="Core.Interfaces.IChargeActions"/> and the selectors,
    /// in the same shape the other surfaces use ("Web UI", "Home Assistant") — but naming the key, so
    /// a log line and a recorded session say <em>which</em> client started the charge.
    /// </summary>
    internal static string Source(this HttpContext http) =>
        http.Items.TryGetValue(ApiKeyFilter.CallerItemKey, out var caller) && caller is string name
            ? $"API ({name})"
            : "API";
}
