using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// A bUnit context with the installation registered, which every page needs since the browser tab
/// carries the system's name (issue #111). A base class rather than a line in each fixture: the
/// alternative is a page test that compiles, renders nothing, and reports a missing service.
/// </summary>
public abstract class PageTest : BunitContext
{
    protected PageTest()
    {
        Services.AddSingleton(Sites.Home);
    }
}

/// <summary>Installations for a page to be about.</summary>
internal static class Sites
{
    public static PvSystemInfo Home { get; } = new(
        Id: "home-roof",
        Name: "Home Roof",
        Address: "Krásného 12, Praha",
        Latitude: 50.0755,
        Longitude: 14.4378,
        AzimuthDegrees: 172,
        TiltDegrees: 35,
        CapacityKwp: 9.2,
        InverterCapacityKw: 8,
        LossFactor: 0.9,
        InstallDate: new DateOnly(2024, 5, 1),
        Inverter: new PvDeviceInfo("Inverter", string.Empty, "SolaX X3-HYB-G4 PRO", new DeviceConfig { Host = "192.168.2.10" }),
        Chargers: [new PvDeviceInfo("wallbox", "Garage wallbox", "SolaX X3-HAC", new DeviceConfig { Host = "192.168.2.6" })]);
}
