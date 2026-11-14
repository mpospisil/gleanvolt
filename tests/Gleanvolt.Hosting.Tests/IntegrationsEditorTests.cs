using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Configuration;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The Home Assistant link and the API edited on Settings, against real configuration providers. What
/// these guard: a save the next start would refuse writes nothing; plain settings go to the overrides
/// file and secrets to the secret store, never the other way; the next start reads what was saved, the
/// page's secrets over the environment's; and the running side never moves.
/// </summary>
public sealed class IntegrationsEditorTests : IDisposable
{
    private readonly string _prefix = $"GLEANVOLT_TEST_{Guid.NewGuid():N}_";
    private readonly string _root = Directory.CreateTempSubdirectory("gleanvolt-integrations-").FullName;
    private readonly List<string> _variables = [];
    private readonly InMemorySecrets _secrets = new();

    private readonly Dictionary<string, string?> _appSettings = new()
    {
        ["HomeAssistant:Enabled"] = "false",
        ["HomeAssistant:BrokerHost"] = "localhost",
        ["HomeAssistant:BrokerPort"] = "1883",
        ["Api:Enabled"] = "false",
    };

    public void Dispose()
    {
        foreach (var variable in _variables)
        {
            Environment.SetEnvironmentVariable(variable, null);
        }

        Directory.Delete(_root, recursive: true);
    }

    private string OverridesPath => Path.Combine(_root, "data", "pv-system.json");

    private void SetEnvironment(string key, string value)
    {
        var variable = _prefix + key.Replace(":", "__", StringComparison.Ordinal);
        _variables.Add(variable);
        Environment.SetEnvironmentVariable(variable, value);
    }

