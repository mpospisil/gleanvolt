using System.Text.RegularExpressions;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The image builds restore against a hand-written list of project files, so that the slow restore
/// layer survives a source edit. The cost of that list is that it can fall behind the repository, and
/// it did: <c>Gleanvolt.Api</c> arrived, both Dockerfiles kept restoring the four projects they knew
/// about, and every published image failed with <c>NETSDK1004</c> — after CI had gone green, because
/// nothing in the test run builds an image.
///
/// <para>This is the cheapest guard that would have caught it: a new project under <c>src/</c> must
/// appear in the solution and in both Dockerfiles, and the answer is knowable from the filesystem
/// without Docker, a network, or a build.</para>
/// </summary>
public class DockerfileTests
{
    private static readonly string Root = FindRepositoryRoot();

    public static TheoryData<string> Dockerfiles => new("Dockerfile", "Dockerfile.windows");

    [Theory]
    [MemberData(nameof(Dockerfiles))]
    public void EveryProjectUnderSrcIsRestoredByTheImageBuild(string dockerfile)
    {
        var content = File.ReadAllText(Path.Combine(Root, dockerfile));

        foreach (var project in Projects())
        {
            // The path as the COPY line spells it, forward slashes on both platforms -- Docker takes
            // no other kind, including in the Windows build.
            var path = $"src/{project}/{project}.csproj";

            Assert.True(
                content.Contains(path, StringComparison.Ordinal),
                $"{dockerfile} does not copy {path}, so `dotnet restore` there will not see that project "
                + "and the publish that follows it fails with NETSDK1004.");
        }
    }

    /// <summary>
    /// The image build cannot read <c>Directory.Build.props</c>: .dockerignore keeps it out of the
    /// build context, so each Dockerfile carries the product line as a literal default for its
    /// <c>ARG VERSION</c>. That literal is a copy, and a copy drifts — bumping the minor in the
    /// props and nowhere else would leave every locally built image reporting the previous line at
    /// startup and to Home Assistant, which is a lie that no build error would ever surface.
    /// </summary>
    [Theory]
    [MemberData(nameof(Dockerfiles))]
    public void TheImageBuildsDefaultVersionIsTheProductLineInDirectoryBuildProps(string dockerfile)
    {
        var expected = $"ARG VERSION={MajorMinorVersion()}.0-dev";
        var content = File.ReadAllText(Path.Combine(Root, dockerfile));

        Assert.True(
            content.Contains(expected, StringComparison.Ordinal),
            $"{dockerfile} does not default to `{expected}`, so an image built without a --build-arg "
            + "would report a version from a different product line than the one this repository is on.");
    }

    /// <summary>The major and minor from Directory.Build.props, e.g. <c>1.0</c>.</summary>
    private static string MajorMinorVersion()
    {
        var props = File.ReadAllText(Path.Combine(Root, "Directory.Build.props"));
        var match = Regex.Match(props, @"<MajorMinorVersion>([^<]+)</MajorMinorVersion>");

        Assert.True(match.Success, "Directory.Build.props no longer declares <MajorMinorVersion>.");
        return match.Groups[1].Value.Trim();
    }

    [Fact]
    public void EveryProjectUnderSrcIsInTheSolution()
    {
        var solution = File.ReadAllText(Path.Combine(Root, "Gleanvolt.slnx"));

        foreach (var project in Projects())
        {
            Assert.Contains($"src/{project}/{project}.csproj", solution, StringComparison.Ordinal);
        }
    }

    /// <summary>Project directory names under <c>src/</c>, read from the repository itself.</summary>
    private static IEnumerable<string> Projects() =>
        Directory.EnumerateDirectories(Path.Combine(Root, "src"))
            .Select(directory => new DirectoryInfo(directory).Name)
            .Where(name => File.Exists(Path.Combine(Root, "src", name, $"{name}.csproj")))
            .OrderBy(name => name, StringComparer.Ordinal);

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
