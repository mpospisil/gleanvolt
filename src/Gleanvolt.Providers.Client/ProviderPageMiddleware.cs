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
/// its own, and nobody on the network can press its buttons without the owner's Gleanvolt.
/// </summary>
internal sealed class ProviderPageMiddleware(
    RequestDelegate next,
    IOptions<ProviderOptions> provider,
    IOptions<ProviderPageOptions> page,
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
        var now = _time.GetUtcNow();
        var cookieName = CookieName(options.ProviderId);

        if (context.Request.Query[ProviderPage.TicketParameter].ToString() is { Length: > 0 } ticket
            && ProviderPage.VerifyTicket(ticket, options.Token, options.ProviderId, now))
        {
            var expires = now + page.Value.CookieLifetime;
            context.Response.Cookies.Append(cookieName, ProviderPage.Sign(CookiePurpose, options.Token, options.ProviderId, expires), new CookieOptions
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

        if (ProviderPage.Verify(CookiePurpose, context.Request.Cookies[cookieName], options.Token, options.ProviderId, now))
        {
            return next(context);
        }

        logger.LogInformation("Refused {Path}: no valid ticket from Gleanvolt. Open the page from Gleanvolt.", context.Request.Path);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync("Open this page from Gleanvolt: the Car page, or Settings → External providers.");
    }
}

/// <summary>Registration of the page guard.</summary>
public static class ProviderPageExtensions
{
    /// <summary>
    /// Guards everything after it with the ticket Gleanvolt adds (#410). Put it before the page's
    /// endpoints. Uses the <see cref="ProviderOptions"/> the provider registered with, for the token and
    /// the provider id.
    /// </summary>
    public static IApplicationBuilder UseGleanvoltProviderPage(this IApplicationBuilder app, Action<ProviderPageOptions>? configure = null)
    {
        var options = app.ApplicationServices.GetRequiredService<IOptions<ProviderPageOptions>>();
        configure?.Invoke(options.Value);
        return app.UseMiddleware<ProviderPageMiddleware>(Options.Create(options.Value));
    }
}
