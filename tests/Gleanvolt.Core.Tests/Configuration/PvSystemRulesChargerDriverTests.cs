using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Models;
using Gleanvolt.Devices;

namespace Gleanvolt.Core.Tests.Configuration;

/// <summary>
/// <c>Pv:Chargers:0:Driver</c> in the rules (issue #349), as <c>Pv:Inverter:Driver</c> is: empty means
/// SolaX, anything else must be a driver the caller has, and that driver's Modbus defaults fill in what the
/// address leaves out. And the charge-control limits against the driver's capabilities.
/// </summary>
public class PvSystemRulesChargerDriverTests
{
    private static readonly EvChargerDriverInfo Solax = new(
        "solax", "SolaX", EvChargerTransport.Modbus, new ModbusDefaults(502, 1, TimeSpan.FromMilliseconds(250)),
        new EvChargerCapabilities(6, 32, 0, 3, false, true));

    private static readonly EvChargerDriverInfo Wallco = new(
        "wallco", "Wallco", EvChargerTransport.Modbus, new ModbusDefaults(5020, 2, TimeSpan.FromMilliseconds(50)),
        new EvChargerCapabilities(6, 16, 0, 1, false, false));

    private static (PvSystemInfo Site, ConfigurationProblems Problems) Resolve(string driver = "", int? port = null, byte? unitId = null)
    {
        var problems = new ConfigurationProblems();
        var pv = new PvSystemOptions
        {
            Inverter = new PvInverterOptions { Host = "192.168.2.10" },
            Chargers = [new PvChargerOptions { Host = "192.168.2.6", Driver = driver, Port = port, UnitId = unitId }],
        };
        return (PvSystemRules.Resolve(pv, "Inverter", problems, chargerDrivers: [Solax, Wallco]), problems);
    }

    [Fact]
    public void Empty_means_solax_so_an_existing_configuration_needs_no_change()
    {
        var (site, problems) = Resolve();

        Assert.Empty(problems);
        Assert.Equal("solax", site.Chargers[0].Driver);
    }

    [Theory]
    [InlineData("wallco")]
    [InlineData(" WALLCO ")]
    public void A_known_driver_is_taken_in_any_case(string configured)
    {
        var (site, problems) = Resolve(configured);

        Assert.Empty(problems);
        Assert.Equal("wallco", site.Chargers[0].Driver);
    }

    [Fact]
    public void An_unknown_driver_is_a_problem_naming_the_key_and_the_known_drivers()
    {
        var (_, problems) = Resolve("goe");

        var problem = Assert.Single(problems);
        Assert.Contains("Pv:Chargers:0:Driver ('goe')", problem);
        Assert.Contains("solax, wallco", problem);
        Assert.Equal("Config.PvChargerDriver", problems.Phrases[0]!.Key);
    }

    [Fact]
    public void The_drivers_defaults_fill_in_the_port_and_unit_id_left_empty()
    {
        var connection = Resolve("wallco").Site.Chargers[0].Connection;

        Assert.Equal(5020, connection.Port);
        Assert.Equal((byte)2, connection.UnitId);
        Assert.Equal(TimeSpan.FromMilliseconds(50), connection.MinRequestInterval);
    }

    [Fact]
    public void A_configured_port_and_unit_id_win_over_the_drivers()
    {
        var connection = Resolve("wallco", port: 502, unitId: 1).Site.Chargers[0].Connection;

        Assert.Equal(502, connection.Port);
        Assert.Equal((byte)1, connection.UnitId);
    }

    [Fact]
    public void Limits_within_what_the_charger_can_do_pass()
    {
        var problems = new ConfigurationProblems();

        PvSystemRules.CheckChargeLimits(Solax, minAmps: 6, maxAmps: 32, phases: 3, problems);

        Assert.Empty(problems);
    }

    [Fact]
    public void Limits_the_charger_cannot_honour_are_each_a_problem()
    {
        var problems = new ConfigurationProblems();

        PvSystemRules.CheckChargeLimits(Wallco, minAmps: 5, maxAmps: 20, phases: 3, problems);

        Assert.Equal(["Config.ChargerMinAmps", "Config.ChargerMaxAmps", "Config.ChargerPhases"], problems.Phrases.Select(phrase => phrase!.Key));
        Assert.Contains("MaxChargingCurrentAmps (20 A) is above the highest current the Wallco charger accepts (16 A)", problems[1]);
    }
}
