using System.Text;
using System.Text.RegularExpressions;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// Every link from one of the repository's documents to another — and to a section of one — has to
/// land where it says. Moving a section between pages is how documentation gets reorganised, and
/// GitHub gives no warning when it leaves a link pointing at nothing: the page opens at the top and
/// the reader is left to search. The same goes for a heading written twice on one page: GitHub
/// numbers the second <c>#huawei</c> as <c>#huawei-1</c>, so which section a link means depends on
/// the order the page happens to be in.
/// </summary>
/// <remarks>
/// Links are checked in the Markdown files and in the web UI's resources, both relative ones and
/// absolute ones to this repository's <c>blob/main</c> (which pages included into the wiki use).
/// Anchors follow GitHub's rule: the heading's text, lower-cased, punctuation dropped, spaces turned
/// into hyphens.
/// </remarks>
public class DocLinkTests
{
    private static readonly string Root = FindRepositoryRoot();

    private const string RepositoryUrl = "https://github.com/mpospisil/gleanvolt/blob/main/";

    private static readonly string[] SkippedDirectories = ["bin", "obj", ".git", "node_modules", "TestResults"];

    // [text](target) and [text](target "title"); images too. Angle-bracketed targets are rare enough to skip.
    private static readonly Regex MarkdownLink = new(@"\]\((?<target>[^)\s]+)(?:\s+""[^""]*"")?\)");

    private static readonly Regex AbsoluteLink = new(
        Regex.Escape(RepositoryUrl) + @"(?<target>[^\s)""'<>\]]+)");

    private static readonly Regex Heading = new(@"^(?<hashes>#{1,6})\s+(?<text>.+?)\s*#*\s*$");

    private static readonly Regex HtmlAnchor = new(@"<a\s+(?:name|id)=""(?<id>[^""]+)""", RegexOptions.IgnoreCase);

    // A log repeats its subheadings in every entry ("Tests", "Files changed"), and nothing links to them.
    private static readonly string[] Logs = ["docs/IMPLEMENTATION_LOG.md"];

    private static readonly Dictionary<string, Page?> Pages = new(StringComparer.Ordinal);

