using Gleanvolt.Providers.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gleanvolt.Providers.Client;

/// <summary>How the provider's own page is guarded (#410).</summary>
public sealed class ProviderPageOptions
{
    /// <summary>
    /// How long the browser stays admitted after Gleanvolt opened the page with a ticket. The page is
    /// then reachable without a ticket until it runs out, so a reload in the frame works.
    /// </summary>
    public TimeSpan CookieLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>
    /// Paths answered without a ticket. By default the static files and Blazor's framework and circuit,
    /// which carry nothing of the owner's: an interactive component can only be started from markers
    /// the server rendered into a page, and that page needed the ticket. See docs/PROVIDERS.md.
    /// </summary>
    public Func<HttpContext, bool> IsPublic { get; set; } = DefaultIsPublic;

    /// <summary>
    /// Answers <c>GET /</c> while the provider has no token (#459), when the page is open to the whole network
    /// and nothing may be shown but that the provider waits to be paired. Null writes a plain text with the
    /// provider's name, the phase and the code the owner compares in Gleanvolt. Every other path that is not
    /// public is refused as usual, so there is nothing to press.
    /// </summary>
    public RequestDelegate? Unpaired { get; set; }

    /// <summary>The default for <see cref="IsPublic"/>.</summary>
    public static bool DefaultIsPublic(HttpContext context)
    {
        var path = context.Request.Path;
        return path.StartsWithSegments("/_framework")
            || path.StartsWithSegments("/_blazor")
            || path.StartsWithSegments("/_content")
            || (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
                && Path.HasExtension(path.Value);
    }
}

/// <summary>
/// Admits only a browser Gleanvolt sent (#410): a request with a valid ticket
/// (<see cref="ProviderPage.TicketParameter"/>) is let through and given the provider's own cookie; a
/// request with that cookie is let through; anything else is answered 403. The page needs no login of
/// its own, and nobody on the network can press its buttons without the owner's Gleanvolt. Until the provider
/// has a token (#459) there is nothing to sign with, so nobody is admitted and <c>GET /</c> shows only
/// <see cref="ProviderPageOptions.Unpaired"/>.
/// </summary>
internal sealed class ProviderPageMiddleware(
    RequestDelegate next,
    IOptions<ProviderOptions> provider,
    IOptions<ProviderPageOptions> page,
    ProviderCredentials credentials,
    ILogger<ProviderPageMiddleware> logger,
    TimeProvider? time = null)
{
    private const string CookiePurpose = "gleanvolt-page-cookie/v1";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The cookie's name, per provider, so two providers on one host don't overwrite each other.</summary>
    internal static string CookieName(string providerId) => "gleanvolt-page-" + providerId;

    public Task InvokeAsync(HttpContext context)
    {
        if (page.Value.IsPublic(context))
        {
            return next(context);
        }

        var options = provider.Value;

        // Read per request: the token arrives or goes while the provider runs. Without one nothing may be
        // signed or verified, an empty key would admit anyone who signs with it.
        if (credentials.Token is not { } token)
        {
            return IsRoot(context.Request) ? (page.Value.Unpaired ?? WriteWaitingView)(context) : Refuse(context);
        }

        var now = _time.GetUtcNow();
        var cookieName = CookieName(options.ProviderId);

        if (context.Request.Query[ProviderPage.TicketParameter].ToString() is { Length: > 0 } ticket
            && ProviderPage.VerifyTicket(ticket, token, options.ProviderId, now))
        {
            var expires = now + page.Value.CookieLifetime;
            context.Response.Cookies.Append(cookieName, ProviderPage.Sign(CookiePurpose, token, options.ProviderId, expires), new CookieOptions
            {
                HttpOnly = true,
                Path = "/",
                Expires = expires,
                IsEssential = true,

                // In Gleanvolt's frame the page is a third party when the two are on different hosts. Only
                // over https can the cookie be sent there; over http it still works on the same host.
                Secure = context.Request.IsHttps,
                SameSite = context.Request.IsHttps ? SameSiteMode.None : SameSiteMode.Lax,
            });

            return next(context);
        }

        if (ProviderPage.Verify(CookiePurpose, context.Request.Cookies[cookieName], token, options.ProviderId, now))
        {
            return next(context);
        }

        return Refuse(context);
    }

    private static bool IsRoot(HttpRequest request) =>
        HttpMethods.IsGet(request.Method) && (request.Path.Value is null or "" or "/");

    private Task Refuse(HttpContext context)
    {
        logger.LogInformation("Refused {Path}: no valid ticket from Gleanvolt. Open the page from Gleanvolt.", context.Request.Path);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync("Open this page from Gleanvolt: the Car page, or Settings → Plugins.");
    }

    // The default waiting view. The pairing is optional: a host without it only learns that it is waiting.
    private Task WriteWaitingView(HttpContext context)
    {
        var name = provider.Value.Name is { Length: > 0 } n ? n : provider.Value.ProviderId;
        var text = $"{name} is not paired with Gleanvolt yet.";

        if (context.RequestServices.GetService<ProviderPairing>()?.Status is { } status)
        {
            text += $"{Environment.NewLine}Phase: {status.Phase}{Environment.NewLine}Code: {status.Code}"
                + $"{Environment.NewLine}Accept this plugin in Gleanvolt, under Settings → Plugins, if the code matches.";
        }

        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(text);
    }
}

/// <summary>Registration of the page guard.</summary>
public static class ProviderPageExtensions
{
    /// <summary>
    /// Guards everything after it with the ticket Gleanvolt adds (#410). Put it before the page's
    /// endpoints. Uses the <see cref="ProviderOptions"/> the provider registered with for the provider id, and
    /// <see cref="ProviderCredentials"/> for the token, which can arrive while the provider runs.
    /// </summary>
    public static IApplicationBuilder UseGleanvoltProviderPage(this IApplicationBuilder app, Action<ProviderPageOptions>? configure = null)
    {
        var options = app.ApplicationServices.GetRequiredService<IOptions<ProviderPageOptions>>();
        configure?.Invoke(options.Value);
        return app.UseMiddleware<ProviderPageMiddleware>(Options.Create(options.Value));
    }
}

/// <summary>Who may show a provider's page in a frame: the CSP <c>frame-ancestors</c> sources.</summary>
public static class ProviderPageFraming
{
    /// <summary>
    /// The sources that may frame the page, for <c>frame-ancestors</c> after <c>'self'</c>.
    /// <paramref name="configured"/> wins when given. Otherwise Gleanvolt's <paramref name="endpoint"/>
    /// origin, except for an endpoint on this machine (#426): the browser then reaches Gleanvolt by a name
    /// the provider can't know (<c>gleanvolt.local</c>, an address), and Gleanvolt opens the page under
    /// that same name, so any host on Gleanvolt's port may frame it. The ticket still decides who sees it.
    /// </summary>
    public static string Sources(Uri endpoint, string? configured)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        return endpoint.IsLoopback
            ? AnyHostOn(endpoint)
            : endpoint.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>
    /// The sources that may frame the page, for the Gleanvolt in use by <paramref name="credentials"/>: as
    /// <see cref="Sources(Uri, string?)"/>, except that a paired endpoint is treated like one on this machine, any host
    /// on its port. A paired endpoint is whichever address the search found, often an IP from mDNS, while the owner
    /// opens Gleanvolt at <c>gleanvolt.local</c> or another name; the ticket still decides who sees the page.
    /// </summary>
    public static string Sources(ProviderCredentials credentials, string? configured)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var endpoint = credentials.Endpoint;
        return credentials.EndpointIsPaired && string.IsNullOrWhiteSpace(configured)
            ? AnyHostOn(endpoint)
            : Sources(endpoint, configured);
    }

    private static string AnyHostOn(Uri endpoint) => $"http://*:{endpoint.Port} https://*:{endpoint.Port}";
}
