using System.Text.RegularExpressions;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// A list that several pages show, such as the supported inverters and chargers, is written once, in a
/// page of its own like <c>docs/SUPPORTED_INVERTERS.md</c>, and copied into each page between
/// <c>&lt;!-- include: … --&gt;</c> and <c>&lt;!-- end include --&gt;</c> by <c>scripts/sync-doc-includes.sh</c>. GitHub renders the copy,
/// not the fragment, so a fragment edited without running the script changes nothing anyone reads.
/// This fails until the copy is brought back in line.
/// </summary>
public class DocIncludeTests
{
    private static readonly string Root = FindRepositoryRoot();

    private static readonly Regex Include = new(
        @"^<!-- include: (?<path>\S+) -->\n(?<body>.*?)^<!-- end include -->$",
        RegexOptions.Multiline | RegexOptions.Singleline);

    public static TheoryData<string> Pages => new("README.md", "docs/SUPPORTED_PV_SYSTEMS.md");

    [Theory]
    [MemberData(nameof(Pages))]
    public void EveryIncludedFragmentMatchesItsSource(string page)
    {
        var content = Normalize(File.ReadAllText(Path.Combine(Root, page)));
        var includes = Include.Matches(content);

        Assert.NotEmpty(includes);

        foreach (Match include in includes)
        {
            var path = include.Groups["path"].Value;
            var hash = path.IndexOf('#', StringComparison.Ordinal);
            var file = hash < 0 ? path : path[..hash];
            var text = Normalize(File.ReadAllText(Path.Combine(Root, file)));
            var fragment = hash < 0 ? WithoutTitle(text) : Section(text, path[(hash + 1)..], path);

            Assert.True(
                include.Groups["body"].Value == fragment,
                $"{page}'s copy of {path} differs from the fragment. Edit the fragment, then run "
                + "scripts/sync-doc-includes.sh.");
        }
    }

    // "file#anchor" copies the lines under that heading, up to the next heading of the same or a higher
    // level, with the blank lines at either end trimmed -- as the script does.
    private static string Section(string text, string anchor, string path)
    {
        var lines = text.Split('\n');
        var section = new List<string>();
        var level = 0;
        var fence = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                fence = !fence;
            }

            var heading = fence ? null : Regex.Match(line, "^(#+) (.*)$");
            if (level > 0 && heading is { Success: true } && heading.Groups[1].Length <= level)
            {
                break;
            }

            if (level > 0)
            {
                section.Add(line);
            }
            else if (heading is { Success: true } && DocLinkTests.Slug(heading.Groups[2].Value) == anchor)
            {
                level = heading.Groups[1].Length;
            }
        }

        Assert.True(level > 0, $"{path}: no such section.");
        return string.Join("\n", section).Trim('\n') + "\n";
    }

    // The fragment's "# title" and the blank lines after it are not copied, as the script does.
    private static string WithoutTitle(string fragment) =>
        Regex.Replace(fragment, @"\A(?:# [^\n]*\n|\n)*", "");

    // A Windows checkout may turn the line endings into CRLF; the comparison is about the text.
    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Gleanvolt.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find the repository root from the test output directory.");
    }
}
