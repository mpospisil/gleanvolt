using System.Globalization;
using System.Text.RegularExpressions;
using Gleanvolt.Providers.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;

namespace Gleanvolt.Chargers;

/// <summary>
/// Which language the plugin's pages use. Gleanvolt names it in the <c>?culture=</c> query of every page it opens, so the
/// query is authoritative: a language the plugin cannot use is English, never whatever an old cookie remembers. Only the
/// requests that carry no query (the circuit's own) fall back to the cookie.
/// </summary>
internal static partial class PluginCulture
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    /// <summary>The culture for a request: the query's if there is one, else the cookie's, and English when that is unusable.</summary>
    internal static CultureInfo Choose(string? query, string? cookie)
    {
        if (query is not null)
        {
            return Usable(query);
        }

        var parsed = CookieRequestCultureProvider.ParseCookieValue(cookie ?? string.Empty);
        return parsed is { UICultures.Count: > 0 } && parsed.UICultures[0].Value is { } name ? Usable(name) : English;
    }

    /// <summary>Adds the middleware that sets the request's culture and <see cref="IRequestCultureFeature"/> (the cookie writer reads it).</summary>
    internal static IApplicationBuilder UsePluginCulture(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use((context, next) =>
        {
            // A repeated parameter (a,b) is not a culture Gleanvolt sends, so it reads as unusable and gives English.
            string? query = context.Request.Query.TryGetValue(ProviderPage.CultureParameter, out var values) ? values.ToString() : null;
            context.Request.Cookies.TryGetValue(CookieRequestCultureProvider.DefaultCookieName, out var cookie);

            var chosen = Choose(query, cookie);
            CultureInfo.CurrentCulture = chosen;
            CultureInfo.CurrentUICulture = chosen;
            context.Features.Set<IRequestCultureFeature>(new RequestCultureFeature(new RequestCulture(chosen), provider: null));
            return next(context);
        });
    }

    // Bounded and shaped like a language tag before the runtime is asked, then only the cultures the runtime knows.
    private static CultureInfo Usable(string name)
    {
        if (name.Length > 64 || !LanguageTag().IsMatch(name))
        {
            return English;
        }

        try
        {
            return CultureInfo.GetCultureInfo(name, predefinedOnly: true);
        }
        catch (CultureNotFoundException)
        {
            return English;
        }
    }

    [GeneratedRegex("^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageTag();
}
