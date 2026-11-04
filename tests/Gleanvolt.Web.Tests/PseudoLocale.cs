using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The pseudo-locale check (#290): a page rendered in <c>qps-ploc</c> shows every string from a
/// resource accented and in brackets, so a word of plain English left outside the brackets is text
/// that never reached a resource.
///
/// <para>Each page's own fixture calls this from one test, with the page in its fullest state, so
/// the check reuses the fakes that fixture already sets up rather than keeping a second copy.</para>
/// </summary>
internal static partial class PseudoLocale
{
    // Not English: units and symbols, and the product's and the data's own names.
    private static readonly HashSet<string> AlwaysAllowed = new(StringComparer.Ordinal)
    {
        "kWh", "kWp", "Wh", "km", "hPa", "SOC", "p10", "p90", "Gleanvolt", "Solcast", "MQTT", "API",
    };

    /// <summary>Runs <paramref name="render"/> with the pseudo-locale as the current culture.</summary>
    public static T Render<T>(Func<T> render)
    {
        var (culture, uiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        var pseudo = CultureInfo.GetCultureInfo(WebUiCultures.Pseudo);

        CultureInfo.CurrentCulture = pseudo;
        CultureInfo.CurrentUICulture = pseudo;
        try
        {
            return render();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    /// <summary>
    /// Fails with every run of English words found outside a bracketed (translated) string.
    /// <paramref name="allowed"/> is the page's own data: names and values the fixture put there.
    /// </summary>
    public static void AssertTranslated(string markup, params string[] allowed)
    {
        var text = WebUtility.HtmlDecode(Tags().Replace(Scripts().Replace(markup, " "), " "));

        // A page that rendered in English passes nothing: there is nothing in brackets to strip.
        Assert.True(Bracketed().IsMatch(text), "Nothing on the page is in the pseudo-locale; was it rendered in it?");

        // Innermost first, until none is left: a translated sentence may carry another inside it.
        string previous;
        do
        {
            previous = text;
            text = Bracketed().Replace(text, " ");
        }
        while (text != previous);

        // The pseudo culture's own date names are ICU's root data, which is English; they come from
        // the culture rather than from a resource, so they are not this check's business.
        var dates = CultureInfo.GetCultureInfo(WebUiCultures.Pseudo).DateTimeFormat;
        allowed = [.. allowed, .. dates.AbbreviatedDayNames, .. dates.DayNames, .. dates.AbbreviatedMonthNames, .. dates.MonthNames];

        var data = allowed.SelectMany(value => Words().Matches(value).Select(match => match.Value)).ToHashSet(StringComparer.Ordinal);

        var english = Words().Matches(text)
            .Select(match => match.Value)
            .Where(word => !AlwaysAllowed.Contains(word) && !data.Contains(word))
            .Distinct()
            .ToList();

        Assert.True(english.Count == 0, "Untranslated text: " + string.Join(", ", english));
    }

    [GeneratedRegex(@"<script[\s\S]*?</script>|<style[\s\S]*?</style>|<!--[\s\S]*?-->")]
    private static partial Regex Scripts();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\[[^\[\]]*\]")]
    private static partial Regex Bracketed();

    // Three letters or more of plain ASCII: a pseudo-localized word always carries an accent, and
    // "W", "A", "kW" and the like are units.
    [GeneratedRegex(@"\b[A-Za-z]{3,}\b")]
    private static partial Regex Words();
}
