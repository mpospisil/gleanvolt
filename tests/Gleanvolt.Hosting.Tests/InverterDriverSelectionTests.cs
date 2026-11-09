using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Models;
using Gleanvolt.Devices;
using Gleanvolt.Devices.GoE;
using Gleanvolt.Devices.Huawei;
using Gleanvolt.Devices.Solax;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Setup;
using Gleanvolt.Infrastructure;

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
    public void The_charger_catalog_has_solax_first_then_the_two_sChargers_then_go_e()
    {
        Assert.Equal(["solax", "huawei-7ks", "huawei-22kt", "goe"], ChargerDrivers.Known.Select(driver => driver.Id));
    }

    [Fact]
    public async Task The_container_runs_go_e_over_http_on_port_80_and_in_dry_run_passes_only_reads()
    {
        // go-e (#352): the driver gets the charger's HTTP client, at the configured host on the family's port,
        // and in dry-run that client drops anything the driver did not mark as a read without sending it.
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(
            [.. UsableSite.Pairs, new("Pv:Chargers:0:Driver", "goe"), new("ChargeControl:DryRun", "true"), new("Web:Enabled", "false")]);
        builder.Services.AddGleanvolt(builder.Configuration);
        await using var provider = builder.Services.BuildServiceProvider();

        Assert.IsType<GoeChargerDriver>(provider.GetRequiredService<IEvChargerDriver>());
        Assert.Equal(80, provider.GetRequiredService<PvSystemInfo>().Chargers[0].Connection.Port);

        var http = provider.GetRequiredKeyedService<HttpClient>(ModbusClientKeys.EvCharger);
        Assert.Equal(new Uri("http://127.0.0.2:80/"), http.BaseAddress);

        using var write = ChargerHttp.Write("api/set?amp=6");
        using var answer = await http.SendAsync(write);
        Assert.Equal("{}", await answer.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_container_runs_the_sCharger_with_the_charge_controls_phases()
    {
        // The SCharger's setpoint is power, so the driver has to convert at the phases the car charges on (#351).
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(
            [.. UsableSite.Pairs, new("Pv:Chargers:0:Driver", "huawei-22kt"), new("ChargeControl:Phases", "1"), new("Web:Enabled", "false")]);
        builder.Services.AddGleanvolt(builder.Configuration);
        await using var provider = builder.Services.BuildServiceProvider();

        var driver = Assert.IsType<HuaweiSChargerDriver>(provider.GetRequiredService<IEvChargerDriver>());
        Assert.Equal(3, driver.Capabilities.Phases);
        Assert.Equal(1, driver.SetpointPhases);
        Assert.False(driver.Capabilities.ReportsStatus);
    }

    [Fact]
    public void Headless_three_phases_on_the_single_phase_sCharger_stops_the_start()
    {
        var error = Record.Exception(() => PvSystemResolver.Resolve(Configuration(
            [.. UsableSite.Settings, ("Pv:Chargers:0:Driver", "huawei-7ks"), ("ChargeControl:Phases", "3")])));

        Assert.NotNull(error);
        Assert.Contains("more than the Huawei SCharger-7KS charger supplies (1)", error.Message);
    }

    [Fact]
    public async Task The_container_runs_the_configured_charger_driver()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection([.. UsableSite.Pairs, new("Pv:Chargers:0:Driver", "SOLAX"), new("Web:Enabled", "false")]);
        builder.Services.AddGleanvolt(builder.Configuration);
        await using var provider = builder.Services.BuildServiceProvider();

        Assert.IsType<SolaxChargerDriver>(provider.GetRequiredService<IEvChargerDriver>());
        Assert.Equal("solax", provider.GetRequiredService<PvSystemInfo>().Chargers[0].Driver);
    }

    [Fact]
    public void Headless_an_unknown_charger_driver_stops_the_start_naming_the_known_drivers()
    {
        var error = Record.Exception(() =>
            PvSystemResolver.Resolve(Configuration([.. UsableSite.Settings, ("Pv:Chargers:0:Driver", "wallco")])));

        Assert.NotNull(error);
        Assert.Contains("Pv:Chargers:0:Driver ('wallco')", error.Message);
        Assert.Contains("Known drivers: solax", error.Message);
    }

    [Theory]
    [InlineData("ChargeControl:MaxChargingCurrentAmps", "40", "above the highest current the SolaX charger accepts (32 A)")]
    [InlineData("ChargeControl:MinChargingCurrentAmps", "4", "below the lowest current the SolaX charger charges at (6 A)")]
    [InlineData("ChargeControl:Phases", "4", "more than the SolaX charger supplies (3)")]
    public void Headless_a_charge_limit_the_charger_cannot_honour_stops_the_start(string key, string value, string expected)
    {
        var error = Record.Exception(() => PvSystemResolver.Resolve(Configuration([.. UsableSite.Settings, (key, value)])));

        Assert.NotNull(error);
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void In_setup_mode_a_charge_limit_the_charger_cannot_honour_is_a_problem_on_the_charger_step()
    {
        var status = SetupStatusEvaluator.Evaluate(
            Configuration([.. UsableSite.Settings, ("ChargeControl:MaxChargingCurrentAmps", "40")]),
            new SetupHost(TimeZoneInfo.Utc, IsWindows: false));

        var problem = Assert.Single(status.MissingRequired);
        Assert.Equal(SetupArea.Charger, problem.Area);
    }
}
