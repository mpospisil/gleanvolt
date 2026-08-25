using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Components.Pages;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The read-only view of the installation (issue #111). What it has to get right is what a
/// configuration page is for: showing what is actually configured, saying plainly when something is
/// not, and never implying that anything on it can be edited here.
/// </summary>
public class PvSystemPageTests : BunitContext
{
    private static PvDeviceInfo Device(string id, string name, string model, string host, int port = 502) =>
        new(id, name, model, new DeviceConfig { Host = host, Port = port });

    private static PvSystemInfo Site(
        string id = "home-roof",
        string name = "Home Roof",
        string address = "Krásného 12, Praha",
        double? latitude = 50.0755,
        double? longitude = 14.4378,
        double? azimuth = 172,
        double? tilt = 35,
        double? capacityKwp = 9.2,
        double? inverterCapacityKw = 8,
        double? lossFactor = 0.9,
        DateOnly? installDate = null,
        IReadOnlyList<PvDeviceInfo>? chargers = null) =>
        new(
            id,
            name,
            address,
            latitude,
            longitude,
            azimuth,
            tilt,
            capacityKwp,
            inverterCapacityKw,
            lossFactor,
            installDate ?? new DateOnly(2024, 5, 1),
            Device("Inverter", string.Empty, "SolaX X3-HYB-G4 PRO", "192.168.2.10"),
            chargers ?? [Device("wallbox", "Garage wallbox", "SolaX X3-HAC", "192.168.2.6")]);

    private IRenderedComponent<PvSystem> Render(PvSystemInfo? site = null, params string[] deprecations)
    {
        Services.AddSingleton(site ?? Site());
        Services.AddSingleton(new PvSystemNotices(deprecations));

        return Render<PvSystem>();
    }

    [Fact]
    public void Shows_what_the_system_is_called_and_where_it_is()
    {
        var page = Render();

        Assert.Contains("Home Roof", page.Markup);
        Assert.Contains("home-roof", page.Markup);
        Assert.Contains("Krásného 12, Praha", page.Markup);
        Assert.Contains("50.0755, 14.4378", page.Markup);
    }

    [Fact]
    public void Spells_out_which_way_the_array_faces()
    {
        // 172° is only checkable by someone who already thinks in degrees; "south" is checkable by
        // anyone who has stood in the garden, and this is the field that goes silently wrong.
        var page = Render();

        Assert.Contains("172° (south)", page.Markup);
    }

    [Theory]
    [InlineData(0, "north")]
    [InlineData(90, "east")]
    [InlineData(225, "south-west")]
    [InlineData(359, "north")]
    public void Names_every_bearing_by_its_nearest_point(double bearing, string expected)
    {
        var page = Render(Site(azimuth: bearing));

        Assert.Contains($"({expected})", page.Markup);
    }

    [Fact]
    public void Shows_the_array_figures_with_their_units()
    {
        var page = Render();

        Assert.Contains("9.2 kWp", page.Markup);
        Assert.Contains("8 kW", page.Markup);
        Assert.Contains("0.90", page.Markup);
        Assert.Contains("2024-05-01", page.Markup);
        Assert.Contains("35°", page.Markup);
    }

    [Fact]
    public void Lists_the_devices_with_their_models_and_addresses()
    {
        var page = Render();

        Assert.Contains("SolaX X3-HYB-G4 PRO", page.Markup);
        Assert.Contains("192.168.2.10:502", page.Markup);
        Assert.Contains("Garage wallbox", page.Markup);
        Assert.Contains("SolaX X3-HAC", page.Markup);
        Assert.Contains("192.168.2.6:502", page.Markup);
        Assert.Contains("wallbox", page.Markup);
    }

    [Fact]
    public void An_unset_value_reads_as_unset_rather_than_as_zero()
    {
        // The installation that has not been described yet -- which is every installation the moment
        // before someone describes it. Zeroes here would be a site in the Atlantic facing north.
        var page = Render(Site(
            id: string.Empty,
            name: string.Empty,
            address: string.Empty,
            latitude: null,
            longitude: null,
            azimuth: null,
            tilt: null,
            capacityKwp: null,
            inverterCapacityKw: null,
            lossFactor: null));

        Assert.DoesNotContain("0.0000", page.Markup);
        Assert.DoesNotContain("(north)", page.Markup);
        Assert.Contains("—", page.Markup);
    }

    [Fact]
    public void Says_how_to_claim_an_id_when_there_is_none()
    {
        var page = Render(Site(id: string.Empty));

        Assert.Contains("Pv__Id", page.Markup);
    }

    [Fact]
    public void Says_what_no_coordinates_costs()
    {
        var page = Render(Site(latitude: null, longitude: null));

        Assert.Contains("no weather is recorded", page.Markup);
        Assert.Contains("Pv__Latitude", page.Markup);
    }

    [Fact]
    public void Lists_the_keys_that_still_need_moving()
    {
        // The same lines the startup log carries, on a page that does not scroll away.
        var page = Render(null, "Solax:Inverter is deprecated and is being used instead of Pv:Inverter.");

        Assert.Contains("Configuration to move", page.Markup);
        Assert.Contains("Solax:Inverter is deprecated", page.Markup);
    }

    [Fact]
    public void Says_nothing_about_migration_when_there_is_nothing_to_migrate()
    {
        var page = Render();

        Assert.DoesNotContain("Configuration to move", page.Markup);
    }

    [Fact]
    public void Offers_nothing_to_click()
    {
        // Read-only is the point: everything here was resolved once at startup, and the Modbus clients
        // were built from it. A control that edited it would be editing a copy of a settled decision.
        var page = Render();

        Assert.Empty(page.FindAll("button"));
        Assert.Empty(page.FindAll("input"));
        Assert.Empty(page.FindAll("select"));
        Assert.Contains("restarting the controller", page.Markup);
    }
}
