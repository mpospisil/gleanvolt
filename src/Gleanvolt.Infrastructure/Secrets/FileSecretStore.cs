using Gleanvolt.Core.Interfaces;

namespace Gleanvolt.Infrastructure.Secrets;

/// <summary>
/// Secrets as owner-only files in the data directory (issue #215). The default, and what Linux,
/// Docker and the .deb get.
///
/// <para><b>Not a stopgap.</b> On a Pi where the service user owns the data directory,
/// <c>0600</c> defends against the one adversary a local key file would also defend against — another
/// unprivileged account on the box. Root is outside both, and so is anyone holding the SD card. The
/// options that sound stronger are the ones that break the unattended restart, which is the whole
/// point of the service; see <see cref="ISecretStore"/>.</para>
///
/// <para><b>One file per secret, named after it,</b> so the store's directory is readable by a person
/// and an upgrade finds the files exactly where the two hand-rolled stores it replaces left
/// them — <c>vw-website-session.json</c>, <c>skoda-api-key.json</c>. Nothing has to be migrated
/// because nothing moved.</para>
/// </summary>
/// <param name="directory">The data directory. Absolute by the time it gets here.</param>
public sealed class FileSecretStore(string directory) : ISecretStore
{
    /// <summary>Where the files are. For the startup log and for tests, not for callers to write to.</summary>
    public string Directory => directory;

    /// <inheritdoc />
    public string? Read(string name)
    {
        try
        {
            var path = PathFor(name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Unreadable is the same answer as absent: sign in again. A caller that stopped for this
            // would be a controller that will not start because a cookie jar is locked.
            return null;
        }
    }

    /// <inheritdoc />
    public bool Write(string name, string value) => WriteFile(PathFor(name), value);

    /// <inheritdoc />
    public bool Delete(string name)
    {
        try
        {
            var path = PathFor(name);

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public string Describe() => OperatingSystem.IsWindows()
        // Said as the file system's, not as ours: on Windows a new file inherits the directory's ACL
        // and this store does not narrow it. Under the Windows container that is the volume's ACL,
        // which is the honest answer and the reason DPAPI is deliberately not used there.
        ? "plain files, protected by the data directory's permissions"
        : "owner-only files (0600) in the data directory";

    /// <summary>The file a secret lives in. Public so the DPAPI store can share the layout.</summary>
    public string PathFor(string name) => Path.Combine(directory, name + ".json");

    /// <summary>
    /// Writes owner-only and atomically: a temporary file beside the target, then a rename, so a power
    /// cut leaves either the old secret or the new one and never half of one.
    ///
    /// <para>The mode is set <b>before</b> the secret is written rather than restricted after, so there
    /// is no moment at which it sits in a world-readable file. <c>UnixCreateMode</c> covers a new file
    /// and the explicit set covers one left over from a looser umask; Windows has no such concept.</para>
    /// </summary>
    internal static bool WriteFile(string path, string content)
    {
        var temporary = path + ".tmp";

        try
        {
            var folder = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(folder))
            {
                System.IO.Directory.CreateDirectory(folder);
            }

            var open = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };

            if (!OperatingSystem.IsWindows())
            {
                open.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(temporary, open))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(content);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            Move(temporary, path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Discard(temporary);
            return false;
        }
    }

    private static void Move(string temporary, string path)
    {
        try
        {
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            // An antivirus scanner or the search indexer can hold the target open for a moment, which
            // Windows reports as a sharing violation. Once more after it has had time to let go --
            // the same retry PvSystemOverrides.Write needs, for the same reason.
            Thread.Sleep(TimeSpan.FromMilliseconds(250));
            File.Move(temporary, path, overwrite: true);
        }
    }

    // A failed write must not leave the secret lying beside the target in a file nothing will ever
    // rename away.
    private static void Discard(string temporary)
    {
        try
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing useful to do; the write already failed and the caller is being told so.
        }
    }
}
