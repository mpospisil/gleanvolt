using System.IO.Compression;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// The committed fixtures, and the ZIP the parser tests build out of them (issue #139).
///
/// <para>Built at run time rather than committed as an archive: a <c>.zip</c> in the tree is opaque
/// in a pull request — nobody can see that a fixture changed, only that some bytes did — and building
/// it here exercises exactly the same <see cref="ZipArchive"/> path while leaving the contents
/// reviewable. See <c>Fixtures/VwGroup/README.md</c>.</para>
/// </summary>
internal static class VwGroupFixtures
{
    private static readonly string Folder =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "VwGroup");

    public static string Read(string name) => File.ReadAllText(Path.Combine(Folder, name));

    /// <summary>A download holding the named fixtures, plus the non-JSON member a real bundle carries.</summary>
    public static byte[] Bundle(params string[] names)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in names)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(Read(name));
            }

            // A delivery is a bundle, not a payload: manifests and PDFs ride along and must be
            // stepped over rather than tripped on.
            var manifest = archive.CreateEntry("manifest.txt");
            using var manifestWriter = new StreamWriter(manifest.Open());
            manifestWriter.Write("delivery manifest, not JSON");
        }

        return buffer.ToArray();
    }

    /// <summary>A bundle with one member that is not readable JSON, to prove one bad file costs nothing.</summary>
    public static byte[] BundleWithBrokenMember(string name)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var broken = archive.CreateEntry("truncated.json");
            using (var writer = new StreamWriter(broken.Open()))
            {
                writer.Write("{ \"Data\": [ { \"dataFieldName\": ");
            }

            var entry = archive.CreateEntry(name);
            using var good = new StreamWriter(entry.Open());
            good.Write(Read(name));
        }

        return buffer.ToArray();
    }
}
