using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Resources;

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
    /// What a service said (#290): its <see cref="Phrase"/> in the viewer's language when there is one
    /// and the UI knows its key, and otherwise the service's own English sentence. Each argument is
    /// written here, in the viewer's numbers and the installation's <paramref name="zone"/>.
    /// </summary>
    public static string Say(Phrase? phrase, string english, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        if (phrase is null
            || ServiceText.ResourceManager.GetString(phrase.Key.Replace('.', '_'), CultureInfo.CurrentUICulture) is not { } template)
        {
            return english;
        }

        return Format(template, [.. phrase.Args.Select(arg => Argument(arg, zone))]);
    }

    /// <summary>
    /// <see cref="Say(Phrase?, string, TimeZoneInfo)"/> for what never carries a time of day: a
    /// validation message about a form. A <see cref="PhraseArg.Clock"/> in one would read in UTC.
    /// </summary>
    public static string Say(Phrase? phrase, string english) => Say(phrase, english, TimeZoneInfo.Utc);

    private static string Argument(PhraseArg arg, TimeZoneInfo zone) => arg switch
    {
        PhraseArg.Energy energy => Units.Energy(energy.WattHours),
        PhraseArg.Power power => Units.Power(power.Watts),
        PhraseArg.Percent percent => Units.Percent(percent.Value),
        PhraseArg.Clock clock => TimeZoneInfo.ConvertTime(clock.At, zone).ToString("HH:mm", CultureInfo.CurrentCulture),
        PhraseArg.Duration duration => Units.Duration(duration.Span),
        PhraseArg.Number number => number.Value.ToString(CultureInfo.CurrentCulture),
        PhraseArg.Text text => text.Value,
        _ => arg.ToString(),
    };

    /// <summary>
    /// An enum value as the UI shows it: the <c>{Type}_{Value}</c> key in <see cref="SharedText"/>,
    /// or the value's own name when there is none. The fallback is for robustness, not a habit:
    /// ResourceTests checks that every enum the UI shows has all of its names.
    /// </summary>
    public static string Name<TEnum>(TEnum value) where TEnum : struct, Enum =>
        SharedText.ResourceManager.GetString($"{typeof(TEnum).Name}_{value}", CultureInfo.CurrentUICulture)
        ?? value.ToString();

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

    /// <summary>
    /// <see cref="Rich"/> for markup that is only an element around words, where an inline Razor
    /// template is not allowed (inside another one): <c>("em", "car's")</c>.
    /// </summary>
    public static RenderFragment Tagged(string template, params (string Element, string Text)[] parts) =>
        Rich(template, [.. parts.Select(part => (RenderFragment)(builder =>
        {
            builder.OpenElement(0, part.Element);
            builder.AddContent(1, part.Text);
            builder.CloseElement();
        }))]);

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex PlaceholderPattern();
}
