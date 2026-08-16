using System.Reflection;

namespace Solax.Hosting;

/// <summary>
/// What this build actually is, read from the assembly's informational version.
///
/// <para>It exists because "which version is on the Pi?" had no answer from inside the running
/// system: the container's image tag knows, but the process did not, so anyone holding a log file
/// had no way to tell which build produced it. The version is logged once at startup and published
/// to Home Assistant as the device's software version.</para>
/// </summary>
public static class BuildInfo
{
    /// <summary>
    /// The full informational version, e.g. <c>1.0.0+31bf347…</c> on a release build or
    /// <c>0.0.0-dev</c> locally. Never null — an assembly with no attribute reports "unknown".
    /// </summary>
    public static string InformationalVersion { get; } =
        typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    /// <summary>The version without the commit, e.g. <c>1.0.0</c>.</summary>
    public static string Version { get; } = SplitVersion(InformationalVersion).Version;

    /// <summary>
    /// The commit the build came from, or null when it was not stamped — which is the normal case
    /// for a local build and a reliable signal that this did not come from CI.
    /// </summary>
    public static string? CommitSha { get; } = SplitVersion(InformationalVersion).Commit;

    /// <summary>The commit abbreviated to the 7 characters git and the image's sha- tags use.</summary>
    public static string? ShortCommitSha =>
        CommitSha is { Length: >= 7 } sha ? sha[..7] : CommitSha;

    /// <summary>Version and commit in one line, for logs and for Home Assistant's device page.</summary>
    public static string Describe() =>
        ShortCommitSha is null ? Version : $"{Version} ({ShortCommitSha})";

    // The SDK builds InformationalVersion as "<version>+<SourceRevisionId>". Split on the *first*
    // '+' only: a semver build-metadata section may itself contain further separators, and the
    // commit is whatever follows the first one.
    private static (string Version, string? Commit) SplitVersion(string informational)
    {
        var plus = informational.IndexOf('+');
        return plus < 0
            ? (informational, null)
            : (informational[..plus], informational[(plus + 1)..] is { Length: > 0 } c ? c : null);
    }
}
