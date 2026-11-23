using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Devices;
using Gleanvolt.Hosting.Chargers;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Plugin.Shelly.TopAc;
using Gleanvolt.Providers.Client;
using Gleanvolt.Providers.Client.Charger;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Charger;
using Gleanvolt.Providers.Server;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// A charger behind a plugin (#435), end to end: the real charger hub and link on a TestServer, the driver the
/// control loop would get, and the Shelly TopAC plugin (#429) as the provider, with the charger faked over HTTP.
/// What the control loop sees is what <see cref="IEvChargerDriver"/> answers, and what the charger gets is
/// <see cref="FakeTopAc.Writes"/>, so those are what is checked.
/// </summary>
public sealed class ExternalChargerTests : IAsyncLifetime, IDisposable
{
    private readonly FakeTopAc _topAc = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gleanvolt-charger-" + Guid.NewGuid().ToString("N"));
    private readonly List<ChargerProviderConnection> _plugins = [];

    private WebApplication? _app;
    private string _token = string.Empty;

    // Read by the hub at every hello, so a test can change it before its plugin connects.
    // The TopAC's own range: a charge control asking more is refused at the hello (#440).
    private ChargeControlOptions _chargeControl = new()
    {
        MinChargingCurrentAmps = 6,
        MaxChargingCurrentAmps = 16,
        Phases = 3,
    };

    private ExternalChargerLink Link => _app!.Services.GetRequiredService<ExternalChargerLink>();

    private ExternalProviderRegistry Registry => _app!.Services.GetRequiredService<ExternalProviderRegistry>();

    private ExternalProviderStatus Status => Registry.Find(ChargerContract.Kind)!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ISecretStore>(new RecordingSecretStore());
        builder.Services.AddSingleton(new ProviderHostInfo("1.4.0-test"));
        builder.Services.AddSingleton(Site);
        builder.Services.AddSingleton<IOptions<ChargeControlOptions>>(new CurrentOptions<ChargeControlOptions>(() => _chargeControl));
        builder.Services.AddSingleton(Options.Create(new ExternalChargerOptions
        {
            ReadTimeout = TimeSpan.FromSeconds(1),
            CommandTimeout = TimeSpan.FromSeconds(2),
            // Every read in these tests is a fresh one, so each sees the fake as it is now.
            ReadingMaxAge = TimeSpan.Zero,
        }));
        builder.Services.AddSingleton<ExternalChargerLink>();
        builder.Services.AddExternalProvider<ChargerHub>(ChargerHub.Kind);

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapExternalProviders();
        await _app.StartAsync();

