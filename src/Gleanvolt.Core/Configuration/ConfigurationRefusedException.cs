namespace Gleanvolt.Core.Configuration;

/// <summary>
/// A configuration startup refuses, with its problems kept as well as listed (#292): the message is
/// the one startup has always printed, and <see cref="Problems"/> lets a settings page show each of
/// them in its viewer's language instead of splitting that message back into lines.
/// </summary>
public sealed class ConfigurationRefusedException(string heading, ConfigurationProblems problems)
    : InvalidOperationException(
        heading + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(problem => "  - " + problem)))
{
    /// <summary>Each problem, with its phrase where it has one.</summary>
    public ConfigurationProblems Problems { get; } = problems;
}
