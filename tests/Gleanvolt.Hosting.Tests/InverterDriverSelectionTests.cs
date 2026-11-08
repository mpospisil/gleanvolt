using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Models;
using Gleanvolt.Devices;
using Gleanvolt.Devices.Solax;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Setup;

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
    public void The_catalog_has_solax()
    {
        var solax = Assert.Single(InverterDrivers.Known);

        Assert.Equal("solax", solax.Id);
        Assert.Equal("SolaX", solax.DisplayName);
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
            PvSystemResolver.Resolve(Configuration([.. UsableSite.Settings, ("Pv:Inverter:Driver", "huawei")])));

        Assert.NotNull(error);
        Assert.Contains("Pv:Inverter:Driver ('huawei')", error.Message);
        Assert.Contains("Known drivers: solax", error.Message);
    }

    [Fact]
    public void In_setup_mode_an_unknown_driver_is_a_problem_on_the_inverter_step()
    {
        var status = SetupStatusEvaluator.Evaluate(
            Configuration([.. UsableSite.Settings, ("Pv:Inverter:Driver", "huawei")]),
            new SetupHost(TimeZoneInfo.Utc, IsWindows: false));

        Assert.False(status.Usable);
        var problem = Assert.Single(status.MissingRequired);
        Assert.Equal(SetupArea.Inverter, problem.Area);
        Assert.Contains("Pv:Inverter:Driver", problem.English);
    }

    [Fact]
    public async Task The_container_runs_the_configured_driver()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection([.. UsableSite.Pairs, new("Pv:Inverter:Driver", "SolaX"), new("Web:Enabled", "false")]);
        builder.Services.AddGleanvolt(builder.Configuration);
        await using var provider = builder.Services.BuildServiceProvider();

        Assert.IsType<SolaxInverterDriver>(provider.GetRequiredService<IInverterDriver>());
        Assert.Equal("solax", Assert.Single(provider.GetServices<IInverterDriverFactory>()).Id);
    }
}