        _token = await _app.Services.GetRequiredService<ExternalProviderRegistry>().CreateTokenAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var plugin in _plugins)
        {
            await plugin.StopAsync(CancellationToken.None);
            plugin.Dispose();
        }

        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_charging_TopAc_reads_through_the_plugin_as_the_driver_contract_says()
    {
        _topAc.Charging(10);
        _topAc.CurrentLimit = 10;
        var (plugin, charger) = await StartPluginAsync();
        var driver = Driver();

        var reading = await driver.ReadAsync();

        Assert.Equal(EvChargerStatus.Charging, reading.Status);
        Assert.Equal(3 * 230 * 10, reading.PowerWatts, precision: 6);
        Assert.Equal(10, reading.CurrentAmps);
        Assert.Equal(new EvChargerSettings(true, 10), await driver.ReadSettingsAsync());

        // The welcome told the plugin the charge control's limits; the plugin told Gleanvolt the charger's ceiling.
        Assert.Equal(new ChargerLimits(6, 16, 3, 230), plugin.Limits);
        Assert.Equal(16, Link.DeviceMaxAmps);
        Assert.Equal(PowerUnit.Kilowatts, charger.Unit);
        Assert.Equal("Shelly TopAC", Status.ProviderName);
        Assert.True(Status.HasPage);
        Assert.Empty(_topAc.Writes);
    }

    [Fact]
    public async Task Idle_and_a_car_waiting_read_as_available_and_preparing_and_a_held_car_is_the_chargers_doing()
    {
        await StartPluginAsync();
        var driver = Driver();

        _topAc.Idle();
        Assert.Equal(EvChargerStatus.Available, (await driver.ReadAsync()).Status);

        _topAc.Waiting();
        Assert.Equal(EvChargerStatus.Preparing, (await driver.ReadAsync()).Status);

        // The switch is off: the charger holds the car, which is never "the car is done".
        _topAc.StartCharging = false;
        _topAc.WorkState = "charger_complete";
        Assert.Equal(EvChargerStatus.SuspendedEvse, (await driver.ReadAsync()).Status);
        Assert.False((await driver.ReadSettingsAsync()).ChargingAllowed);
    }

    [Fact]
    public async Task A_pause_is_the_switch_off_held_by_the_plugin_and_a_setpoint_resumes_it_limit_first()
    {
        _topAc.Charging(10);
        _topAc.CurrentLimit = 10;
        await StartPluginAsync();
        var driver = Driver();

        await driver.SetChargingAllowedAsync(true, "test start");
        await driver.SetCurrentAsync(10, 0, "test pause");
        _topAc.WorkState = "charger_pause";
        _topAc.PhaseCurrent = 0;
        _topAc.TotalPowerKw = 0;

        Assert.Equal(["start_charging=true", "start_charging=false"], _topAc.Writes);
        Assert.Equal(new EvChargerSettings(true, 0), await driver.ReadSettingsAsync());
        Assert.Equal(EvChargerStatus.SuspendedEvse, (await driver.ReadAsync()).Status);

        await driver.SetCurrentAsync(0, 8, "test resume");

        Assert.Equal(["start_charging=true", "start_charging=false", "current_limit=8", "start_charging=true"], _topAc.Writes);
        Assert.Equal(new EvChargerSettings(true, 8), await driver.ReadSettingsAsync());
    }

    [Fact]
    public async Task A_setpoint_within_the_threshold_sends_nothing_and_one_above_the_charger_is_clamped_to_it()
    {
        _topAc.CurrentLimit = 10;
        await StartPluginAsync();
        var driver = Driver();
        await driver.ReadAsync();

        await driver.SetCurrentAsync(10, 10, "steady");
        Assert.Empty(_topAc.Writes);

        // Above the 16 A the TopAC said in its hello.
        await driver.SetCurrentAsync(10, 20, "more sun");
        Assert.Equal(["current_limit=16"], _topAc.Writes);
    }

    [Fact]
    public async Task In_dry_run_nothing_reaches_the_plugin_and_what_would_have_been_stands_in()
    {
        _topAc.CurrentLimit = 10;
        await StartPluginAsync();
        var driver = Driver(dryRun: true);

        await driver.SetChargingAllowedAsync(false, "dry stop");
        await driver.SetCurrentAsync(10, 6, "dry setpoint");

        Assert.Empty(_topAc.Writes);
        Assert.Equal(new EvChargerSettings(false, 6), await driver.ReadSettingsAsync());
        Assert.Equal(6, (await driver.ReadAsync()).CurrentAmps);
    }

    [Fact]
    public async Task A_charger_with_a_password_is_one_Gleanvolt_could_not_read_and_the_owner_is_told_why()
    {
        _topAc.Fail = HttpStatusCode.Unauthorized;
        var (_, charger) = await StartPluginAsync(publishes: false);

        await Assert.ThrowsAsync<TimeoutException>(() => Driver().ReadAsync());

        await Eventually(() => Status.NeedsOwner, "the plugin should report that only the owner can fix it");
        Assert.Equal(ProviderStatusCodes.Refused, Status.StatusCode);
        Assert.False(charger.Status!.Healthy);
    }

    [Fact]
    public async Task A_command_the_charger_refuses_fails_in_Gleanvolt_with_the_plugins_reason()
    {
        await StartPluginAsync();
        _topAc.Fail = HttpStatusCode.InternalServerError;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Driver().SetChargingAllowedAsync(false, "stop"));

        Assert.Contains("could not stop charging", error.Message);
        Assert.Contains("HTTP 500", error.Message);
    }

    [Fact]
    public async Task Without_a_plugin_a_read_fails_at_once()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Driver().ReadAsync());
        Assert.Contains("No charger plugin is connected", error.Message);
    }

    [Fact]
    public async Task Auto_balance_on_in_the_Shelly_app_asks_the_owner_to_turn_it_off()
    {
        _topAc.AutoBalance = true;
        var (_, charger) = await StartPluginAsync();

        await Driver().ReadAsync();

        await Eventually(() => Status.NeedsOwner, "auto balance fights the control loop");
        Assert.Contains("Auto balance", Status.StatusMessage);
        Assert.Equal(true, charger.OwnerSettings?.AutoBalance);
    }

    [Fact]
    public async Task A_reading_in_the_wrong_units_is_refused_and_the_read_fails_with_why()
    {
        await StartPluginAsync(new FixedSource(new ChargerReading(DateTimeOffset.UtcNow, ChargerState.Charging, -7_000)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Driver().ReadAsync());

        Assert.Contains("refused", error.Message);
        Assert.Contains("The power is -7000 W", error.Message);
    }

    [Fact]
    public async Task A_single_phase_plugin_is_let_in_and_controlled_within_the_range_it_said()
    {
        _chargeControl = new ChargeControlOptions { MinChargingCurrentAmps = 6, MaxChargingCurrentAmps = 32, Phases = 1 };
        var source = new FixedSource(new ChargerReading(DateTimeOffset.UtcNow, ChargerState.Charging, 2300, CurrentAmps: 10, ChargingAllowed: true))
        {
            Capabilities = new ChargerCapabilities(6, 32, Phases: 1),
        };
        await StartPluginAsync(source);
        var driver = Driver();

        Assert.Equal(new EvChargerCapabilities(6, 32, 0, 1, false, false), driver.Capabilities);

        // Above the charger's 32 A: clamped to it, never past it.
        await driver.SetCurrentAsync(10, 40, "more sun");
        await driver.SetCurrentAsync(32, 6, "a cloud");

        Assert.Equal(["current=32", "current=6"], source.Commands);
        Assert.Null(Status.LastRefusal);
    }

    [Fact]
    public async Task A_charger_that_cannot_do_what_the_charge_control_asks_is_refused_with_limits_naming_the_settings()
    {
        // ChargeControl wants 6-20 A on three phases; the charger does 16 A on one.
        _chargeControl = new ChargeControlOptions { MinChargingCurrentAmps = 6, MaxChargingCurrentAmps = 20, Phases = 3 };
        var source = new FixedSource(new ChargerReading(DateTimeOffset.UtcNow, ChargerState.Available, 0))
        {
            Capabilities = new ChargerCapabilities(6, 16, Phases: 1),
        };
        await StartPluginAsync(source, attaches: false);

        await Eventually(() => Status.LastRefusal is not null, "the hello should be refused");

        var refusal = Status.LastRefusal!;
        Assert.Equal(RefusalCodes.Limits, refusal.Code);
        Assert.Contains("ChargeControl:MaxChargingCurrentAmps (20 A)", refusal.Reason);
        Assert.Contains("ChargeControl:Phases (3)", refusal.Reason);
        Assert.Equal(2, refusal.Phrases!.Count);
        Assert.False(Link.IsAttached);

        // Nothing is remembered from a plugin that was not let in.
        Assert.Null(Status.RememberedProviderId);
    }

    [Fact]
    public async Task Capabilities_that_make_no_sense_are_refused_as_invalid()
    {
        var source = new FixedSource(new ChargerReading(DateTimeOffset.UtcNow, ChargerState.Available, 0))
        {
            Capabilities = new ChargerCapabilities(16, 6, Phases: 3),
        };
        await StartPluginAsync(source, attaches: false);

        await Eventually(() => Status.LastRefusal is not null, "the hello should be refused");

        Assert.Equal(RefusalCodes.Invalid, Status.LastRefusal!.Code);
        Assert.Contains("maximum current is 6 A", Status.LastRefusal.Reason);
    }

    [Fact]
    public async Task The_first_plugin_is_remembered_and_another_is_refused_even_after_it_has_gone_until_the_owner_forgets_it()
    {
        var first = await StartPluginAsync(new FixedSource(new ChargerReading(DateTimeOffset.UtcNow, ChargerState.Available, 0)));
        Assert.Equal("shelly-topac", Status.RememberedProviderId);
        Assert.True(Status.CanForget);

        await first.StopAsync(CancellationToken.None);
        await Eventually(() => !Link.IsAttached, "the first plugin should be gone");

        // A sample started by mistake: the charger is not free for it, though nothing is connected.
        var sample = new FixedSource(new ChargerReading(DateTimeOffset.UtcNow, ChargerState.Available, 0));
        await StartPluginAsync(sample, attaches: false, id: "charger-sample");

        await Eventually(() => Status.LastRefusal is not null, "the second plugin should be refused");
        Assert.Equal(RefusalCodes.AnotherProvider, Status.LastRefusal!.Code);
        Assert.Equal("charger-sample", Status.LastRefusal.ProviderId);
        Assert.Contains("shelly-topac", Status.LastRefusal.Reason);
        Assert.False(Link.IsAttached);

        await Registry.ForgetAsync(ChargerContract.Kind);

        await Eventually(() => Link.IsAttached, "after Forget the next plugin should get in");
        Assert.Equal("charger-sample", Status.ProviderId);
        Assert.Equal("charger-sample", Status.RememberedProviderId);
        Assert.Null(Status.LastRefusal);
    }

    [Fact]
    public async Task Forget_closes_the_remembered_plugins_session_and_it_is_remembered_again_when_it_comes_back()
    {
        await StartPluginAsync(new FixedSource(new ChargerReading(DateTimeOffset.UtcNow, ChargerState.Available, 0)));
        var connectedSince = Status.Since;

        await Registry.ForgetAsync(ChargerContract.Kind);

        // Closed: a new session, after the plugin's wait.
        await Eventually(() => Status.Since != connectedSince && Link.IsAttached, "the plugin should be closed and reconnect");
        await Eventually(() => Link.IsAttached && Status.RememberedProviderId == "shelly-topac", "the plugin should reconnect and be remembered");
    }

    [Theory]
    [InlineData("charger_free", true, ChargerState.Available)]
    [InlineData("charger_insert", true, ChargerState.Preparing)]
    [InlineData("charger_wait", true, ChargerState.Preparing)]
    [InlineData("charger_wait", false, ChargerState.SuspendedEvse)]
    [InlineData("charger_charging", true, ChargerState.Charging)]
    [InlineData("charger_pause", true, ChargerState.SuspendedEvse)]
    [InlineData("charger_complete", true, ChargerState.SuspendedEv)]
    [InlineData("charger_complete", false, ChargerState.SuspendedEvse)]
    [InlineData("charger_end", true, ChargerState.Finishing)]
    [InlineData("charger_end", false, ChargerState.SuspendedEvse)]
    [InlineData("charger_error", true, ChargerState.Faulted)]
    [InlineData("charger_new_in_firmware_9", true, ChargerState.Unknown)]
    public void Each_work_state_maps_to_one_contract_state(string workState, bool startCharging, ChargerState expected)
    {
        Assert.Equal(expected, ShellyCharger.StateOf(workState, startCharging));
    }

    [Fact]
    public void Total_power_is_read_in_whichever_unit_matches_the_phases_and_the_unit_is_kept()
    {
        var phases = Enumerable.Repeat(new PhaseInfo(230, 10, null), 3).ToList();
        var unit = PowerUnit.Unknown;

        Assert.Equal(6900, ShellyCharger.PowerWatts(new PowerInfo(6.9, phases), ref unit), precision: 6);
        Assert.Equal(PowerUnit.Kilowatts, unit);

        unit = PowerUnit.Unknown;
        Assert.Equal(6890, ShellyCharger.PowerWatts(new PowerInfo(6890, phases), ref unit), precision: 6);
        Assert.Equal(PowerUnit.Watts, unit);

        // Idle: too little to tell, so the unit found while charging stays.
        var idle = Enumerable.Repeat(new PhaseInfo(230, 0, null), 3).ToList();
        Assert.Equal(0, ShellyCharger.PowerWatts(new PowerInfo(0, idle), ref unit));
        Assert.Equal(PowerUnit.Watts, unit);
    }

    [Theory]
    [InlineData("192.168.1.50", "http://192.168.1.50/")]
    [InlineData(" shelly.local:8080/ ", "http://shelly.local:8080/")]
    [InlineData("https://192.168.1.50", "https://192.168.1.50/")]
    [InlineData("http://192.168.1.50/rpc", null)]
    [InlineData("ftp://192.168.1.50", null)]
    [InlineData("", null)]
    public void An_address_is_a_host_with_an_optional_port(string text, string? expected)
    {
        Assert.Equal(expected, ChargerAddressStore.Parse(text)?.AbsoluteUri);
    }

    private static PvSystemInfo Site { get; } = new(
        Id: "test-site",
        Name: "Test site",
        Address: string.Empty,
        Latitude: 49.2678,
        Longitude: 16.5295,
        AzimuthDegrees: 180,
        TiltDegrees: 35,
        PeakPowerWatts: 5000,
        InverterCapacityWatts: null,
        LossFactor: null,
        InstallDate: null,
        Inverter: new PvDeviceInfo("Inverter", string.Empty, string.Empty, new DeviceConfig { Host = "127.0.0.1" }),
        Chargers: [new PvDeviceInfo("charger", string.Empty, string.Empty, new DeviceConfig { Host = string.Empty }) { Driver = "external" }]);

    // The driver the control loop gets, as the container makes it.
    private IEvChargerDriver Driver(bool dryRun = false) =>
        new ExternalChargerDriverFactory(Link).Create(EvChargerConnection.External, new EvChargerDriverOptions { DryRun = dryRun });

    // The Shelly plugin as Program.cs wires it, on this TestServer.
    private async Task<(ChargerProviderConnection Plugin, ShellyCharger Charger)> StartPluginAsync(bool publishes = true)
    {
        var options = new ShellyOptions { DataDirectory = _directory };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Shelly:Host"] = "192.168.1.50" })
            .Build();
        var address = new ChargerAddressStore(new ProviderSecretStore(_directory), configuration);
        var rpc = new ShellyRpcClient(new HttpClient(_topAc, disposeHandler: false), address, options);
        var charger = new ShellyCharger(rpc, options, TimeProvider.System, NullLogger<ShellyCharger>.Instance);
        var plugin = await StartPluginAsync(charger, publishes);
        _ = new StatusReporter(plugin, charger, NullLogger<StatusReporter>.Instance);
        return (plugin, charger);
    }

    private async Task<ChargerProviderConnection> StartPluginAsync(
        IChargerSource source, bool publishes = false, bool attaches = true, string id = "shelly-topac")
    {
        var plugin = new ChargerProviderConnection(
            Options.Create(new ProviderOptions
            {
                Endpoint = _app!.GetTestServer().BaseAddress,
                Token = _token,
                ProviderId = id,
                Name = "Shelly TopAC",
                Version = "1.0",
                PageUrl = new Uri("http://localhost:8097/"),
                InitialRetryDelay = TimeSpan.FromMilliseconds(50),
                MaxRetryDelay = TimeSpan.FromMilliseconds(200),
                RefusedRetryDelay = TimeSpan.FromMilliseconds(100),
                ConfigureHttp = Configure,
            }),
            source,
            NullLogger<ChargerProviderConnection>.Instance);
        _plugins.Add(plugin);

        await plugin.StartAsync(CancellationToken.None);

        if (!attaches)
        {
            return plugin;
        }

        await Eventually(() => Link.IsAttached, "the plugin should attach");

        if (publishes)
        {
            await Eventually(() => Status.LastDataAt is not null, "the plugin should publish after its hello");
        }

        return plugin;
    }

    private void Configure(Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions options)
    {
        options.HttpMessageHandlerFactory = _ => _app!.GetTestServer().CreateHandler();
        options.Transports = HttpTransportType.LongPolling;
        options.AccessTokenProvider = () => Task.FromResult<string?>(_token);
    }

    private static async Task Eventually(Func<bool> condition, string because)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(condition(), because);
    }

    // A plugin whose charger always says the same thing, fresh, and keeps the commands it was sent.
    private sealed class FixedSource(ChargerReading reading) : IChargerSource
    {
        public ChargerCapabilities Capabilities { get; init; } = ChargerCapabilities.Default;

        public List<string> Commands { get; } = [];

        public Task<ChargerReading> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(reading with { ReadAt = DateTimeOffset.UtcNow });

        public Task SetCurrentAsync(int amps, CancellationToken cancellationToken)
        {
            Commands.Add($"current={amps}");
            return Task.CompletedTask;
        }

        public Task SetChargingAllowedAsync(bool allowed, CancellationToken cancellationToken)
        {
            Commands.Add($"allowed={allowed}");
            return Task.CompletedTask;
        }
    }

    // Options read anew each time, as a test changes them.
    private sealed class CurrentOptions<T>(Func<T> value) : IOptions<T>
        where T : class
    {
        public T Value => value();
    }
}
