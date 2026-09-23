using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Components.Pages;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The car as /car shows it running (issue #214). The form at the top has its own tests in
/// <see cref="CarEditorFormTests"/>; what this page owes is the other half — the car the process
/// actually resolved, and the feed it actually started, which is not always the feed the
/// configuration asks for (#212).
/// </summary>
public class CarPageTests : PageTest
{
    private readonly FakeEvEditor _editor = new();

    public CarPageTests()
    {
        Services.AddSingleton<IEvEditor>(_editor);
        Services.AddSingleton<IServiceShutdown>(new FakeServiceShutdown());
        Services.AddSingleton(Options.Create(new WebOptions { PasswordHash = "hash" }));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>An ID.4 Pro: 77 kWh usable, three phases, 6–16 A.</summary>
    private static EvInfo Id4 { get; } = new(
        "id4", "The ID.4", "Volkswagen", "ID.4 Pro", 77, 0.9, 3, 6, 16, string.Empty);

    private sealed class StubFeed : IVehicleUpdateService
    {
        public string VehicleId => "id4";

        public string Manufacturer => "vw-website";

        public string DisplayName => "volkswagen.de";

        public VehicleSourceHealth Health { get; } = VehicleSourceHealth.Ok("signed in");

        public TimeSpan NextDelay => TimeSpan.FromMinutes(5);

        public Task<VehicleState?> FetchAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("A render must never fetch.");
    }

    private IRenderedComponent<Car> Page(EvInfo? car = null, int minAmps = 6, int maxAmps = 16)
    {
        var resolved = car ?? Id4;
        Services.AddSingleton(resolved);
        Services.AddSingleton(ChargingLimits.Intersect(minAmps, maxAmps, 3, resolved));

        return Render<Car>();
    }

    [Fact]
    public void An_undescribed_car_is_said_to_be_a_supported_installation()
    {
        var page = Page(EvInfo.Unknown);

        Assert.Contains("supported installation", page.Find("#car-unconfigured").TextContent);
    }

    [Fact]
    public void The_car_is_shown_beside_what_the_installation_allows()
    {
        // Three columns, because the interesting number is the third: a 32 A car behind a 16 A supply
        // charges at 16, and a page showing only the car's figures leaves somebody wondering why.
        var page = Page(Id4 with { MaxChargingCurrentAmps = 32 });

        var row = page.FindAll("table.grid tbody tr")[1].TextContent;
        Assert.Contains("32 A", row);
        Assert.Contains("16 A", row);
    }

    [Fact]
    public void The_feed_that_actually_started_is_the_one_named()
    {
        Services.AddSingleton<IVehicleUpdateService>(new StubFeed());

        var page = Page();

        Assert.Contains("volkswagen.de", page.Markup);
        Assert.Empty(page.FindAll("#no-feed"));
    }

    [Fact]
    public void No_feed_says_what_it_costs_and_what_it_does_not()
    {
        var page = Page();

        Assert.Contains("Charging is unaffected", page.Find("#no-feed").TextContent);
    }

    [Fact]
    public void A_portal_set_aside_by_a_live_feed_is_said_here_rather_than_only_in_the_log()
    {
        // #212 skips the Data Act portal beside a live feed with one startup line. This is the page
        // where an owner would otherwise wonder why the portal they configured does nothing.
        Services.AddSingleton(new ConfiguredVehicleFeed(new StubFeed(), "Vehicle:Website is configured, so the Data Act portal is not used."));

        var page = Page();

        Assert.Contains("not used", page.Find("#feed-set-aside").TextContent);
    }

    [Fact]
    public void An_own_topic_is_said_to_run_beside_a_manufacturer_feed()
    {
        Services.AddSingleton<IVehicleUpdateService>(new StubFeed());

        var page = Page(Id4 with { TelemetryTopic = "gleanvolt/vehicle/id4/state" });

        Assert.Contains("freshest reading wins", page.Find("#own-topic").TextContent);
    }

    [Fact]
    public void It_points_at_the_page_that_proves_an_account() =>
        Assert.Contains(Page().FindAll("a"), link => link.GetAttribute("href") == "/vehicle-portal");
}
