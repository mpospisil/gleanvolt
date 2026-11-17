namespace Gleanvolt.Plugin.Volkswagen.Volkswagen;

/// <summary>
/// The plugin's data directory (#410): the account and the session cookie jar, one file each. It stands
/// in for Gleanvolt's <c>ISecretStore</c>, which a plugin does not have, and does what that store's file
/// variant does on Linux: every file readable and writable by the plugin's user alone (0600), the
/// directory 0700, written atomically, nothing encrypted. On Windows there is no DPAPI here, unlike
/// Gleanvolt's store. What protects the files is their permissions and whoever can read the volume: see
/// README.md.
/// </summary>
public sealed class DataFiles
{
    private readonly string _directory;
    private readonly Lock _gate = new();

    public DataFiles(VolkswagenOptions options)
    {
        _directory = Path.GetFullPath(options.DataDirectory);
    }

    /// <summary>What protects the files, for a sentence that has to mention it.</summary>
    public Words Describe() => OperatingSystem.IsWindows()
        ? new Words("files in {0}, protected only by the folder's permissions", "soubory v {0}, chráněné jen oprávněními složky").Format(_directory)
        : new Words("files in {0}, readable only by the plugin's user (0600)", "soubory v {0}, čitelné jen pro uživatele pluginu (0600)").Format(_directory);

    public string? Read(string name)
    {
        lock (_gate)
        {
            var path = PathOf(name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
    }

    /// <summary>Writes atomically: a crash mid-write leaves the old file, never half a new one.</summary>
    public bool Write(string name, string value)
    {
        lock (_gate)
        {
            try
            {
                EnsureDirectory();
                var path = PathOf(name);
                var temporary = path + ".tmp";

                if (OperatingSystem.IsWindows())
                {
                    File.WriteAllText(temporary, value);
                }
                else
                {
                    using var stream = new FileStream(temporary, new FileStreamOptions
                    {
                        Mode = FileMode.Create,
                        Access = FileAccess.Write,
                        UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    });
                    using var writer = new StreamWriter(stream);
                    writer.Write(value);
                }

                // UnixCreateMode covers a new file only; a temporary file left by a crash keeps its mode.
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                File.Move(temporary, path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public void Delete(string name)
    {
        lock (_gate)
        {
            File.Delete(PathOf(name));
        }
    }

    private string PathOf(string name) => Path.Combine(_directory, name);

    private void EnsureDirectory()
    {
        if (Directory.Exists(_directory))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(_directory);
        }
        else
        {
            Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
