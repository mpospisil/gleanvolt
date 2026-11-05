namespace Gleanvolt.Hosting.Cli;

/// <summary>
/// The .deb's service environment, for a command run by hand (#303). The unit file sets where the
/// data lives -- the overrides file, the secrets, the databases -- with <c>Environment=</c> lines, and
/// <c>/etc/gleanvolt/gleanvolt.env</c> holds the rest. A command started from a shell has none of it,
/// and would write the setup somewhere the service never looks. Loaded only for a command, only when
/// this binary is the installed one, and never over a variable already set.
/// </summary>
public static class DebEnvironment
{
    public const string InstallDirectory = "/opt/gleanvolt/";
    public const string UnitFile = "/usr/lib/systemd/system/gleanvolt.service";
    public const string EnvironmentFile = "/etc/gleanvolt/gleanvolt.env";

    /// <summary>
    /// Loads the service's environment when this is the .deb's binary, and returns the user the
    /// service runs as; null anywhere else.
    /// </summary>
    public static string? Load(string? processPath, Func<string, bool>? exists = null, Func<string, string[]>? read = null)
    {
        exists ??= File.Exists;
        read ??= File.ReadAllLines;

        if (!OperatingSystem.IsLinux()
            || processPath is null
            || !processPath.StartsWith(InstallDirectory, StringComparison.Ordinal)
            || !exists(UnitFile))
        {
            return null;
        }

        string? user = null;

        foreach (var line in read(UnitFile).Select(line => line.Trim()))
        {
            if (line.StartsWith("User=", StringComparison.Ordinal))
            {
                user = line["User=".Length..].Trim();
            }
            else if (line.StartsWith("Environment=", StringComparison.Ordinal))
            {
                Set(line["Environment=".Length..]);
            }
        }

        if (exists(EnvironmentFile))
        {
            try
            {
                foreach (var line in read(EnvironmentFile).Select(line => line.Trim()))
                {
                    if (line.Length > 0 && !line.StartsWith('#'))
                    {
                        Set(line);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Readable by the service user only; the user check that follows says who to run as.
            }
        }

        return user ?? "gleanvolt";
    }

    private static void Set(string assignment)
    {
        assignment = assignment.Trim().Trim('"');
        var equals = assignment.IndexOf('=', StringComparison.Ordinal);

        if (equals <= 0)
        {
            return;
        }

        var name = assignment[..equals].Trim();
        var value = assignment[(equals + 1)..].Trim().Trim('"');

        if (Environment.GetEnvironmentVariable(name) is null)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
