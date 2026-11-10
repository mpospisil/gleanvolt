using System.Globalization;
using System.Text.RegularExpressions;

namespace Gleanvolt.Web;

/// <summary>
/// Addresses in the user manual (#329), for the build that is running and the language the viewer
/// reads the UI in.
///
/// <para>The manual is published per minor version (<c>/v1.4/</c>), so a link from 1.4.2 opens the
/// pages written for 1.4 rather than whatever is newest. A development build has no published version
/// of its own and links to <c>/next/</c>, built from <c>main</c>; a build whose version can't be read
/// links to <c>/latest/</c>. The language is the UI's when the manual has it, English otherwise, and an
/// untranslated page shows the English one with a notice.</para>
/// </summary>
public static partial class ManualLinks
{
    /// <summary>Where every version of the manual is published.</summary>
    public const string Site = "https://mpospisil.github.io/gleanvolt/";

    /// <summary>The languages the manual is written in, English being the default.</summary>
    public static IReadOnlyList<string> Languages { get; } = ["en", "cs"];

    /// <summary>
    /// The address of <paramref name="page"/> (e.g. <c>how-it-works/unmetered-phase</c>, or empty for the
    /// welcome page) in the manual for <paramref name="build"/>, in <paramref name="culture"/>'s language.
    /// </summary>
    public static string For(string page, WebBuildInfo? build, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(culture);

        var language = Languages.Contains(culture.TwoLetterISOLanguageName) ? culture.TwoLetterISOLanguageName : Languages[0];
        var path = page.Trim('/');
        return $"{Site}{Version(build)}/{language}/{(path.Length == 0 ? string.Empty : path + "/")}";
    }

    /// <summary>The manual's folder for a build: <c>v1.4</c>, <c>next</c> or <c>latest</c>.</summary>
    public static string Version(WebBuildInfo? build)
    {
        if (build is null || MajorMinor().Match(build.Version) is not { Success: true } match)
        {
            return "latest";
        }

        // 0.0.0-dev is a local build, which is ahead of every release.
        return match.Groups[1].Value == "0" ? "next" : $"v{match.Groups[1].Value}.{match.Groups[2].Value}";
    }

    [GeneratedRegex(@"^\s*(\d+)\.(\d+)\.\d+")]
    private static partial Regex MajorMinor();
}
