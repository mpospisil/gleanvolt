using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;

namespace Gleanvolt.Web.Components;

/// <summary>
/// The three things a translated sentence needs beyond a string from a resource (#290).
///
/// <para>Text lives in whole sentences with numbered placeholders ("Ready by {0}"), never in
/// fragments glued together around a value: another language puts the value somewhere else, and a
/// sentence cut in two cannot be reordered by whoever translates it.</para>
/// </summary>
public static partial class Localized
{
    /// <summary>A resource template with its placeholders filled, in the viewer's culture.</summary>
    public static string Format(string template, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, template, args);

    /// <summary>
    /// Which of a counted phrase's forms the viewer's language uses for <paramref name="count"/>.
    /// Every resource that counts has all three forms (<c>…_One</c>, <c>…_Few</c>, <c>…_Other</c>);
    /// English simply repeats <c>_Other</c> as <c>_Few</c>, and Czech is why the middle one exists
    /// (1 den, 2–4 dny, 5 dní). A language with more forms adds its rule here.
    /// </summary>
    public static string Plural(long count, string one, string few, string other) =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
        {
            "cs" or "sk" => Math.Abs(count) switch
            {
                1 => one,
                >= 2 and <= 4 => few,
                _ => other,
            },
            _ => Math.Abs(count) == 1 ? one : other,
        };

    /// <summary>
    /// A template whose placeholders are markup rather than text: "<c>Ready by {0}</c>" with
    /// <c>{0}</c> a <c>&lt;strong&gt;</c>. The words stay one translatable sentence, and the markup
    /// never enters a resource, so a translator cannot break it.
    /// </summary>
    public static RenderFragment Rich(string template, params RenderFragment[] parts) => builder =>
    {
        var at = 0;
        foreach (Match placeholder in PlaceholderPattern().Matches(template))
        {
            if (placeholder.Index > at)
            {
                builder.AddContent(0, template[at..placeholder.Index]);
            }

            var index = int.Parse(placeholder.Groups[1].Value, CultureInfo.InvariantCulture);
            builder.AddContent(1, index < parts.Length ? parts[index] : null);
            at = placeholder.Index + placeholder.Length;
        }

        if (at < template.Length)
        {
            builder.AddContent(2, template[at..]);
        }
    };

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex PlaceholderPattern();
}
