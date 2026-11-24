using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Devices;
using Gleanvolt.Devices.Huawei;
using Gleanvolt.Devices.Solax;
using Gleanvolt.Hosting.Chargers;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Setup;
using Gleanvolt.Infrastructure;
using Gleanvolt.Modbus;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// Choosing the inverter driver in configuration (issue #318), on each path a configuration takes: the
/// headless start that refuses it, setup mode that lists it, and the container that runs it.
/// </summary>
public class InverterDriverSelectionTests
{
    private static IConfiguration Configuration(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings
                .GroupBy(setting => setting.Key)
                .Select(group => new KeyValuePair<string, string?>(group.Key, group.Last().Value)))
            .Build();

    [Fact]
    public void The_catalog_has_solax_first_and_huawei()
    {
        Assert.Equal(["solax", "huawei"], InverterDrivers.Known.Select(driver => driver.Id));
        Assert.Equal(["SolaX", "Huawei"], InverterDrivers.Known.Select(driver => driver.DisplayName));
    }

    [Fact]
    public void A_configuration_without_the_field_resolves_to_solax()
    {
        var site = PvSystemResolver.Resolve(Configuration(UsableSite.Settings));

        Assert.Equal("solax", site.InverterDriver);
    }

    [Fact]
    public void Headless_an_unknown_driver_stops_the_start_naming_the_known_drivers()
    {
        var error = Record.Exception(() =>
            PvSystemResolver.Resolve(Configuration([.. UsableSite.Settings, ("Pv:Inverter:Driver", "goodwe")])));

        Assert.NotNull(error);
        Assert.Contains("Pv:Inverter:Driver ('goodwe')", error.Message);
        Assert.Contains("Known drivers: solax, huawei", error.Message);
    }

    [Fact]
    public void In_setup_mode_an_unknown_driver_is_a_problem_on_the_inverter_step()
    {
        var status = SetupStatusEvaluator.Evaluate(
            Configuration([.. UsableSite.Settings, ("Pv:Inverter:Driver", "goodwe")]),
            new SetupHost(TimeZoneInfo.Utc, IsWindows: false));

        Assert.False(status.Usable);
        var problem = Assert.Single(status.MissingRequired);
        Assert.Equal(SetupArea.Inverter, problem.Area);
        Assert.Contains("Pv:Inverter:Driver", problem.English);
    }

    [Fact]
    public void Huawei_refuses_the_correction_for_a_meter_blind_on_one_phase()
    {
        // The SUN2000 driver reports no output per phase, single- or three-phase (#320, #321).
        var error = Record.Exception(() => PvSystemResolver.Resolve(Configuration(
            [.. UsableSite.Settings, ("Pv:Inverter:Driver", "huawei"), ("Pv:Inverter:UnmeteredGridPhase", "L3")])));

        Assert.NotNull(error);
        Assert.Contains("UnmeteredGridPhase cannot be used with the Huawei driver", error.Message);
    }

    [Fact]
    public async Task The_container_runs_the_configured_driver()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection([.. UsableSite.Pairs, new("Pv:Inverter:Driver", "SolaX"), new("Web:Enabled", "false")]);
        builder.Services.AddGleanvolt(builder.Configuration);
        await using var provider = builder.Services.BuildServiceProvider();

