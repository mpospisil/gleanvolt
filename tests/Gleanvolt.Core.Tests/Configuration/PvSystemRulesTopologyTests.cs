using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Models;
using Gleanvolt.Devices;

namespace Gleanvolt.Core.Tests.Configuration;

/// <summary>
/// <c>Pv:Inverter:Topology</c> in the rules (issue #322): empty is the driver's first way, anything else must
/// be one the driver lists, and a driver reached one way takes nothing.
/// </summary>
public class PvSystemRulesTopologyTests
{
    private static readonly InverterDriverInfo Solax = new(
        "solax", "SolaX", new ModbusDefaults(502, 1, TimeSpan.FromMilliseconds(250)), new InverterCapabilities(true, true));

    private static readonly InverterDriverInfo Huawei = new(
        "huawei", "Huawei", new ModbusDefaults(502, 1, TimeSpan.FromSeconds(1)), new InverterCapabilities(false, false),
        [new InverterTopology("dongle", "SDongle"), new InverterTopology("emma", "EMMA")]);

    private static (PvSystemInfo Site, ConfigurationProblems Problems) Resolve(string driver, string topology)
    {
        var problems = new ConfigurationProblems();
        var pv = new PvSystemOptions
        {
            Inverter = new PvInverterOptions { Driver = driver, Topology = topology, Host = "192.168.2.10" },
            Chargers = [new PvChargerOptions { Host = "192.168.2.6" }],
        };
        return (PvSystemRules.Resolve(pv, "Inverter", problems, [Solax, Huawei]), problems);
    }

    [Fact]
    public void Empty_is_the_drivers_first_way()
    {
        var (site, problems) = Resolve("huawei", "");

        Assert.Empty(problems);
        Assert.Equal("dongle", site.InverterTopology);
    }

    [Theory]
    [InlineData("emma")]
    [InlineData(" EMMA ")]
    public void A_way_the_driver_lists_is_taken_in_any_case(string configured)
    {
        var (site, problems) = Resolve("huawei", configured);

        Assert.Empty(problems);
        Assert.Equal("emma", site.InverterTopology);
    }

    [Fact]
    public void A_way_the_driver_does_not_list_is_a_problem_naming_the_ones_it_does()
    {
        var (_, problems) = Resolve("huawei", "smartlogger");

        var problem = Assert.Single(problems);
        Assert.Contains("Pv:Inverter:Topology ('smartlogger')", problem);
        Assert.Contains("dongle, emma", problem);
        Assert.Equal("Config.PvTopology", problems.Phrases[0]!.Key);
    }

    [Fact]
    public void A_driver_reached_one_way_takes_nothing()
    {
        var (site, problems) = Resolve("solax", "emma");

        var problem = Assert.Single(problems);
        Assert.Contains("cannot be used with the SolaX driver", problem);
        Assert.Equal("Config.PvTopologyNone", problems.Phrases[0]!.Key);
        Assert.Equal(string.Empty, site.InverterTopology);
    }

    [Fact]
    public void A_driver_reached_one_way_has_no_topology()
    {
        var (site, problems) = Resolve("solax", "");

        Assert.Empty(problems);
        Assert.Equal(string.Empty, site.InverterTopology);
    }
}
