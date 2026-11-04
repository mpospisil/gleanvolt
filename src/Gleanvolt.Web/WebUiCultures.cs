using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Hosting;

namespace Gleanvolt.Web;

/// <summary>
/// The languages the web UI is offered in, and how a browser gets one (#290).
///
/// <para>Localization stops at the web UI. The process runs invariant (Gleanvolt.Worker pins it
/// before anything starts), and only a request for a UI page -- and the circuit that page opens --
/// runs in the viewer's culture. The HTTP API is excluded explicitly, so nothing the API, MQTT, Home
/// Assistant or the logs say changes with whoever is looking at the dashboard.</para>
///
/// <para>Per browser rather than per installation: two people on one roof may well read different
/// languages, and the choice costs nothing to keep in a cookie. A browser that has not chosen gets
/// the best match for its Accept-Language, and English when nothing matches.</para>
/// </summary>
public static class WebUiCultures
{
    /// <summary>
    /// The languages the UI ships in, the first being the default. Adding one means a .resx beside
    /// each neutral one under Resources/ and its name here -- nothing else.
    /// </summary>
    public static IReadOnlyList<string> Shipped { get; } = ["en", "cs"];

    /// <summary>
    /// The pseudo-locale, offered in Development only: every string accented and bracketed, so a
    /// page that still shows plain English somewhere has a string that never reached a resource.
    /// </summary>
    public const string Pseudo = "qps-ploc";

    /// <summary>Where the language picker posts; allowed through the setup gate.</summary>
    public const string Path = "/culture";

    /// <summary>The languages on offer in this environment, in the order the picker lists them.</summary>
    public static IReadOnlyList<CultureInfo> Offered(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var names = environment.IsDevelopment() ? [.. Shipped, Pseudo] : Shipped;
        return [.. names.Select(CultureInfo.GetCultureInfo)];
    }

    internal static void Configure(RequestLocalizationOptions options, IHostEnvironment environment)
    {
        var offered = Offered(environment).ToList();

        options.DefaultRequestCulture = new RequestCulture(offered[0]);

        // Formatting and text follow the same choice: a Czech page with English number formatting,
        // or the reverse, is not something anyone asks for.
        options.SupportedCultures = offered;
        options.SupportedUICultures = offered;

        // The cookie the picker writes, then the browser's own list. The query-string provider goes:
        // a culture in a shared link would switch the language of whoever opens it.
        options.RequestCultureProviders =
        [
            new CookieRequestCultureProvider(),
            new AcceptLanguageHeaderRequestCultureProvider(),
        ];
    }

    /// <summary>
    /// Runs request localization for everything but the HTTP API. The Blazor hub is a request like
    /// any other, so the circuit a page opens starts in the same culture as the page itself.
    /// </summary>
    internal static void UseWebUiCultures(this WebApplication app) =>
        app.UseWhen(
            context => !context.Request.Path.StartsWithSegments("/api"),
            branch => branch.UseRequestLocalization());

    /// <summary>
    /// The language picker's endpoint: remembers the choice in the culture cookie and goes back to
    /// the page it was made on. A form post rather than a circuit call, because the cookie has to be
    /// written onto an HTTP response, and the full reload that follows is what restarts the circuit
    /// in the new language.
    /// </summary>
    internal static void MapWebUiCultures(this WebApplication app)
    {
        var offered = Offered(app.Environment);

        app.MapPost(Path, async context =>
        {
            var form = await context.Request.ReadFormAsync();
            var name = form["culture"].ToString();
            var back = form["returnUrl"].ToString();

            if (offered.FirstOrDefault(culture => string.Equals(culture.Name, name, StringComparison.OrdinalIgnoreCase)) is { } chosen)
            {
                context.Response.Cookies.Append(
                    CookieRequestCultureProvider.DefaultCookieName,
                    CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(chosen)),
                    new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, SameSite = SameSiteMode.Lax });
            }

            // Only back to a page of this site: an absolute or protocol-relative URL here would make
            // the endpoint an open redirect.
            var local = back.StartsWith('/') && !back.StartsWith("//", StringComparison.Ordinal) && !back.StartsWith("/\\", StringComparison.Ordinal);
            context.Response.Redirect(local ? back : "/");
        });
    }
}
