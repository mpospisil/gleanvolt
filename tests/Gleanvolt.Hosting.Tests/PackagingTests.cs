using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The .deb package (issue #205) runs the controller with its content root in a read-only
/// <c>/opt/gleanvolt</c>, so every file the controller writes has to be pointed somewhere writable by
/// the systemd unit — and the unit is a hand-written list, which is the kind of thing that falls behind
/// the code. A new <c>…Path</c> setting in <c>appsettings.json</c> with no line in the unit would work
/// in Docker and under <c>dotnet run</c>, and fail only on an installed machine, at the moment the
/// feature first tries to write.
///
/// <para>Like <see cref="DockerfileTests"/>, these read files rather than build anything: the answer
/// is knowable from the repository without Docker, systemd or a package.</para>
/// </summary>
public class PackagingTests
{
    private static readonly string Root = FindRepositoryRoot();

    private static readonly string[] WritableRoots = ["/var/lib/gleanvolt/", "/var/log/gleanvolt/"];

    [Fact]
    public void EveryPathSettingIsPointedAtAWritableDirectoryByTheSystemdUnit()
    {
        var unit = File.ReadAllText(Path.Combine(Root, "packaging", "linux", "gleanvolt.service"));
        var keys = PathSettings().ToList();

        Assert.NotEmpty(keys);

        foreach (var key in keys)
        {
            var match = Regex.Match(unit, $@"^Environment={Regex.Escape(key)}=(\S+)$", RegexOptions.Multiline);

            Assert.True(
                match.Success,
                $"appsettings.json has a path setting {key}, and packaging/linux/gleanvolt.service does not "
                + $"set it. On an installed machine it resolves under the read-only /opt/gleanvolt. Add "
                + $"`Environment={key}=/var/lib/gleanvolt/…`.");

            // A file inside a writable root, or the root itself: the secret store is named by its
            // directory, and /var/lib/gleanvolt is the directory it belongs in.
            Assert.True(
                WritableRoots.Any(root =>
                    match.Groups[1].Value.StartsWith(root, StringComparison.Ordinal)
                    || match.Groups[1].Value == root.TrimEnd('/')),
                $"gleanvolt.service sets {key} to {match.Groups[1].Value}, which is not under "
                + $"{string.Join(" or ", WritableRoots)} -- the only directories the unit makes writable.");
        }
    }

    [Fact]
    public void EveryFileTheNfpmDefinitionPackagesExists()
    {
        var definition = File.ReadAllText(Path.Combine(Root, "packaging", "linux", "nfpm.yaml"));
        var sources = Regex.Matches(definition, @"^\s+(?:-\s+src|postinstall|preremove|postremove):\s+(\S+)\s*$", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            // The publish output is produced by the workflow, not checked in.
            .Where(source => !source.StartsWith("publish/", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(sources);

        foreach (var source in sources)
        {
            Assert.True(
                File.Exists(Path.Combine(Root, source)),
                $"packaging/linux/nfpm.yaml packages {source}, which does not exist; nfpm would fail the release.");
        }
    }

    /// <summary>
    /// The package is the controller and its web UI, nothing else: no broker is installed, so nothing
    /// shipped may switch on a feature that needs one. A commented example is fine; a live line is not.
    /// </summary>
    [Theory]
    [InlineData("gleanvolt.service")]
    [InlineData("gleanvolt.env")]
    public void ThePackageSwitchesOnNothingThatNeedsAnMqttBroker(string file)
    {
        var content = File.ReadAllText(Path.Combine(Root, "packaging", "linux", file));
        var live = content.Split('\n')
            .Select(line => line.Trim())
            .Where(line => !line.StartsWith('#'))
            .Where(line => line.Contains("HomeAssistant__", StringComparison.Ordinal)
                || line.Contains("Vehicle__Enabled", StringComparison.Ordinal))
            .ToList();

        Assert.True(live.Count == 0, $"packaging/linux/{file} configures MQTT: {string.Join("; ", live)}");
    }

    /// <summary>
    /// Every string setting in the Worker's appsettings.json whose name ends in "path" or
    /// "directory", as the environment-variable key that overrides it — <c>SessionStore__Path</c>,
    /// <c>Serilog__WriteTo__1__Args__path</c>, <c>Secrets__Directory</c>.
    ///
    /// <para>"Directory" is in for the secret store (issue #215), which is named by the folder its
    /// files live in rather than by a file. It writes the same way, in the same read-only content
    /// root, and the failure it would cause is the same one.</para>
    /// </summary>
    private static IEnumerable<string> PathSettings()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(Root, "src", "Gleanvolt.Worker", "appsettings.json")));

        return Walk(document.RootElement, []).ToList();

        static IEnumerable<string> Walk(JsonElement element, List<string> keys)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        foreach (var key in Walk(property.Value, [.. keys, property.Name]))
                        {
                            yield return key;
                        }
                    }
                    break;

                case JsonValueKind.Array:
                    var index = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        foreach (var key in Walk(item, [.. keys, (index++).ToString(System.Globalization.CultureInfo.InvariantCulture)]))
                        {
                            yield return key;
                        }
                    }
                    break;

                case JsonValueKind.String when keys[^1].EndsWith("path", StringComparison.OrdinalIgnoreCase)
                        || keys[^1].EndsWith("directory", StringComparison.OrdinalIgnoreCase):
                    yield return string.Join("__", keys);
                    break;
            }
        }
    }

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
