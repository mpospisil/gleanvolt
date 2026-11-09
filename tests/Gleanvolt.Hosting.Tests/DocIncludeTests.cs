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
            var fragment = WithoutTitle(Normalize(File.ReadAllText(Path.Combine(Root, path))));

            Assert.True(
                include.Groups["body"].Value == fragment,
                $"{page}'s copy of {path} differs from the fragment. Edit the fragment, then run "
                + "scripts/sync-doc-includes.sh.");
        }
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