        Assert.IsType<SolaxInverterDriver>(provider.GetRequiredService<IInverterDriver>());
        Assert.Equal(["solax", "huawei"], provider.GetServices<IInverterDriverFactory>().Select(factory => factory.Id));
    }

    [Fact]
    public async Task Huawei_runs_the_sun2000_driver_on_a_client_with_its_pacing()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection([.. UsableSite.Pairs, new("Pv:Inverter:Driver", "huawei"), new("Web:Enabled", "false")]);
        builder.Services.AddGleanvolt(builder.Configuration);
        await using var provider = builder.Services.BuildServiceProvider();

        Assert.IsType<HuaweiSun2000Driver>(provider.GetRequiredService<IInverterDriver>());

        Assert.False(((HuaweiSun2000Driver)provider.GetRequiredService<IInverterDriver>()).BehindEmma);

        var connection = provider.GetRequiredService<PvSystemInfo>().Inverter.Connection;
        Assert.Equal(TimeSpan.FromSeconds(1), connection.MinRequestInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), connection.ReconnectBackoff);
    }

    [Fact]
    public async Task Huawei_behind_an_emma_reads_the_grid_from_it()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(
            [.. UsableSite.Pairs, new("Pv:Inverter:Driver", "huawei"), new("Pv:Inverter:Topology", "EMMA"), new("Web:Enabled", "false")]);
        builder.Services.AddGleanvolt(builder.Configuration);
        await using var provider = builder.Services.BuildServiceProvider();

        Assert.Equal("emma", provider.GetRequiredService<PvSystemInfo>().InverterTopology);
        Assert.True(Assert.IsType<HuaweiSun2000Driver>(provider.GetRequiredService<IInverterDriver>()).BehindEmma);
    }

    [Fact]
    public void Headless_a_topology_for_solax_stops_the_start()
    {
        var error = Record.Exception(() =>
            PvSystemResolver.Resolve(Configuration([.. UsableSite.Settings, ("Pv:Inverter:Topology", "emma")])));

        Assert.NotNull(error);
        Assert.Contains("Pv:Inverter:Topology ('emma') cannot be used with the SolaX driver", error.Message);
    }

    [Fact]
    public async Task The_container_drives_the_charger_through_a_plugin_and_opens_no_connection_to_it()
    {
        // #445: every charger is behind a plugin. The hub is on, and nothing in the container reaches the charger
        // itself: a charger that serves one Modbus client drops a second.
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection([.. UsableSite.Pairs, new("Web:Enabled", "false")]);
        builder.Services.AddGleanvolt(builder.Configuration);
        await using var provider = builder.Services.BuildServiceProvider();

        Assert.IsType<ExternalChargerDriver>(provider.GetRequiredService<IEvChargerDriver>());
        Assert.Null(provider.GetKeyedService<IModbusClient>("EvCharger"));
        Assert.Null(provider.GetKeyedService<IModbusClient>("charger"));
        Assert.Null(provider.GetKeyedService<HttpClient>("EvCharger"));

        var registry = provider.GetRequiredService<Gleanvolt.Providers.Server.ExternalProviderRegistry>();
        Assert.True(registry.IsEnabled(ChargerHub.Kind));
    }

    [Theory]
    [InlineData("", "192.168.2.6")]
    [InlineData("goe", "192.168.2.6")]
    [InlineData("huawei-7ks", "")]
    [InlineData("wallco", "")]
    public async Task A_charger_configured_for_a_built_in_driver_is_ignored(string driver, string host)
    {
        // An installation upgraded from 1.3 still has its charger in Pv:Chargers. It starts, on the plugin.
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(
            [.. UsableSite.Pairs, new("Pv:Chargers:0:Driver", driver), new("Pv:Chargers:0:Host", host), new("Web:Enabled", "false")]);
        builder.Services.AddGleanvolt(builder.Configuration);
        await using var provider = builder.Services.BuildServiceProvider();

        Assert.IsType<ExternalChargerDriver>(provider.GetRequiredService<IEvChargerDriver>());
        Assert.Null(provider.GetKeyedService<IModbusClient>("charger"));
    }

    [Fact]
    public void Charge_limits_are_the_plugins_to_check_not_the_starts()
    {
        // Checked at the plugin's hello, against what its charger says it can do (#440).
        var site = PvSystemResolver.Resolve(Configuration([.. UsableSite.Settings, ("ChargeControl:MaxChargingCurrentAmps", "40")]));

        Assert.Equal("127.0.0.1", site.Inverter.Connection.Host);
    }
}
