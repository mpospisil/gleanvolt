using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Configuration;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The car edited from the web UI (issue #214), against real configuration providers: an in-memory
/// stand-in for appsettings.json, real environment variables, and the same overrides file #204 writes.
///
/// <para>What these guard: only allowlisted keys are written, a feed switch disables the feed it leaves in
/// the same save, and a save the startup rules would refuse writes nothing.</para>
/// </summary>
public sealed class EvEditorTests : IDisposable
{
    // Environment variables are process-wide, so each test gets a prefix nobody else uses.
    private readonly string _prefix = $"GLEANVOLT_TEST_{Guid.NewGuid():N}_";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gleanvolt-ev-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _variables = [];

    private static readonly Dictionary<string, string?> AppSettings = new()
    {
        ["Ev:Vehicles:0:Id"] = "id4",
        ["Ev:Vehicles:0:Name"] = "The ID.4",
        ["Ev:Vehicles:0:BatteryCapacityKWh"] = "77",
        ["Ev:Vehicles:0:Phases"] = "3",
    };

    public EvEditorTests() => Directory.CreateDirectory(_root);

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
        var variable = _prefix + EvSettingKeys.EnvironmentVariable(key);
        _variables.Add(variable);
        Environment.SetEnvironmentVariable(variable, value);
    }

    private void WriteFile(params (string Key, string Value)[] values) =>
        PvSystemOverrides.Write(OverridesPath, values.ToDictionary(value => value.Key, value => value.Value));

    private Dictionary<string, string> File() => PvSystemOverrides.Read(OverridesPath);

    // Composed in the host's order: appsettings, environment, the overrides file.
    private ConfigurationManager Configuration()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(AppSettings);
        configuration.AddEnvironmentVariables(_prefix);
        configuration.AddPvSystemOverrides(_root);
        return configuration;
    }

    private EvEditor Editor(IConfiguration configuration, int minAmps = 6, int maxAmps = 16) =>
        new(
            configuration,
            EvEditor.Snapshot(configuration),
            minAmps,
            maxAmps,
            NullLogger<EvEditor>.Instance);

    private EvEditor Editor() => Editor(Configuration());

    // -- The allowlist.

    [Fact]
    public void A_key_that_is_not_on_the_list_is_refused()
    {
        var result = Editor().Save(new Dictionary<string, string>
        {
            ["Ev:Vehicles:1:Id"] = "the-other-one",
        });

        Assert.False(result.Saved);
        Assert.Contains("cannot be edited", Assert.Single(result.Problems));
        Assert.Empty(File());
    }

    [Fact]
    public void The_house_brokers_own_password_is_not_this_pages_to_write()
    {
        var result = Editor().Save(new Dictionary<string, string> { ["Vehicle:Password"] = "hunter2" });

        Assert.False(result.Saved);
        Assert.Empty(File());
    }

    [Theory]
    [InlineData("Vehicle:Website:Password")]
    [InlineData("Vehicle:Skoda:Enabled")]
    [InlineData("Vehicle:DataAct:Vin")]
    public void A_removed_built_in_feeds_key_is_refused(string key)
    {
        // #477: a car plugin keeps the account on its own page; this page writes none of it.
        var result = Editor().Save(new Dictionary<string, string> { [key] = "true" });

        Assert.False(result.Saved);
        Assert.Empty(File());
    }

    [Fact]
    public void Every_listed_key_round_trips()
    {
        var editor = Editor();

        // Values a resolver will accept for each: the point is the round trip, not the numbers.
        var values = EvSettingKeys.All
            .ToDictionary(key => key, Plausible, StringComparer.OrdinalIgnoreCase);

        Assert.True(editor.Save(values).Saved);

        var read = editor.Read();

        foreach (var (key, value) in values)
        {
            Assert.Equal(value, read[key].Saved);

            // Stored only where it is a difference: four of these are already what appsettings.json
            // says, and pinning those would make the file a second copy of the configuration.
            Assert.Equal(
                value == (read[key].Underlying ?? string.Empty) ? SettingSource.AppSettings : SettingSource.WebUi,
                read[key].Source);
        }
    }

    private static string Plausible(string key) => key switch
    {
        EvSettingKeys.Id => "id4",
        EvSettingKeys.Name => "The ID.4",
        EvSettingKeys.Make => "Volkswagen",
        EvSettingKeys.Model => "ID.4 Pro",
        EvSettingKeys.BatteryCapacityKWh => "77",
        EvSettingKeys.ChargeEfficiency => "0.9",
        EvSettingKeys.Phases => "3",
        EvSettingKeys.MinChargingCurrentAmps => "6",
        EvSettingKeys.MaxChargingCurrentAmps => "16",
        EvSettingKeys.TelemetryTopic => "gleanvolt/vehicle/id4/state",
        // Every Enabled key: false, so that round-tripping them all enables no feed.
        _ => "false",
    };

    // -- Where a value comes from.

    [Fact]
    public void The_file_wins_over_the_environment_and_says_so()
    {
        SetEnvironment(EvSettingKeys.TelemetryTopic, "cars/one");
        WriteFile((EvSettingKeys.TelemetryTopic, "cars/two"));

        var setting = Editor().Read()[EvSettingKeys.TelemetryTopic];

        Assert.Equal("cars/two", setting.Saved);
        Assert.Equal(SettingSource.WebUi, setting.Source);
        Assert.Equal("cars/one", setting.Underlying);
    }

    [Fact]
    public void A_value_equal_to_what_is_underneath_is_not_stored()
    {
        // The file lists exceptions. Pinning a value the environment could otherwise still change is
        // how ".env isn't working" becomes a mystery.
        var editor = Editor();

        Assert.True(editor.Save(new Dictionary<string, string> { [EvSettingKeys.Phases] = "3" }).Saved);

        Assert.Empty(File());
        Assert.Equal(SettingSource.AppSettings, editor.Read()[EvSettingKeys.Phases].Source);
    }

    [Fact]
    public void Revert_takes_a_key_back_out_of_the_file()
    {
        WriteFile((EvSettingKeys.Phases, "1"));
        var editor = Editor();

        Assert.True(editor.Revert(EvSettingKeys.Phases).Saved);

        Assert.Empty(File());
        Assert.Equal("3", editor.Read()[EvSettingKeys.Phases].Saved);
    }

    [Fact]
    public void Removing_the_last_key_deletes_the_file_and_nothing_else()
    {
        WriteFile((EvSettingKeys.Phases, "1"));

        Assert.True(Editor().Revert(EvSettingKeys.Phases).Saved);

        Assert.False(System.IO.File.Exists(OverridesPath));
    }

    [Fact]
    public void The_running_values_do_not_move_when_the_file_does()
    {
        var editor = Editor();

        Assert.True(editor.Save(new Dictionary<string, string> { [EvSettingKeys.Phases] = "1" }).Saved);

        var setting = editor.Read()[EvSettingKeys.Phases];
        Assert.Equal("1", setting.Saved);
        Assert.Equal("3", setting.Running);
        Assert.True(setting.IsPending);
    }

    // -- Validation.

    [Fact]
    public void A_car_the_charger_can_never_serve_is_refused_at_the_form()
    {
        // The whole point of running the real rules: refused here rather than at 2 a.m. on the next
        // restart, where the only symptom is a car that never starts.
        var result = Editor(Configuration(), minAmps: 6, maxAmps: 6).Save(new Dictionary<string, string>
        {
            [EvSettingKeys.MinChargingCurrentAmps] = "8",
        });

        Assert.False(result.Saved);
        Assert.Contains(result.Problems, problem => problem.Contains("no current in common", StringComparison.Ordinal));
        Assert.Empty(File());
    }

    [Fact]
    public void A_phase_count_no_car_has_is_refused()
    {
        var result = Editor().Save(new Dictionary<string, string> { [EvSettingKeys.Phases] = "4" });

        Assert.False(result.Saved);
        Assert.Contains(result.Problems, problem => problem.Contains("must be 1, 2 or 3", StringComparison.Ordinal));
        Assert.Empty(File());
    }

    [Fact]
    public void A_feed_switched_on_with_a_required_field_empty_is_refused_naming_the_field()
    {
        var result = Editor().SaveFeed(VehicleFeed.OwnTopic, new Dictionary<string, string>());

        Assert.False(result.Saved);
        Assert.Contains(EvSettingKeys.TelemetryTopic, Assert.Single(result.Problems), StringComparison.Ordinal);
        Assert.Empty(File());
    }

    // -- Switching feed.

    [Fact]
    public void Switching_disables_the_feed_it_leaves_in_the_same_save()
    {
        SetEnvironment(EvSettingKeys.MqttEnabled, "true");
        SetEnvironment(EvSettingKeys.TelemetryTopic, "cars/id4");

        var editor = Editor();
        Assert.Equal(VehicleFeed.OwnTopic, editor.Read().Feed);

        var result = editor.SaveFeed(VehicleFeed.External, new Dictionary<string, string>());

        Assert.True(result.Saved);
        Assert.Equal("false", File()[EvSettingKeys.MqttEnabled]);
        Assert.Equal("true", File()[EvSettingKeys.ExternalEnabled]);
        Assert.Equal(VehicleFeed.External, editor.Read().Feed);
    }

    [Fact]
    public void Switching_leaves_the_old_accounts_fields_alone()
    {
        // Switching back must not mean typing the topic in again.
        WriteFile((EvSettingKeys.MqttEnabled, "true"), (EvSettingKeys.TelemetryTopic, "cars/id4"));

        var editor = Editor();

        Assert.True(editor.SaveFeed(VehicleFeed.ChargeOnly, new Dictionary<string, string>()).Saved);

        Assert.Equal("cars/id4", File()[EvSettingKeys.TelemetryTopic]);
        Assert.Equal(VehicleFeed.ChargeOnly, editor.Read().Feed);
    }

    [Fact]
    public void Switching_off_a_feed_nothing_underneath_enables_writes_no_noise()
    {
        // "false" against a key that is already false is not an exception, and the file holds only
        // exceptions.
        var editor = Editor();

        Assert.True(editor.SaveFeed(VehicleFeed.OwnTopic, new Dictionary<string, string>
        {
            [EvSettingKeys.TelemetryTopic] = "gleanvolt/vehicle/id4/state",
        }).Saved);

        Assert.Equal(
            [EvSettingKeys.TelemetryTopic, EvSettingKeys.MqttEnabled],
            File().Keys.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Charge_only_is_a_configuration_that_saves()
    {
        WriteFile((EvSettingKeys.ExternalEnabled, "true"));
        var editor = Editor();

        Assert.True(editor.SaveFeed(VehicleFeed.ChargeOnly, new Dictionary<string, string>()).Saved);

        Assert.Equal(VehicleFeed.ChargeOnly, editor.Read().Feed);
        Assert.DoesNotContain(EvSettingKeys.ExternalEnabled, File().Keys);
    }

    // -- The host without the file.

    [Fact]
    public void A_host_that_does_not_read_the_file_takes_no_edit()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(AppSettings);

        var editor = Editor(configuration);

        Assert.NotNull(editor.Read().Unavailable);
        Assert.False(editor.Save(new Dictionary<string, string> { [EvSettingKeys.Phases] = "1" }).Saved);
    }
}

/// <summary>
/// A secret store that keeps what it is given in memory and records every call — so a test can assert
/// what was written and what was deleted.
/// </summary>
internal sealed class RecordingSecretStore : ISecretStore
{
    public Dictionary<string, string> Stored { get; } = new(StringComparer.Ordinal);

    public List<string> Written { get; } = [];

    public List<string> Deleted { get; } = [];

    public string? Read(string name) => Stored.GetValueOrDefault(name);

    public bool Write(string name, string value)
    {
        Written.Add(name);
        Stored[name] = value;
        return true;
    }

    public bool Delete(string name)
    {
        Deleted.Add(name);
        Stored.Remove(name);
        return true;
    }

    public string Describe() => "owner-only files (0600) in the data directory";
}
