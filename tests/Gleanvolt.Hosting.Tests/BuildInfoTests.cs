namespace Gleanvolt.Hosting.Tests;

public class BuildInfoTests
{
    [Fact]
    public void ReportsSomething()
    {
        // The test assembly is built from the same Directory.Build.props, so this asserts the
        // attribute is actually emitted -- the failure mode being an empty version in every log.
        Assert.False(string.IsNullOrWhiteSpace(BuildInfo.InformationalVersion));
        Assert.False(string.IsNullOrWhiteSpace(BuildInfo.Version));
        Assert.NotEqual("unknown", BuildInfo.InformationalVersion);
    }

    [Fact]
    public void VersionNeverCarriesTheCommit() =>
        Assert.DoesNotContain('+', BuildInfo.Version);

    [Fact]
    public void Describe_WithoutACommit_IsJustTheVersion()
    {
        // A local build is not stamped, so this is the shape a developer sees.
        if (BuildInfo.CommitSha is null)
        {
            Assert.Equal(BuildInfo.Version, BuildInfo.Describe());
        }
        else
        {
            Assert.Equal($"{BuildInfo.Version} ({BuildInfo.ShortCommitSha})", BuildInfo.Describe());
        }
    }

    [Fact]
    public void ShortCommitSha_IsSevenCharacters_WhenStamped()
    {
        if (BuildInfo.CommitSha is { Length: >= 7 })
        {
            Assert.Equal(7, BuildInfo.ShortCommitSha!.Length);
            Assert.StartsWith(BuildInfo.ShortCommitSha, BuildInfo.CommitSha);
        }
    }
}
