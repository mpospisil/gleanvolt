using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web;
using Gleanvolt.Web.Components.Pages;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The whole car, on one page (issues #214, #227).
///
/// <para>The form at the top keeps its own tests in <see cref="CarEditorFormTests"/>. The cases below run once per
/// <see cref="VehicleFeed"/> value where it matters, so that nothing is silent and nothing is red for the wrong
/// reason. The built-in manufacturer feeds, their sign-in and the Data Act diagnostics went in #477.</para>
/// </summary>
public class CarPageTests : PageTest
{
    /// <summary>When the car reported, for the tests that show a reading.</summary>
    private static readonly DateTimeOffset Reported = new(2026, 9, 2, 10, 29, 46, TimeSpan.Zero);

    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private readonly FakeEvEditor _editor = new();

    /// <summary>The external providers' sessions, for the External feed's panel (#408).</summary>
    private readonly FakeExternalProviders _providers = new();

    /// <summary>Now, for the reading ages the card prints (#178). Two hours after <see cref="Reported"/>.</summary>
    private readonly FixedTimeProvider _time =
        new(new DateTimeOffset(2026, 9, 2, 12, 29, 46, TimeSpan.Zero), Prague);

    /// <summary>What the feed is holding. Empty by default: the install with no feed switched on.</summary>
    private readonly VehicleStateHolder _held = new();

    private IVehicleStateRefresh _refresh = new FakeVehicleStateRefresh(canRefresh: false);

    /// <summary>A UI with a login, which is what makes the form editable.</summary>
    private WebOptions _web = new() { PasswordHash = "hash" };

    public CarPageTests()
    {
        Services.AddSingleton<IEvEditor>(_editor);
        Services.AddSingleton<IServiceShutdown>(new FakeServiceShutdown());
        Services.AddSingleton<IExternalProviders>(_providers);
        Services.AddSingleton<IRetiredCarFeed>(new FakeRetiredCarFeed());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>An ID.4 Pro: 77 kWh usable, three phases, 6–16 A.</summary>
    private static EvInfo Id4 { get; } = new(
        "id4", "The ID.4", "Volkswagen", "ID.4 Pro", 77_000, 0.9, 3, 6, 16);

    private sealed class StubFeed : IVehicleUpdateService
    {
        public string VehicleId => "id4";

        public string Manufacturer => "external";

        public string DisplayName => "Volkswagen plugin";

        public VehicleSourceHealth Health { get; } = VehicleSourceHealth.Ok("signed in");

        public TimeSpan NextDelay => TimeSpan.FromMinutes(5);

        public Task<VehicleState?> FetchAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("A render must never fetch.");
    }

    private IRenderedComponent<Car> Page(
        EvInfo? car = null,
        VehicleFeed feed = VehicleFeed.ChargeOnly,
        int minAmps = 6,
        int maxAmps = 16)
    {
        var resolved = car ?? Id4;
        _editor.RunningFeed = feed;

        Services.AddSingleton(resolved);
        Services.AddSingleton(Options.Create(_web));
        Services.AddSingleton(new Gleanvolt.Web.Auth.WebPasswordState(_web.PasswordHash));
        Services.AddSingleton(ChargingLimits.Intersect(minAmps, maxAmps, 3, resolved));
        Services.AddSingleton(_refresh);
        Services.AddSingleton<TimeProvider>(_time);
        Services.AddSingleton<IVehicleTelemetry>(_held);

        return Render<Car>();
    }

    // ---------------------------------------------------------------- the car the process resolved

    [Fact]
    public void Shows_the_car_the_installation_and_which_of_them_is_in_effect()
    {
        // The third column is the interesting one: a page showing only the car's figures would leave
        // somebody wondering why a 32 A car charges at 16.
        var page = Page(Id4 with { Phases = 1, MinChargingCurrentAmps = 8, MaxChargingCurrentAmps = 32 });

        Assert.Contains("In effect", page.Markup);
        Assert.Contains("narrower of the two", page.Markup);

        // The car refuses below 8; the installation gives out at 16; the car can only use one phase.
        var row = page.FindAll("table.grid tbody tr").Last();
        Assert.Contains("1", row.TextContent);
    }

    [Fact]
    public void An_undescribed_car_is_said_to_be_a_supported_installation()
    {
        var page = Page(EvInfo.Unknown);

        Assert.Contains("supported setup", page.Find("#car-unconfigured").TextContent);
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

        var page = Page(feed: VehicleFeed.External);

        Assert.Contains("Volkswagen plugin", page.Markup);
        Assert.Empty(page.FindAll("#no-feed"));
    }

    [Fact]
    public void No_feed_says_what_it_costs_and_what_it_does_not()
    {
        var page = Page();

        Assert.Contains("Charging is unaffected", page.Find("#no-feed").TextContent);
    }

    // -------------------------------------------------------------------- one page, one outline (#227)

    /// <summary>
    /// The heading outline: one <c>h1</c>, above every <c>h2</c>, whatever reads the car.
    /// </summary>
    [Theory]
    [InlineData(VehicleFeed.External)]
    [InlineData(VehicleFeed.ChargeOnly)]
    public void There_is_one_h1_and_it_precedes_every_h2(VehicleFeed feed)
    {
        var page = Page(feed: feed);
        var headings = page.FindAll("h1, h2");

        Assert.Equal("H1", headings[0].TagName);
        Assert.Single(headings, heading => heading.TagName == "H1");
    }

    /// <summary>
    /// The one negative that has to hold for every feed: a render is not a press. Opening this page
    /// must never ask the car.
    /// </summary>
    [Theory]
    [InlineData(VehicleFeed.External)]
    [InlineData(VehicleFeed.ChargeOnly)]
    public void A_render_never_asks_the_car(VehicleFeed feed)
    {
        var refresh = new FakeVehicleStateRefresh(new VehicleState(Reported, SocPercent: 64));
        _refresh = refresh;
        Services.AddSingleton<IVehicleUpdateService>(new StubFeed());

        var page = Page(feed: feed);
        page.Render();

        Assert.Equal(0, refresh.Asks);
    }

    /// <summary>
    /// The rule #204 set for the device addresses, which #214 applied to these fields and the merge
    /// must not quietly drop: without a login, anyone on the LAN could change what reads this car, so
    /// the form is shown and not usable.
    /// </summary>
    [Fact]
    public void Without_a_login_the_form_is_still_read_only()
    {
        _web = new WebOptions();

        var page = Page(feed: VehicleFeed.External);

        Assert.Contains("Read-only", page.Find("#car-readonly").TextContent);
        Assert.Empty(page.FindAll("#car-save"));
    }

    /// <summary>Nothing on the page points at the route that is now a redirect.</summary>
    [Fact]
    public void Nothing_links_to_the_page_that_was_folded_in() =>
        Assert.DoesNotContain(
            Page().FindAll("a"), link => link.GetAttribute("href") == "/vehicle-portal");

    /// <summary>
    /// The bookmark, the README's anchors and the old <c>docs/VW_PORTAL_SETUP.md</c> all pointed at
    /// <c>/vehicle-portal</c> since #137. Cheaper to keep the route than to chase every one of them.
    /// </summary>
    [Fact]
    public void The_old_route_redirects_to_the_car()
    {
        Render<VehiclePortalRedirect>();

        Assert.EndsWith("/car", Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri);
    }

    // ---------------------------------------------------------------- an external provider (#408)

    [Fact]
    public void An_external_provider_shows_its_session_and_is_asked_through_the_feed()
    {
        _providers.Set(new ExternalProviderStatus(
            "vehicle", "Car", "/providers/vehicle", HasToken: true, ExternalProviderState.Connected,
            "vehicle-sample", "Vehicle sample", "1.0", Since: Reported, LastDataAt: Reported));
        var refresh = new FakeVehicleStateRefresh(new VehicleState(Reported, SocPercent: 52, SourceId: "Vehicle sample"));
        _refresh = refresh;
        Services.AddSingleton<IVehicleUpdateService>(new ExternalStubFeed());

        var page = Page(feed: VehicleFeed.External);

        Assert.Contains("Vehicle sample", page.Find("#car-ask-external").TextContent);
        Assert.Contains("Vehicle sample", page.Find("#external-vehicle-state").TextContent);

        page.Find("#car-ask").Click();

        Assert.Equal(1, refresh.Asks);
        Assert.Contains("52%", page.Find("#car").TextContent);
    }

    [Fact]
    public void An_external_provider_that_needs_its_owner_says_so_on_its_panel()
    {
        _providers.Set(new ExternalProviderStatus(
            "vehicle", "Car", "/providers/vehicle", HasToken: true, ExternalProviderState.Connected,
            "vehicle-sample", "Vehicle sample", "1.0", Since: Reported,
            Healthy: false, StatusMessage: "The login expired.", NeedsOwner: true));
        Services.AddSingleton<IVehicleUpdateService>(new ExternalStubFeed());

        var page = Page(feed: VehicleFeed.External);

        var line = page.Find("#external-vehicle-unhealthy");
        Assert.Contains("needs you: The login expired.", line.TextContent);
        Assert.Contains("error", line.ClassList);
    }

    [Fact]
    public void An_external_provider_with_a_page_shows_it_in_a_frame_with_a_link_to_a_new_tab()
    {
        _providers.Set(new ExternalProviderStatus(
            "vehicle", "Car", "/providers/vehicle", HasToken: true, ExternalProviderState.Connected,
            "volkswagen", "Volkswagen", "1.0", Since: Reported, PageUrl: new Uri("http://192.168.2.7:8095/")));
        Services.AddSingleton<IVehicleUpdateService>(new ExternalStubFeed());

        var page = Page(feed: VehicleFeed.External);

        // Gleanvolt's own redirect, never the provider's address with a ticket in it.
        var frame = page.Find("#external-vehicle-frame");
        Assert.Equal("/external/vehicle/page", frame.GetAttribute("src"));
        Assert.Equal("The page of Volkswagen", frame.GetAttribute("title"));

        var link = page.Find("#external-vehicle-page-link");
        Assert.Equal("/external/vehicle/page", link.GetAttribute("href"));
        Assert.Equal("_blank", link.GetAttribute("target"));
        Assert.Equal("Open in a new tab", link.TextContent);
        Assert.DoesNotContain("192.168.2.7", page.Markup);
    }

    [Fact]
    public void A_provider_without_a_page_or_not_connected_shows_no_frame()
    {
        _providers.Set(new ExternalProviderStatus(
            "vehicle", "Car", "/providers/vehicle", HasToken: true, ExternalProviderState.Disconnected,
            "volkswagen", "Volkswagen", "1.0", Since: Reported, PageUrl: new Uri("http://192.168.2.7:8095/")));
        Services.AddSingleton<IVehicleUpdateService>(new ExternalStubFeed());

        var page = Page(feed: VehicleFeed.External);

        Assert.Empty(page.FindAll("#external-vehicle-frame"));

        _providers.Set(new ExternalProviderStatus(
            "vehicle", "Car", "/providers/vehicle", HasToken: true, ExternalProviderState.Connected,
            "vehicle-sample", "Vehicle sample", "1.0", Since: Reported));

        page.WaitForAssertion(() => Assert.Contains("Vehicle sample", page.Find("#external-vehicle-state").TextContent));
        Assert.Empty(page.FindAll("#external-vehicle-frame"));
    }

    private sealed class ExternalStubFeed : IVehicleUpdateService
    {
        public string VehicleId => "id4";

        public string Manufacturer => "external";

        public string DisplayName => "Vehicle sample";

        public bool IsPushed => true;

        public VehicleSourceHealth Health { get; } = VehicleSourceHealth.Ok("Vehicle sample is connected");

        public TimeSpan NextDelay => Timeout.InfiniteTimeSpan;

        public Task<VehicleState?> FetchAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("A render must never fetch.");
    }

    // ---------------------------------------------------------------- asking a live feed (#227)

    [Fact]
    public void A_live_feed_is_asked_with_the_same_call_the_worker_makes()
    {
        var refresh = new FakeVehicleStateRefresh(new VehicleState(
            Reported, SocPercent: 64, RangeMetres: 312_000, SourceId: "volkswagen"));
        _refresh = refresh;
        Services.AddSingleton<IVehicleUpdateService>(new StubFeed());

        var page = Page(feed: VehicleFeed.External);

        Assert.Equal(0, refresh.Asks);
        page.Find("#car-ask").Click();

        Assert.Equal(1, refresh.Asks);
        Assert.Contains("64%", page.Find("#car").TextContent);
        Assert.Contains("312 km", page.Find("#car").TextContent);
    }

    /// <summary>
    /// A feed's fetch writes to the holder, so a press here reaches the dashboard's card, and the page says so.
    /// </summary>
    [Fact]
    public void A_live_feeds_press_says_the_dashboard_has_caught_up()
    {
        _refresh = new FakeVehicleStateRefresh(new VehicleState(Reported, SocPercent: 64, SourceId: "volkswagen"));
        Services.AddSingleton<IVehicleUpdateService>(new StubFeed());

        var page = Page(feed: VehicleFeed.External);

        page.Find("#car-ask").Click();

        Assert.Contains("the dashboard card now shows it too", page.Find("#car-ask-source").TextContent);
    }

    /// <summary>
    /// A feed that cannot answer says what is stopping it, and what was already held stays on screen:
    /// an answer known to be old beats no answer, provided its age is beside it.
    /// </summary>
    [Fact]
    public void A_live_feed_that_cannot_answer_says_why_and_keeps_the_held_reading()
    {
        _refresh = new FakeVehicleStateRefresh(
            new VehicleState(Reported, SocPercent: 61),
            failure: "the Volkswagen plugin: the session is gone — sign in again");
        Services.AddSingleton<IVehicleUpdateService>(new StubFeed());

        var page = Page(feed: VehicleFeed.External);
        page.Find("#car-ask").Click();

        Assert.Contains("the session is gone", page.Find("#car-ask-failed").TextContent);
        Assert.Contains("61%", page.Find("#car").TextContent);
        Assert.Contains("already had this reading", page.Find("#car-ask-source").TextContent);
    }

    /// <summary>Charge-only: nothing to press, and nothing that reads as an unfinished form.</summary>
    [Fact]
    public void Charge_only_reads_as_a_supported_installation_rather_than_an_error()
    {
        var page = Page();

        Assert.Empty(page.FindAll("#car-ask"));
        Assert.Empty(page.FindAll(".error"));
        Assert.Contains("nothing reads this car", page.Find("#car-ask-charge-only").TextContent);
    }

    [Theory]
    [InlineData(VehicleFeed.ChargeOnly)]
    [InlineData(VehicleFeed.External)]
    public void Every_string_on_the_car_page_comes_from_a_resource(VehicleFeed feed)
    {
        Services.AddSingleton<IRetiredCarFeed>(new FakeRetiredCarFeed(CarFeedPlugin.Volkswagen));
        var page = PseudoLocale.Render(() => Page(Id4, feed));

        // Only names: the car's and the plugin's.
        PseudoLocale.AssertTranslated(
            page.Markup,
            "The ID.4", "Volkswagen ID.4 Pro", "id4", "Volkswagen");
    }
}
