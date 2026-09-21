using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Configuration;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The installation edited from the web UI (issue #204), against real configuration providers: an
/// in-memory stand-in for appsettings.json, real environment variables, and the overrides file on disk.
/// What these guard: the file wins over the environment and loses to the command line, it holds only
/// real differences, a save the startup resolver would refuse writes nothing, and the running values
/// never move.
/// </summary>
public sealed class PvSystemEditorTests : IDisposable
{
    // Environment variables are process-wide, so each test gets a prefix nobody else uses.
    private readonly string _prefix = $"GLEANVOLT_TEST_{Guid.NewGuid():N}_";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gleanvolt-pv-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _variables = [];

    private static readonly Dictionary<string, string?> AppSettings = new()
    {
        ["Pv:Inverter:Host"] = "192.168.2.10",
        ["Pv:Inverter:Port"] = "502",
        ["Pv:Chargers:0:Id"] = "charger",
        ["Pv:Chargers:0:Host"] = "192.168.2.6",
        ["Pv:TiltDegrees"] = "35",
    };

    public PvSystemEditorTests() => Directory.CreateDirectory(_root);

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
        var variable = _prefix + PvSystemSettingKeys.EnvironmentVariable(key);
        _variables.Add(variable);
        Environment.SetEnvironmentVariable(variable, value);
    }

    private void WriteFile(params (string Key, string Value)[] values) =>
        PvSystemOverrides.Write(OverridesPath, values.ToDictionary(value => value.Key, value => value.Value));

    // Composed in the host's order: appsettings, environment, [the overrides file], command line.
    private ConfigurationManager Configuration(params string[] commandLine)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(AppSettings);
        configuration.AddEnvironmentVariables(_prefix);

        if (commandLine.Length > 0)
        {
            configuration.AddCommandLine(commandLine);
        }

        configuration.AddPvSystemOverrides(_root);
        return configuration;
    }

    private static PvSystemEditor Editor(IConfiguration configuration, Func<DeviceConfig, IModbusClient>? clients = null) =>
        new(configuration, PvSystemEditor.Snapshot(configuration), NullLogger<PvSystemEditor>.Instance, clients);

    // -- Precedence: where the file sits among the sources.

    [Fact]
    public void The_file_wins_over_the_environment()
    {
        // The case the order exists for: compose always sets the device addresses from .env.
        SetEnvironment(PvSystemSettingKeys.InverterHost, "192.168.2.10");
        WriteFile((PvSystemSettingKeys.InverterHost, "192.168.2.20"));

        var configuration = Configuration();

        Assert.Equal("192.168.2.20", PvSystemResolver.Resolve(configuration).Inverter.Connection.Host);
    }

    [Fact]
    public void The_command_line_wins_over_the_file()
    {
        WriteFile((PvSystemSettingKeys.TiltDegrees, "30"));

        var configuration = Configuration("--Pv:TiltDegrees=40");

        Assert.Equal("40", configuration[PvSystemSettingKeys.TiltDegrees]);
    }

    [Fact]
    public void The_file_is_where_the_setting_says()
    {
        var elsewhere = Path.Combine(_root, "var", "lib", "gleanvolt", "pv-system.json");
        PvSystemOverrides.Write(elsewhere, new Dictionary<string, string> { [PvSystemSettingKeys.TiltDegrees] = "20" });

        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(AppSettings);
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { [PvSystemOverrides.PathKey] = elsewhere });

        Assert.Equal(elsewhere, configuration.AddPvSystemOverrides("/opt/gleanvolt"));
        Assert.Equal("20", configuration[PvSystemSettingKeys.TiltDegrees]);
    }

    [Fact]
    public void A_relative_path_is_under_the_content_root()
    {
        var configuration = new ConfigurationManager();

        Assert.Equal(OverridesPath, configuration.AddPvSystemOverrides(_root));
    }

    [Fact]
    public void A_file_that_is_not_json_stops_the_start_naming_itself()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OverridesPath)!);
        File.WriteAllText(OverridesPath, "{ not json");

        var error = Assert.Throws<InvalidOperationException>(() => Configuration());

        Assert.Contains(OverridesPath, error.Message);
    }

    [Fact]
    public void The_startup_line_names_what_the_file_overrode()
    {
        WriteFile((PvSystemSettingKeys.TiltDegrees, "30"));

        var described = PvSystemOverrides.DescribeLoaded(Configuration());

        Assert.Contains(OverridesPath, described);
        Assert.Contains(PvSystemSettingKeys.TiltDegrees, described);
        Assert.Equal(string.Empty, PvSystemOverrides.DescribeLoaded(new ConfigurationManager()));
    }

    // -- Reading: saved against running, and where each value comes from.

    [Fact]
    public void Each_value_says_where_it_comes_from()
    {
        SetEnvironment(PvSystemSettingKeys.ChargerHost, "192.168.2.7");
        WriteFile((PvSystemSettingKeys.TiltDegrees, "30"));

        var settings = Editor(Configuration()).Read();

        Assert.Equal(PvSettingSource.WebUi, settings[PvSystemSettingKeys.TiltDegrees].Source);
        Assert.Equal("35", settings[PvSystemSettingKeys.TiltDegrees].Underlying);
        Assert.Equal(PvSettingSource.Environment, settings[PvSystemSettingKeys.ChargerHost].Source);
        Assert.Equal(PvSettingSource.AppSettings, settings[PvSystemSettingKeys.InverterHost].Source);
        Assert.Equal(PvSettingSource.Default, settings[PvSystemSettingKeys.Latitude].Source);
        Assert.Equal(OverridesPath, settings.OverridesPath);
        Assert.Null(settings.Unavailable);
    }

    [Fact]
    public void A_save_is_pending_until_the_next_start_and_the_running_value_does_not_move()
    {
        var configuration = Configuration();
        var editor = Editor(configuration);

        Assert.True(editor.Save(new Dictionary<string, string> { [PvSystemSettingKeys.TiltDegrees] = "30" }).Saved);

        var tilt = editor.Read()[PvSystemSettingKeys.TiltDegrees];
        Assert.Equal("30", tilt.Saved);
        Assert.Equal("35", tilt.Running);
        Assert.True(tilt.IsPending);
        Assert.Equal("35", configuration[PvSystemSettingKeys.TiltDegrees]);

        // And the next start reads it, which is what makes the banner go away.
        var restarted = Editor(Configuration()).Read();
        Assert.Empty(restarted.Pending);
        Assert.Equal("30", restarted[PvSystemSettingKeys.TiltDegrees].Running);
    }

    // -- Saving.

    [Fact]
    public void The_file_holds_only_the_keys_that_were_edited()
    {
        var editor = Editor(Configuration());

        editor.Save(new Dictionary<string, string>
        {
            [PvSystemSettingKeys.TiltDegrees] = "30",
            // Unchanged from appsettings.json: not an edit, so not written.
            [PvSystemSettingKeys.InverterHost] = "192.168.2.10",
        });

        Assert.Equal(
            new Dictionary<string, string> { [PvSystemSettingKeys.TiltDegrees] = "30" },
            PvSystemOverrides.Read(OverridesPath));
    }

    [Fact]
    public void A_charger_key_round_trips_through_the_file()
    {
        var editor = Editor(Configuration());

        editor.Save(new Dictionary<string, string> { [PvSystemSettingKeys.ChargerHost] = "192.168.2.20" });

        Assert.Equal("192.168.2.20", PvSystemResolver.Resolve(Configuration()).Chargers[0].Connection.Host);
    }

    [Fact]
    public void A_save_the_resolver_refuses_writes_nothing_and_says_why()
    {
        var editor = Editor(Configuration());

        var result = editor.Save(new Dictionary<string, string>
        {
            [PvSystemSettingKeys.LossFactor] = "2",
            [PvSystemSettingKeys.InverterHost] = string.Empty,
        });

        Assert.False(result.Saved);
        Assert.Contains(result.Problems, problem => problem.Contains("Pv:LossFactor", StringComparison.Ordinal));
        Assert.Contains(result.Problems, problem => problem.Contains("Pv:Inverter:Host", StringComparison.Ordinal));
        Assert.False(File.Exists(OverridesPath));
    }

    [Fact]
    public void A_value_that_is_not_a_number_is_refused_rather_than_thrown()
    {
        var result = Editor(Configuration()).Save(
            new Dictionary<string, string> { [PvSystemSettingKeys.TiltDegrees] = "steep" });

        Assert.False(result.Saved);
        Assert.Contains(result.Problems, problem => problem.Contains("TiltDegrees", StringComparison.Ordinal));
        Assert.False(File.Exists(OverridesPath));
    }

    [Fact]
    public void A_key_outside_the_installation_is_refused()
    {
        var result = Editor(Configuration()).Save(
            new Dictionary<string, string> { ["Solcast:ApiKey"] = "secret" });

        Assert.False(result.Saved);
        Assert.False(File.Exists(OverridesPath));
    }

    // -- Reverting.

    [Fact]
    public void Revert_goes_back_to_the_environment()
    {
        SetEnvironment(PvSystemSettingKeys.InverterHost, "192.168.2.10");
        WriteFile((PvSystemSettingKeys.InverterHost, "192.168.2.20"), (PvSystemSettingKeys.TiltDegrees, "30"));
        var editor = Editor(Configuration());

        Assert.True(editor.Revert(PvSystemSettingKeys.InverterHost).Saved);

        var host = editor.Read()[PvSystemSettingKeys.InverterHost];
        Assert.Equal("192.168.2.10", host.Saved);
        Assert.Equal(PvSettingSource.Environment, host.Source);
        Assert.True(File.Exists(OverridesPath));
    }

    [Fact]
    public void Reverting_the_last_key_deletes_the_file()
    {
        WriteFile((PvSystemSettingKeys.TiltDegrees, "30"));
        var editor = Editor(Configuration());

        editor.Revert(PvSystemSettingKeys.TiltDegrees);

        Assert.False(File.Exists(OverridesPath));
    }

    [Fact]
    public void Saving_the_underlying_value_takes_the_key_out_of_the_file()
    {
        WriteFile((PvSystemSettingKeys.TiltDegrees, "30"));
        var editor = Editor(Configuration());

        editor.Save(new Dictionary<string, string> { [PvSystemSettingKeys.TiltDegrees] = "35" });

        Assert.False(File.Exists(OverridesPath));
    }

    // -- A host that does not read the file.

    [Fact]
    public void A_host_without_the_file_among_its_sources_takes_no_edits()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(AppSettings).Build();
        var editor = Editor(configuration);

        Assert.NotNull(editor.Read().Unavailable);
        Assert.False(editor.Save(new Dictionary<string, string> { [PvSystemSettingKeys.TiltDegrees] = "30" }).Saved);
    }

    // -- Probing an address.

    [Theory]
    [InlineData(PvDeviceRole.Inverter, (ushort)57, true)]
    [InlineData(PvDeviceRole.Inverter, (ushort)1234, false)]
    [InlineData(PvDeviceRole.Charger, (ushort)2, true)]
    [InlineData(PvDeviceRole.Charger, (ushort)57, false)]
    public async Task A_probe_says_whether_what_answered_looks_like_the_device(PvDeviceRole role, ushort value, bool plausible)
    {
        DeviceConfig? probed = null;
        var editor = Editor(Configuration(), device =>
        {
            probed = device;
            return new OneRegisterClient(value);
        });

        var result = await editor.ProbeAsync(role, "192.168.2.20", 502, 1);

        Assert.True(result.Answered);
        Assert.Equal(plausible, result.Plausible);
        Assert.Equal("192.168.2.20", probed!.Host);
    }

    [Fact]
    public async Task A_device_that_does_not_answer_is_reported_not_thrown()
    {
        var editor = Editor(Configuration(), _ => new OneRegisterClient(null));

        var result = await editor.ProbeAsync(PvDeviceRole.Charger, "192.168.2.20", 502, 1);

        Assert.False(result.Answered);
        Assert.Contains("did not answer", result.Message);
    }

    /// <summary>A device that answers every read with one value, or refuses the connection when there is none.</summary>
    private sealed class OneRegisterClient(ushort? value) : IModbusClient
    {
        public bool IsConnected => value is not null;

        public Task ConnectAsync(CancellationToken cancellationToken = default) =>
            value is null ? Task.FromException(new TimeoutException("Timed out connecting.")) : Task.CompletedTask;

        public Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort numberOfPoints, CancellationToken cancellationToken = default) =>
            Task.FromResult(new[] { value!.Value });

        public Task<ushort[]> ReadInputRegistersAsync(ushort startAddress, ushort numberOfPoints, CancellationToken cancellationToken = default) =>
            Task.FromResult(new[] { value!.Value });

        public Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A probe never writes.");

        public Task WriteMultipleRegistersAsync(ushort startAddress, ushort[] values, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A probe never writes.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
