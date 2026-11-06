using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Discovery;
using Gleanvolt.Hosting.Cli;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Infrastructure.Secrets;
using Gleanvolt.Web.Auth;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// #303: the command-line route. A file is checked and applied through the same document the wizard
/// uses, with every problem listed and the exit code to match; the prompts walk the wizard's steps on
/// redirected input; and the safeguards -- the service user, a world-readable file of secrets, DPAPI's
/// one user -- stop or warn before anything is written.
/// </summary>
public sealed class CliTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gleanvolt-cli-").FullName;
    private readonly InMemorySecrets _secrets = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _error = new();
    private readonly HashSet<string> _worldReadable = [];
    private IReadOnlyList<DiscoveredDevice> _found = [];

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string OverridesPath => Path.Combine(_root, "pv-system.json");

    private CliContext Context(string input = "", string? serviceUser = null, string userName = "martin", bool windowsDpapi = false,
        params (string Key, string Value)[] current)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(current.Select(entry => new KeyValuePair<string, string?>(entry.Key, entry.Value)));
        configuration[PvSystemOverrides.PathKey] = OverridesPath;
        configuration.AddPvSystemOverrides(_root);
        ((IConfigurationBuilder)configuration).Add(new IntegrationSecretsSource(_secrets));

        var platform = new CliPlatform(
            serviceUser,
            userName,
            windowsDpapi,
            windowsDpapi ? SecretStoreKind.Dpapi : SecretStoreKind.File,
            path => _worldReadable.Contains(path),
            _ => Task.FromResult(_found));

        return new CliContext(configuration, _root, new CliConsole(new StringReader(input), _out, _error), platform, _secrets);
    }

    private string File(string json)
    {
        var path = Path.Combine(_root, "site.json");
        System.IO.File.WriteAllText(path, json);
        return path;
    }

    private static Task<int> Run(CliContext context, params string[] args) =>
        GleanvoltCli.RunAsync(args, context, CancellationToken.None);

    private const string Site = """
        {
          "Pv": { "Id": "home-roof", "Latitude": 50.0755, "Longitude": 14.4378,
                  "Inverter": { "Host": "192.168.1.10" }, "Chargers": [ { "Host": "192.168.1.11" } ] },
          "Controller": { "TimeZone": "Europe/Prague" },
          "Web": { "Password": "correct horse" },
          "Solcast": { "ApiKey": "solcast-secret", "ResourceId": "abcd-1234" },
          "HomeAssistant": { "Enabled": true, "BrokerHost": "192.168.1.5", "Password": "mqtt-secret" },
          "Api": { "Enabled": true, "Keys": { "claude": "abcdefghijklmnopqrstuvwx" } }
        }
        """;

    [Fact]
    public void Only_the_setup_commands_are_commands()
    {
        Assert.True(GleanvoltCli.IsCommand(["setup", "--file", "x"]));
        Assert.True(GleanvoltCli.IsCommand(["discover", "--json"]));
        Assert.True(GleanvoltCli.IsCommand(["reset-password"]));
        Assert.False(GleanvoltCli.IsCommand([]));
        Assert.False(GleanvoltCli.IsCommand(["--urls", "http://*:8090"]));
        Assert.False(GleanvoltCli.IsCommand(["hash-password", "x"]));
    }

    [Fact]
    public async Task A_file_is_applied_plain_values_to_the_file_secrets_to_the_store()
    {
        var path = File(Site);

        Assert.Equal(0, await Run(Context(), "setup", "--file", path));

        var stored = PvSystemOverrides.Read(OverridesPath);
        Assert.Equal("192.168.1.10", stored["Pv:Inverter:Host"]);
        Assert.Equal("192.168.1.5", stored["HomeAssistant:BrokerHost"]);
        Assert.True(WebPasswordHasher.Verify(stored[WebPasswordStore.Key], "correct horse"));

        var file = System.IO.File.ReadAllText(OverridesPath);
        Assert.DoesNotContain("solcast-secret", file, StringComparison.Ordinal);
        Assert.DoesNotContain("mqtt-secret", file, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwx", file, StringComparison.Ordinal);
        Assert.Equal("mqtt-secret", _secrets.Read(SecretNames.MqttPassword));
        Assert.Equal("abcdefghijklmnopqrstuvwx", IntegrationSecrets.ReadApiKeys(_secrets)["claude"]);
        Assert.NotNull(_secrets.Read(SecretNames.SolcastAccount));

        var output = _out.ToString();
        Assert.Contains("Recovery key:", output, StringComparison.Ordinal);
        Assert.Contains("Restart Gleanvolt", output, StringComparison.Ordinal);
        Assert.Contains("still holds them", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_problem_is_listed_and_nothing_is_written()
    {
        var path = File("""
            {
              "Pv": { "Latitude": 123 },
              "Controller": { "TimeZone": "Mars/Olympus" },
              "Web": { "Password": "short" },
              "Wallbox": { "Colour": "red" }
            }
            """);

        Assert.Equal(1, await Run(Context(), "setup", "--file", path));
        Assert.Contains("Wallbox:Colour is not a setting", _error.ToString(), StringComparison.Ordinal);
        Assert.False(System.IO.File.Exists(OverridesPath));

        // Without the unknown key, the rest are all listed at once.
        _error.GetStringBuilder().Clear();
        File("""{ "Pv": { "Latitude": 123 }, "Controller": { "TimeZone": "Mars/Olympus" }, "Web": { "Password": "short" } }""");

        Assert.Equal(1, await Run(Context(), "setup", "--file", path));

        var errors = _error.ToString();
        Assert.Contains("problems:", errors, StringComparison.Ordinal);
        Assert.Contains("Mars/Olympus", errors, StringComparison.Ordinal);
        Assert.Contains("at least 8 characters", errors, StringComparison.Ordinal);
        Assert.False(System.IO.File.Exists(OverridesPath));
    }

    [Fact]
    public async Task Validate_checks_and_writes_nothing()
    {
        var path = File(Site);

        Assert.Equal(0, await Run(Context(), "setup", "--validate", path));

        Assert.Contains("is valid", _out.ToString(), StringComparison.Ordinal);
        Assert.False(System.IO.File.Exists(OverridesPath));
        Assert.Null(_secrets.Read(SecretNames.MqttPassword));
    }

    [Fact]
    public async Task Malformed_json_is_the_files_problem()
    {
        var path = File("{ \"Pv\": ");

        Assert.Equal(1, await Run(Context(), "setup", "--validate", path));
        Assert.Contains("not valid JSON", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_world_readable_file_of_secrets_is_refused()
    {
        var path = File(Site);
        _worldReadable.Add(path);

        Assert.Equal(1, await Run(Context(), "setup", "--file", path));

        Assert.Contains("chmod 600", _error.ToString(), StringComparison.Ordinal);
        Assert.False(System.IO.File.Exists(OverridesPath));
    }

    [Fact]
    public async Task A_world_readable_file_without_secrets_is_fine()
    {
        var path = File("""{ "Pv": { "Inverter": { "Host": "192.168.1.10" } } }""");
        _worldReadable.Add(path);

        Assert.Equal(0, await Run(Context(), "setup", "--file", path));
    }

    [Theory]
    [InlineData("setup", "--file")]
    [InlineData("setup", null)]
    [InlineData("set-password", null)]
    public async Task Under_the_deb_anyone_but_the_service_user_is_refused(string command, string? flag)
    {
        var path = File(Site);
        string[] args = flag is null ? [command] : [command, flag, path];

        Assert.Equal(1, await Run(Context("new password\nnew password\n", serviceUser: "gleanvolt", userName: "root"), args));

        Assert.Contains("sudo -u gleanvolt", _error.ToString(), StringComparison.Ordinal);
        Assert.False(System.IO.File.Exists(OverridesPath));
    }

    [Fact]
    public async Task The_service_user_is_let_through_and_told_how_to_restart()
    {
        var path = File("""{ "Pv": { "Inverter": { "Host": "192.168.1.10" } } }""");

        Assert.Equal(0, await Run(Context(serviceUser: "gleanvolt", userName: "gleanvolt"), "setup", "--file", path));
        Assert.Contains("systemctl restart gleanvolt", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dpapi_secrets_are_said_to_be_this_users()
    {
        var path = File(Site);

        Assert.Equal(0, await Run(Context(userName: "martin", windowsDpapi: true), "setup", "--file", path));
        Assert.Contains("sealed to the Windows user martin", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_password_asks_twice_and_prints_a_recovery_key()
    {
        var context = Context("short\nnew password 1\nnew password 2\nnew password 1\nnew password 1\n",
            current: (WebPasswordStore.Key, WebPasswordHasher.Hash("old password")));

        Assert.Equal(0, await Run(context, "reset-password"));

        var errors = _error.ToString();
        Assert.Contains("Too short", errors, StringComparison.Ordinal);
        Assert.Contains("do not match", errors, StringComparison.Ordinal);
        Assert.True(WebPasswordHasher.Verify(PvSystemOverrides.Read(OverridesPath)[WebPasswordStore.Key], "new password 1"));
        Assert.Matches(@"Recovery key: [A-Z0-9]{5}(-[A-Z0-9]{5}){4}", _out.ToString());
    }

    [Fact]
    public async Task Set_password_with_no_input_sets_nothing()
    {
        Assert.Equal(1, await Run(Context(""), "set-password"));
        Assert.False(System.IO.File.Exists(OverridesPath));
    }

    [Fact]
    public async Task The_prompts_follow_the_wizard_and_take_a_found_device()
    {
        _found =
        [
            new DiscoveredDevice("192.168.1.10", 502, 1, DiscoveryFinding.SolaxDevice, PvDeviceRole.Inverter, "X3-HYB-G4 PRO", null, "H34", SupportTier.Verified, true, ""),
            new DiscoveredDevice("192.168.1.11", 502, 3, DiscoveryFinding.SolaxDevice, PvDeviceRole.Charger, "X3-HAC", null, "C31", SupportTier.Verified, true, ""),
        ];

        // Password twice; search yes; take both suggestions; the site; skip the optional ones; save.
        var input = string.Join('\n',
            "correct horse", "correct horse", "y", "", "", "50.0755", "14.4378", "Europe/Prague", "9", "", "", "y") + "\n";

        Assert.Equal(0, await Run(Context(input), "setup"));

        var stored = PvSystemOverrides.Read(OverridesPath);
        Assert.Equal("192.168.1.10", stored["Pv:Inverter:Host"]);
        Assert.Equal("192.168.1.11", stored["Pv:Chargers:0:Host"]);
        Assert.Equal("3", stored["Pv:Chargers:0:UnitId"]);
        Assert.Equal("9", stored["ChargeControl:Forecast:BatteryCapacityKWh"]);
        Assert.Contains("X3-HAC", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("Recovery key:", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_swapped_choice_is_said_and_allowed()
    {
        _found =
        [
            new DiscoveredDevice("192.168.1.10", 502, 1, DiscoveryFinding.SolaxDevice, PvDeviceRole.Inverter, "X3-HYB-G4 PRO", null, null, SupportTier.Verified, true, ""),
        ];

        // A password exists, so no password prompt. The inverter's address typed as the charger.
        var input = string.Join('\n', "y", "", "192.168.1.10", "50", "14", "Europe/Prague", "", "", "", "y") + "\n";

        Assert.Equal(0, await Run(Context(input, current: (WebPasswordStore.Key, "hash")), "setup"));

        Assert.Contains("identified itself as the inverter", _out.ToString(), StringComparison.Ordinal);
        Assert.Equal("192.168.1.10", PvSystemOverrides.Read(OverridesPath)["Pv:Chargers:0:Host"]);
    }

    [Fact]
    public async Task Input_that_ends_early_saves_nothing()
    {
        Assert.Equal(1, await Run(Context("n\n192.168.1.10\n", current: (WebPasswordStore.Key, "hash")), "setup"));

        Assert.Contains("nothing was saved", _error.ToString(), StringComparison.Ordinal);
        Assert.False(System.IO.File.Exists(OverridesPath));
    }

    [Fact]
    public async Task The_schema_is_json_in_the_shape_of_appsettings()
    {
        Assert.Equal(0, await Run(Context(), "setup", "--print-schema"));

        using var schema = JsonDocument.Parse(_out.ToString());
        var properties = schema.RootElement.GetProperty("properties");

        Assert.Equal("array", properties.GetProperty("Pv").GetProperty("properties").GetProperty("Chargers").GetProperty("type").GetString());
        Assert.True(properties.GetProperty("Web").GetProperty("properties").GetProperty("Password").GetProperty("writeOnly").GetBoolean());
        Assert.Equal(JsonValueKind.Object,
            properties.GetProperty("Api").GetProperty("properties").GetProperty("Keys").GetProperty("additionalProperties").ValueKind);
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task An_unknown_command_shape_prints_the_usage()
    {
        Assert.Equal(2, await Run(Context(), "setup", "--frobnicate"));
        Assert.Contains("setup --file", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_overrides_lock_holds_off_a_second_writer_until_released()
    {
        var path = Path.Combine(_root, "locked.json");

        // Another process's lock, as the file sees it.
        var other = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var acquired = new ManualResetEventSlim();

        var writer = Task.Run(() =>
        {
            using (PvSystemOverrides.Lock(path))
            {
                acquired.Set();
            }
        });

        Assert.False(acquired.Wait(TimeSpan.FromMilliseconds(300)));

        other.Dispose();

        Assert.True(acquired.Wait(TimeSpan.FromSeconds(5)));
        writer.Wait();
    }

    [Fact]
    public void The_debs_environment_is_loaded_only_for_the_installed_binary()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var variable = $"GLEANVOLT_TEST_{Guid.NewGuid():N}";
        var files = new Dictionary<string, string[]>
        {
            [DebEnvironment.UnitFile] = ["[Service]", "User=gleanvolt", $"Environment={variable}=/var/lib/gleanvolt/pv-system.json"],
            [DebEnvironment.EnvironmentFile] = ["# comment", $"{variable}_2=\"quoted\""],
        };

        try
        {
            Assert.Null(DebEnvironment.Load("/usr/bin/dotnet", files.ContainsKey, path => files[path]));
            Assert.Null(Environment.GetEnvironmentVariable(variable));

            Assert.Equal("gleanvolt", DebEnvironment.Load("/opt/gleanvolt/Gleanvolt.Worker", files.ContainsKey, path => files[path]));
            Assert.Equal("/var/lib/gleanvolt/pv-system.json", Environment.GetEnvironmentVariable(variable));
            Assert.Equal("quoted", Environment.GetEnvironmentVariable(variable + "_2"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            Environment.SetEnvironmentVariable(variable + "_2", null);
        }
    }

    private sealed class InMemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = [];

        public string? Read(string name) => _values.GetValueOrDefault(name);

        public bool Write(string name, string value)
        {
            _values[name] = value;
            return true;
        }

        public bool Delete(string name) => _values.Remove(name) || true;

        public string Describe() => "memory";
    }
}
