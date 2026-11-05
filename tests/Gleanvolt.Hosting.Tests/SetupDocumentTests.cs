using System.Text.Json;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Setup;
using Gleanvolt.Infrastructure.Secrets;
using Gleanvolt.Infrastructure.Solcast;
using Gleanvolt.Web.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// #301: the one path the web wizard and the CLI save setup through. Non-secrets to the overrides
/// file, secrets to the secret store, the password hashed with a recovery key, and nothing outside
/// the allowlist.
/// </summary>
public sealed class SetupDocumentTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gleanvolt-setup-").FullName;

    private string OverridesPath => Path.Combine(_root, "pv-system.json");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ISecretStore Secrets { get; } = new InMemorySecrets();

    private SetupTarget Target(WebPasswordState? password = null, params (string Key, string? Value)[] current)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(current.Select(entry => new KeyValuePair<string, string?>(entry.Key, entry.Value)))
            .Build();

        var state = password ?? new WebPasswordState(null);
        var service = new WebPasswordService(
            state,
            new WebPasswordStore(Overrides(configuration)),
            new PasswordResetOptions(Path.Combine(_root, "code.txt")),
            NullLogger<WebPasswordService>.Instance);

        return new SetupTarget(Overrides(configuration), OverridesPath, Secrets, service);
    }

    // The configuration as the host composes it: whatever it started with, then the overrides file.
    private IConfiguration Overrides(IConfiguration current)
    {
        var manager = new ConfigurationManager();
        manager.AddConfiguration(current);
        manager[PvSystemOverrides.PathKey] = OverridesPath;
        manager.AddPvSystemOverrides(_root);
        return manager;
    }

    private static SetupDocument Document(params (string Key, string Value)[] values) =>
        SetupDocument.From(values.ToDictionary(value => value.Key, value => value.Value));

    [Fact]
    public void A_key_outside_the_allowlist_is_refused_by_name()
    {
        var problems = Document(("Web:Port", "80"), ("Pv:Inverter:Host", "192.168.1.10")).Validate(Target().Configuration);

        var problem = Assert.Single(problems);
        Assert.Contains("Web:Port", problem);
        Assert.False(File.Exists(OverridesPath));
    }

    [Fact]
    public void The_resolvers_own_rules_judge_the_values()
    {
        var problems = Document(
            ("Pv:Inverter:Host", "192.168.1.10"),
            ("Pv:Inverter:Port", "70000"),
            ("Controller:TimeZone", "Mars/Olympus_Mons"),
            ("ChargeControl:Forecast:BatteryCapacityKWh", "-3"),
            ("Web:Password", "short"),
            ("Solcast:ApiKey", "key-without-a-site"))
            .Validate(Target().Configuration);

        Assert.Contains(problems, problem => problem.Contains("Pv:Inverter:Port", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("Mars/Olympus_Mons", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("BatteryCapacityKWh", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("at least 8", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("Solcast:ResourceId", StringComparison.Ordinal));
    }

    [Fact]
    public void A_step_is_checked_against_what_was_saved_before_it()
    {
        // Devices alone: the location is not this step's business, so it is not demanded here.
        var problems = Document(("Pv:Inverter:Host", "192.168.1.10"), ("Pv:Chargers:0:Host", "192.168.1.11"))
            .Validate(Target().Configuration);

        Assert.Empty(problems);
    }

    [Fact]
    public void Apply_writes_plain_values_to_the_file_and_secrets_to_the_store()
    {
        var result = Document(
            ("Pv:Inverter:Host", "192.168.1.10"),
            ("Pv:Chargers:0:Host", "192.168.1.11"),
            ("Pv:Latitude", "50.0755"),
            ("Pv:Longitude", "14.4378"),
            ("Controller:TimeZone", "Europe/Prague"),
            ("ChargeControl:Forecast:BatteryCapacityKWh", "9"),
            ("Solcast:ApiKey", "solcast-secret"),
            ("Solcast:ResourceId", "abcd-1234"),
            ("Weather:ApiKey", "weather-secret"),
            ("Web:Password", "correct horse"))
            .Apply(Target());

        Assert.True(result.Applied, string.Join("; ", result.Problems));
        Assert.NotNull(result.RecoveryKey);

        var stored = PvSystemOverrides.Read(OverridesPath);
        Assert.Equal("192.168.1.10", stored["Pv:Inverter:Host"]);
        Assert.Equal("Europe/Prague", stored["Controller:TimeZone"]);
        Assert.Equal("9", stored["ChargeControl:Forecast:BatteryCapacityKWh"]);

        // The password as a hash, beside its recovery key; never as typed.
        Assert.True(WebPasswordHasher.Verify(stored[WebPasswordStore.Key], "correct horse"));
        Assert.True(stored.ContainsKey(WebPasswordStore.RecoveryKeyHashKey));

        var file = File.ReadAllText(OverridesPath);
        Assert.DoesNotContain("solcast-secret", file, StringComparison.Ordinal);
        Assert.DoesNotContain("correct horse", file, StringComparison.Ordinal);
        Assert.DoesNotContain("abcd-1234", file, StringComparison.Ordinal);
        Assert.DoesNotContain("weather-secret", file, StringComparison.Ordinal);
        Assert.Equal("weather-secret", Secrets.Read(SecretNames.WeatherApiKey));

        var account = JsonSerializer.Deserialize<SolcastAccount>(Secrets.Read(SecretNames.SolcastAccount)!);
        Assert.Equal(new SolcastAccount("solcast-secret", "abcd-1234"), account);
    }

    [Fact]
    public void Applied_twice_the_second_step_keeps_the_first()
    {
        Assert.True(Document(("Pv:Inverter:Host", "192.168.1.10")).Apply(Target()).Applied);
        Assert.True(Document(("Pv:Latitude", "50.0755"), ("Pv:Longitude", "14.4378")).Apply(Target()).Applied);

        var stored = PvSystemOverrides.Read(OverridesPath);
        Assert.Equal("192.168.1.10", stored["Pv:Inverter:Host"]);
        Assert.Equal("50.0755", stored["Pv:Latitude"]);
    }

    [Fact]
    public void An_empty_value_removes_the_key_so_dot_env_applies_again()
    {
        Assert.True(Document(("Pv:Name", "Home Roof")).Apply(Target()).Applied);
        Assert.True(Document(("Pv:Name", "")).Apply(Target()).Applied);

        Assert.False(File.Exists(OverridesPath) && PvSystemOverrides.Read(OverridesPath).ContainsKey("Pv:Name"));
    }

    [Fact]
    public void A_refused_document_writes_nothing()
    {
        var result = Document(("Pv:Inverter:Host", "192.168.1.10"), ("Pv:Inverter:Port", "0")).Apply(Target());

        Assert.False(result.Applied);
        Assert.False(File.Exists(OverridesPath));
    }

    [Fact]
    public void The_password_is_set_once_and_never_overwritten_by_a_setup()
    {
        var password = new WebPasswordState(WebPasswordHasher.Hash("already chosen"));

        var result = Document(("Web:Password", "something else")).Apply(Target(password));

        Assert.False(result.Applied);
        Assert.True(WebPasswordHasher.Verify(password.Hash, "already chosen"));
    }

    [Fact]
    public void Json_in_the_shape_of_appsettings_is_read_as_flat_keys()
    {
        var document = SetupDocument.FromJson("""
            {
              "Pv": { "Inverter": { "Host": "192.168.1.10" }, "Chargers": [ { "Host": "192.168.1.11" } ] },
              "Controller": { "TimeZone": "Europe/Prague" }
            }
            """);

        Assert.Equal("192.168.1.10", document.Values["Pv:Inverter:Host"]);
        Assert.Equal("192.168.1.11", document.Values["Pv:Chargers:0:Host"]);
        Assert.Empty(document.Validate(Target().Configuration));
    }

    [Fact]
    public void Malformed_json_is_a_format_problem()
    {
        Assert.Throws<FormatException>(() => SetupDocument.FromJson("{ \"Pv\": "));
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

        public bool Delete(string name) => _values.Remove(name);

        public string Describe() => "memory";
    }
}