    public static TheoryData<string> Documents()
    {
        var data = new TheoryData<string>();
        foreach (var file in Files(Root, "*.md").Concat(Files(Path.Combine(Root, "src"), "*.resx")))
        {
            data.Add(Relative(file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void EveryLinkToAnotherDocumentLandsOnIt(string document)
    {
        var text = Normalize(File.ReadAllText(Path.Combine(Root, document)));
        var isMarkdown = document.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        var problems = new List<string>();

        foreach (var (target, line) in Links(text, isMarkdown))
        {
            var problem = Check(document, target);
            if (problem is not null)
            {
                problems.Add($"line {line}: {target} — {problem}");
            }
        }

        Assert.True(problems.Count == 0, $"{document} has links that go nowhere:\n" + string.Join("\n", problems));
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void NoHeadingIsWrittenTwiceOnOnePage(string document)
    {
        if (!document.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || Logs.Contains(document))
        {
            return;
        }

        var page = Load(document)!;
        var duplicates = page.HeadingSlugs
            .GroupBy(slug => slug, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.True(
            duplicates.Count == 0,
            $"{document} has headings that GitHub numbers to tell them apart (#{string.Join(", #", duplicates)}). "
            + "Give each its own wording, so a link to it means one section whatever the order of the page.");
    }

    private static IEnumerable<(string Target, int Line)> Links(string text, bool isMarkdown)
    {
        var lines = text.Split('\n');
        var inFence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (isMarkdown && line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                continue;
            }

            if (isMarkdown)
            {
                // Inline code is an example, not a link.
                var withoutCode = Regex.Replace(line, "`[^`]*`", "");
                foreach (Match match in MarkdownLink.Matches(withoutCode))
                {
                    var target = match.Groups["target"].Value;
                    if (!target.Contains("://", StringComparison.Ordinal) && !target.StartsWith("mailto:", StringComparison.Ordinal))
                    {
                        yield return (target, i + 1);
                    }
                }
            }

            foreach (Match match in AbsoluteLink.Matches(line))
            {
                yield return (RepositoryUrl + match.Groups["target"].Value, i + 1);
            }
        }
    }

    // Null when the link lands; otherwise what is wrong with it.
    private static string? Check(string document, string target)
    {
        string path;
        if (target.StartsWith(RepositoryUrl, StringComparison.Ordinal))
        {
            path = target[RepositoryUrl.Length..];
        }
        else
        {
            var directory = Path.GetDirectoryName(document) ?? "";
            path = target.StartsWith('#') ? document + target : Path.Combine(directory, target);
        }

        var hash = path.IndexOf('#', StringComparison.Ordinal);
        var file = Uri.UnescapeDataString(hash < 0 ? path : path[..hash]);
        var anchor = hash < 0 ? null : Uri.UnescapeDataString(path[(hash + 1)..]);
        var query = file.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            file = file[..query];
        }

        file = Path.GetRelativePath(Root, Path.GetFullPath(Path.Combine(Root, file)));
        var full = Path.Combine(Root, file);
        if (!File.Exists(full) && !Directory.Exists(full))
        {
            return $"there is no {file}";
        }

        if (string.IsNullOrEmpty(anchor) || !file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var page = Load(file)!;
        if (page.Anchors.Contains(anchor))
        {
            return null;
        }

        return $"{file} has no section #{anchor}";
    }

    private static Page? Load(string file)
    {
        file = file.Replace('\\', '/');
        if (Pages.TryGetValue(file, out var cached))
        {
            return cached;
        }

        var page = Page.Parse(Normalize(File.ReadAllText(Path.Combine(Root, file))));
        Pages[file] = page;
        return page;
    }

    private sealed record Page(IReadOnlyList<string> HeadingSlugs, HashSet<string> Anchors)
    {
        public static Page Parse(string text)
        {
            var slugs = new List<string>();
            var anchors = new HashSet<string>(StringComparer.Ordinal);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var inFence = false;
            foreach (var line in text.Split('\n'))
            {
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    inFence = !inFence;
                    continue;
                }

                if (inFence)
                {
                    continue;
                }

                foreach (Match anchor in HtmlAnchor.Matches(line))
                {
                    anchors.Add(anchor.Groups["id"].Value);
                }

                var heading = Heading.Match(line);
                if (!heading.Success)
                {
                    continue;
                }

                var slug = Slug(heading.Groups["text"].Value);
                slugs.Add(slug);
                // GitHub's own numbering, so a link to "#x-1" still resolves; the duplicate test is what objects to it.
                var seen = counts.GetValueOrDefault(slug);
                anchors.Add(seen == 0 ? slug : $"{slug}-{seen}");
                counts[slug] = seen + 1;
            }

            return new Page(slugs, anchors);
        }
    }

    // GitHub's heading anchor: the rendered text, lower-cased, everything but letters, digits, spaces,
    // hyphens and underscores dropped, then each space turned into a hyphen.
    internal static string Slug(string heading)
    {
        var text = Regex.Replace(heading, @"!?\[(?<text>[^\]]*)\]\([^)]*\)", "${text}");
        text = Regex.Replace(text, "<[^>]+>", "");
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
            {
                builder.Append(c);
            }
            else if (c == ' ')
            {
                builder.Append('-');
            }
        }

        return builder.ToString();
    }

    private static IEnumerable<string> Files(string directory, string pattern)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false };
        foreach (var file in Directory.EnumerateFiles(directory, pattern, options))
        {
            yield return file;
        }

        foreach (var child in Directory.EnumerateDirectories(directory, "*", options))
        {
            if (Array.IndexOf(SkippedDirectories, Path.GetFileName(child)) < 0)
            {
                foreach (var file in Files(child, pattern))
                {
                    yield return file;
                }
            }
        }
    }

    private static string Relative(string file) => Path.GetRelativePath(Root, file).Replace('\\', '/');

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
