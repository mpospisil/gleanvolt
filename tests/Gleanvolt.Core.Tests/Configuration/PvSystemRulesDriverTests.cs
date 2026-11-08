using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Models;
using Gleanvolt.Devices;

namespace Gleanvolt.Core.Tests.Configuration;

/// <summary>
/// <c>Pv:Inverter:Driver</c> in the rules (issue #318): empty means SolaX, anything else must be a driver
/// the caller has, and that driver's Modbus defaults fill in what the address leaves out.
/// </summary>
public class PvSystemRulesDriverTests
{
    private static readonly InverterDriverInfo Solax = new(
        "solax", "SolaX", new ModbusDefaults(502, 1, TimeSpan.FromMilliseconds(250)), new InverterCapabilities(true, true));

    // A family that differs from SolaX in every way the rules look at.
    private static readonly InverterDriverInfo Acme = new(
        "acme", "Acme", new ModbusDefaults(6607, 0, TimeSpan.FromMilliseconds(50)), new InverterCapabilities(false, false));

    private static readonly IReadOnlyList<InverterDriverInfo> Drivers = [Solax, Acme];

    private static PvSystemOptions Site(string driver = "", int? port = null, byte? unitId = null, string unmetered = "") => new()
    {
        Inverter = new PvInverterOptions
        {
            Driver = driver,
            Host = "192.168.2.10",
            Port = port,
            UnitId = unitId,
            UnmeteredGridPhase = unmetered,
        },
        Chargers = [new PvChargerOptions { Host = "192.168.2.6" }],
    };

    private static (PvSystemInfo Site, ConfigurationProblems Problems) Resolve(PvSystemOptions pv, IReadOnlyList<InverterDriverInfo>? drivers = null)
    {
        var problems = new ConfigurationProblems();
        return (PvSystemRules.Resolve(pv, "Inverter", problems, drivers ?? Drivers), problems);
    }

    [Fact]
    public void Empty_means_solax_so_an_existing_configuration_needs_no_change()
    {
        var (site, problems) = Resolve(Site());

        Assert.Empty(problems);
        Assert.Equal("solax", site.InverterDriver);
    }

    [Theory]
    [InlineData("acme")]
    [InlineData(" ACME ")]
    public void A_known_driver_is_taken_in_any_case(string configured)
    {
        var (site, problems) = Resolve(Site(driver: configured));

        Assert.Empty(problems);
        Assert.Equal("acme", site.InverterDriver);
    }

    [Fact]
    public void An_unknown_driver_is_a_problem_naming_the_key_and_the_known_drivers()
    {
        var (_, problems) = Resolve(Site(driver: "huwei"));

        var problem = Assert.Single(problems);
        Assert.Contains("Pv:Inverter:Driver", problem);
        Assert.Contains("'huwei'", problem);
        Assert.Contains("solax, acme", problem);
        Assert.Equal("Config.PvDriver", problems.Phrases[0]!.Key);
    }

    [Fact]
    public void The_drivers_defaults_fill_in_the_port_and_unit_id_left_empty()
    {
        var (site, _) = Resolve(Site(driver: "acme"));

        Assert.Equal(6607, site.Inverter.Connection.Port);
        Assert.Equal(0, site.Inverter.Connection.UnitId);
        Assert.Equal(TimeSpan.FromMilliseconds(50), site.Inverter.Connection.MinRequestInterval);
    }

    [Fact]
    public void A_configured_port_and_unit_id_win_over_the_drivers_defaults()
    {
        var (site, _) = Resolve(Site(driver: "acme", port: 502, unitId: 3));

        Assert.Equal(502, site.Inverter.Connection.Port);
        Assert.Equal(3, site.Inverter.Connection.UnitId);
    }

    [Fact]
    public void A_blind_phase_is_refused_for_a_driver_without_per_phase_output()
    {
        var (_, problems) = Resolve(Site(driver: "acme", unmetered: "L3"));

        var problem = Assert.Single(problems);
        Assert.Contains("Pv:Inverter:UnmeteredGridPhase", problem);
        Assert.Contains("Acme", problem);
    }

    [Fact]
    public void A_blind_phase_is_accepted_for_a_driver_with_per_phase_output()
    {
        var (site, problems) = Resolve(Site(driver: "solax", unmetered: "L3"));

        Assert.Empty(problems);
        Assert.Equal(GridPhase.L3, site.UnmeteredGridPhase);
    }

    [Fact]
    public void A_caller_that_names_no_drivers_is_held_to_the_default()
    {
        Assert.Empty(PvSystemRules.Validate(Site()));
        Assert.Contains(PvSystemRules.Validate(Site(driver: "acme")), problem => problem.Contains("Pv:Inverter:Driver", StringComparison.Ordinal));
    }
}