    // The host's order: appsettings, environment, the overrides file, the page's secrets.
    private ConfigurationManager Configuration()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(_appSettings);
        configuration.AddEnvironmentVariables(_prefix);
        configuration.AddPvSystemOverrides(_root);
        ((IConfigurationBuilder)configuration).Add(new IntegrationSecretsSource(_secrets));
        return configuration;
    }

    private IntegrationsEditor Editor(IConfiguration configuration) =>
        new(configuration, IntegrationsSnapshot.From(configuration), _secrets, NullLogger<IntegrationsEditor>.Instance);

    [Fact]
    public void Home_assistant_on_without_a_broker_or_an_id_is_refused_and_writes_nothing()
    {
        // No broker anywhere below the page, so an empty field leaves none (#396).
        _appSettings.Remove(IntegrationSettingKeys.MqttBrokerHost);
        var editor = Editor(Configuration());

        var result = editor.Save(new Dictionary<string, string>
        {
            [IntegrationSettingKeys.MqttEnabled] = "true",
            [IntegrationSettingKeys.MqttBrokerHost] = "",
        });

        Assert.False(result.Saved);
        Assert.Contains(result.Problems, problem => problem.Contains("broker's address", StringComparison.Ordinal));
        Assert.Contains(result.Problems, problem => problem.Contains("installation's id", StringComparison.Ordinal));
        Assert.False(File.Exists(OverridesPath));
    }

    [Fact]
    public void A_bad_port_is_refused()
    {
        _appSettings["Pv:Id"] = "home-roof";

        var result = Editor(Configuration()).Save(new Dictionary<string, string>
        {
            [IntegrationSettingKeys.MqttEnabled] = "true",
            [IntegrationSettingKeys.MqttBrokerPort] = "70000",
        });

        Assert.Contains(result.Problems, problem => problem.Contains("70000", StringComparison.Ordinal));
    }

    [Fact]
    public void A_saved_link_goes_to_the_file_its_password_to_the_store_and_both_wait_for_a_restart()
    {
        _appSettings["Pv:Id"] = "home-roof";
        var editor = Editor(Configuration());

        var result = editor.Save(
            new Dictionary<string, string>
            {
                [IntegrationSettingKeys.MqttEnabled] = "true",
                [IntegrationSettingKeys.MqttBrokerHost] = "192.168.1.5",
                [IntegrationSettingKeys.MqttUsername] = "gleanvolt",
            },
            mqttPassword: "s3cret");

        Assert.True(result.Saved, string.Join("; ", result.Problems));

        var stored = PvSystemOverrides.Read(OverridesPath);
        Assert.Equal("true", stored[IntegrationSettingKeys.MqttEnabled]);
        Assert.Equal("192.168.1.5", stored[IntegrationSettingKeys.MqttBrokerHost]);
        Assert.DoesNotContain("s3cret", File.ReadAllText(OverridesPath), StringComparison.Ordinal);
        Assert.Equal("s3cret", _secrets.Read(SecretNames.MqttPassword));

        var settings = editor.Read();
        Assert.True(settings[IntegrationSettingKeys.MqttBrokerHost].IsPending);
        Assert.Equal("localhost", settings[IntegrationSettingKeys.MqttBrokerHost].Running);
        Assert.Equal(new SecretSettingState(true, SettingSource.SecretStore, true), settings.MqttPassword);

        // The next start reads all of it.
        var next = Configuration();
        Assert.Equal("192.168.1.5", next[IntegrationSettingKeys.MqttBrokerHost]);
        Assert.Equal("s3cret", next[IntegrationSettingKeys.MqttPassword]);
    }

    [Fact]
    public void The_pages_password_wins_over_the_environments_and_removing_it_brings_that_back()
    {
        SetEnvironment(IntegrationSettingKeys.MqttPassword, "from-env");
        var editor = Editor(Configuration());

        Assert.Equal(new SecretSettingState(true, SettingSource.Environment, false), editor.Read().MqttPassword);

        editor.Save(new Dictionary<string, string>(), "from-page");
        Assert.Equal("from-page", Configuration()[IntegrationSettingKeys.MqttPassword]);

        editor.Save(new Dictionary<string, string>(), "");
        Assert.Equal("from-env", Configuration()[IntegrationSettingKeys.MqttPassword]);
        Assert.Null(_secrets.Read(SecretNames.MqttPassword));
    }

    [Fact]
    public void The_api_needs_a_key_before_it_can_be_turned_on()
    {
        var editor = Editor(Configuration());
        var on = new Dictionary<string, string> { [IntegrationSettingKeys.ApiEnabled] = "true" };

        Assert.Contains(editor.Save(on).Problems, problem => problem.Contains("without a key", StringComparison.Ordinal));

        var created = editor.CreateApiKey("claude");
        Assert.True(created.Result.Saved);
        Assert.Matches("^[A-Za-z0-9]{24}$", created.Key!);

        Assert.True(editor.Save(on).Saved);

        var key = Assert.Single(editor.Read().ApiKeys);
        Assert.Equal(new ApiKeyEntry("claude", created.Key!, SettingSource.SecretStore, IsPending: true), key);
        Assert.Equal(created.Key, Configuration()["Api:Keys:claude"]);

        // The last key of an API that is on: the next start would refuse it.
        Assert.False(editor.RemoveApiKey("claude").Saved);
        Assert.NotNull(_secrets.Read(SecretNames.ApiKeys));
    }

    [Fact]
    public void The_environments_keys_are_listed_but_not_removable_here()
    {
        SetEnvironment("Api:Keys:mcp", "env-key");
        var editor = Editor(Configuration());

        var key = Assert.Single(editor.Read().ApiKeys);
        Assert.Equal(new ApiKeyEntry("mcp", "env-key", SettingSource.Environment, IsPending: false), key);
        Assert.False(editor.RemoveApiKey("mcp").Saved);
        Assert.False(editor.CreateApiKey("MCP").Result.Saved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("-leading-dash")]
    public void A_key_name_must_be_a_plain_word(string name)
    {
        var created = Editor(Configuration()).CreateApiKey(name);

        Assert.False(created.Result.Saved);
        Assert.Null(created.Key);
    }

    [Fact]
    public void A_removed_key_is_gone_from_the_next_start()
    {
        var editor = Editor(Configuration());
        editor.CreateApiKey("one");
        editor.CreateApiKey("two");

        Assert.True(editor.RemoveApiKey("one").Saved);

        Assert.Null(Configuration()["Api:Keys:one"]);
        Assert.NotNull(Configuration()["Api:Keys:two"]);
    }

    [Fact]
    public void Only_the_editors_own_keys_are_accepted()
    {
        var result = Editor(Configuration()).Save(new Dictionary<string, string> { ["Web:Port"] = "80" });

        Assert.False(result.Saved);
    }

    [Fact]
    public async Task A_broker_that_does_not_answer_is_said_rather_than_thrown()
    {
        // A port nothing listens on: bound, read, and closed again.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var result = await Editor(Configuration()).TestMqttAsync("127.0.0.1", port, "user", "pw");

        Assert.False(result.Connected);
        Assert.Contains($"127.0.0.1:{port}", result.Message, StringComparison.Ordinal);
    }

    // The save from #396: publishing switched off while the broker came from the environment, with the
    // form's fields empty. Only the switch may reach the file, and the next start must bind.
    [Fact]
    public void Empty_fields_are_not_stored_so_the_environment_still_applies()
    {
        _appSettings["Pv:Id"] = "home-roof";
        SetEnvironment(IntegrationSettingKeys.MqttEnabled, "true");
        SetEnvironment(IntegrationSettingKeys.MqttBrokerHost, "localhost");
        SetEnvironment(IntegrationSettingKeys.MqttBrokerPort, "1883");
        SetEnvironment(IntegrationSettingKeys.MqttUsername, "solax");

        var result = Editor(Configuration()).Save(new Dictionary<string, string>
        {
            [IntegrationSettingKeys.MqttEnabled] = "false",
            [IntegrationSettingKeys.MqttBrokerHost] = "",
            [IntegrationSettingKeys.MqttBrokerPort] = "",
            [IntegrationSettingKeys.MqttUsername] = "",
            [IntegrationSettingKeys.MqttDiscoveryPrefix] = " ",
        });

        Assert.True(result.Saved, string.Join("; ", result.Problems));
        var stored = PvSystemOverrides.Read(OverridesPath);
        Assert.Equal(new Dictionary<string, string> { [IntegrationSettingKeys.MqttEnabled] = "false" }, stored);

        var next = Configuration();
        Assert.Equal("1883", next[IntegrationSettingKeys.MqttBrokerPort]);
        Assert.Equal("solax", next[IntegrationSettingKeys.MqttUsername]);
        var options = next.GetSection(HomeAssistantOptions.SectionName).Get<HomeAssistantOptions>()!;
        Assert.False(options.Enabled);
        Assert.Equal(1883, options.BrokerPort);
    }

    [Fact]
    public void A_save_removes_the_empty_values_an_earlier_version_left_in_the_file()
    {
        WriteOverrides("""{"HomeAssistant":{"BrokerHost":"","BrokerPort":"","DiscoveryPrefix":"","Enabled":"false","Username":""}}""");

        var result = Editor(Configuration()).Save(new Dictionary<string, string>
        {
            [IntegrationSettingKeys.MqttBrokerHost] = "192.168.1.5",
        });

        Assert.True(result.Saved, string.Join("; ", result.Problems));
        Assert.Equal(
            new Dictionary<string, string>
            {
                [IntegrationSettingKeys.MqttEnabled] = "false",
                [IntegrationSettingKeys.MqttBrokerHost] = "192.168.1.5",
            },
            PvSystemOverrides.Read(OverridesPath));
    }

    // A file 1.2.0 wrote is read past, not allowed to stop the start: the environment or the default
    // applies, and the startup log says which keys were left out.
    [Fact]
    public void The_next_start_ignores_empty_values_an_earlier_version_left_in_the_file()
    {
        SetEnvironment(IntegrationSettingKeys.MqttUsername, "solax");
        WriteOverrides("""{"HomeAssistant":{"BrokerPort":"","Enabled":"false","Username":""},"Pv":{"Address":""}}""");

        var configuration = Configuration();

        var options = configuration.GetSection(HomeAssistantOptions.SectionName).Get<HomeAssistantOptions>()!;
        Assert.Equal(1883, options.BrokerPort);
        Assert.Equal("solax", options.Username);
        Assert.False(options.Enabled);

        var provider = PvSystemOverrides.Find(configuration)!;
        Assert.Equal(
            [IntegrationSettingKeys.MqttBrokerPort, IntegrationSettingKeys.MqttUsername],
            provider.IgnoredEmptyKeys.Order(StringComparer.Ordinal));
        Assert.Contains("HomeAssistant:BrokerPort", PvSystemOverrides.DescribeIgnored(configuration), StringComparison.Ordinal);
        Assert.DoesNotContain("HomeAssistant:BrokerPort", PvSystemOverrides.DescribeLoaded(configuration), StringComparison.Ordinal);

        // Outside these keys an empty value keeps its meaning: it blanks what is below it.
        Assert.Equal(string.Empty, configuration["Pv:Address"]);
    }

    [Fact]
    public void A_file_without_empty_values_has_nothing_to_say()
    {
        WriteOverrides("""{"HomeAssistant":{"Enabled":"false"}}""");

        Assert.Null(PvSystemOverrides.DescribeIgnored(Configuration()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    public void A_port_set_to_anything_but_a_port_is_refused_with_home_assistant_off(string port)
    {
        // In appsettings rather than the environment: an empty environment variable is no variable.
        _appSettings[IntegrationSettingKeys.MqttBrokerPort] = port;

        var result = Editor(Configuration()).Save(new Dictionary<string, string>
        {
            [IntegrationSettingKeys.MqttDiscoveryPrefix] = "ha",
        });

        Assert.False(result.Saved);
        Assert.Contains(result.Problems, problem => problem.Contains("1 to 65535", StringComparison.Ordinal));
    }

    private void WriteOverrides(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OverridesPath)!);
        File.WriteAllText(OverridesPath, json);
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
