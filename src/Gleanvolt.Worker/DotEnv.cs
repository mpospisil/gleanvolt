namespace Gleanvolt.Worker;

/// <summary>
/// Minimal loader for a local, untracked <c>.env</c> file so developer secrets (e.g.
/// <c>Solcast__ApiKey</c>) can live outside every tracked file while still reaching the app.
/// Values are pushed into the process environment <em>before</em> configuration is built, so the
/// standard environment-variable configuration provider picks them up — meaning both
/// <c>dotnet run</c> and the VS Code debugger get them without a shell profile or committed
/// config. <c>*.env</c> is gitignored; see <c>.env.example</c> for the format.
/// </summary>
internal static class DotEnv
{
    /// <summary>
    /// Finds the nearest <c>.env</c> file at or above <paramref name="startDirectory"/> and loads
    /// its <c>KEY=VALUE</c> lines into the process environment. Blank lines and <c>#</c> comments
    /// are skipped and surrounding double quotes are stripped. A variable already present in the
    /// environment is never overwritten, so a real system/CI variable always wins over the file.
    /// No-op when no <c>.env</c> file exists.
    /// </summary>
    public static void Load(string startDirectory)
    {
        var path = FindEnvFile(startDirectory);
        if (path is null)
        {
            return;
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"');

            if (Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    private static string? FindEnvFile(string startDirectory)
    {
        for (var directory = new DirectoryInfo(startDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, ".env");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
