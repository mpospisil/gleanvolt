using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web;
using Gleanvolt.Web.Components.Pages;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// On-demand vehicle state (issue #168): the dashboard shows the car's battery and range only when
/// somebody has asked for them.
///
/// <para>The reading has always existed and the API has always served it. What this mode changes is
/// that the page stops putting a number in front of an eye that will read it as current — on the EU
/// Data Act portal a reading is routinely two to four hours behind, and the age beside it is in
/// smaller type than the figure.</para>
/// </summary>
public class DashboardOnDemandTests : PageTest
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 8, 0, 0, TimeSpan.Zero);

    private static VehicleState Car(double soc = 64, double range = 312) =>
        new(Now.AddMinutes(-20), SocPercent: soc, RangeKm: range,
            ChargeState: VehicleChargeState.Idle, PlugState: VehiclePlugState.Disconnected,
            SourceId: "vw-group");

    private FakeVehicleStateRefresh Arrange(bool onDemand, VehicleState? held, FakeVehicleStateRefresh refresh)
    {
        var holder = new VehicleStateHolder();

        if (held is not null)
        {
            holder.Set(held);
        }

        // The page renders a "waiting for the first poll" placeholder until a status lands, and the
        // vehicle section is inside what that replaces -- so a status is a precondition for testing
        // anything about the card at all.
        var status = new ChargeControlStatusHolder();
        status.Set(Statuses.Sample(Now, ChargeControlMode.Solar));
        Services.AddSingleton(status);
        Services.AddSingleton(holder);
        Services.AddSingleton<IVehicleTelemetry>(holder);
        Services.AddSingleton(holder.Comparison);
        Services.AddSingleton<IVehicleStateRefresh>(refresh);
        Services.AddSingleton(new VehicleDisplayOptions(TimeSpan.FromHours(12), 77, 0.9, onDemand));
        Services.AddSingleton(EvInfo.Unknown);
        return refresh;
    }

    [Fact]
    public void Ambient_mode_is_unchanged_and_shows_the_held_reading()
    {
        Arrange(onDemand: false, held: Car(), new FakeVehicleStateRefresh(Car()));

        var page = RenderDashboard();

        // "Car battery" rather than the bare percentage: the home battery's SOC is on this page too.
        Assert.Contains("Car battery", page.Markup);
        Assert.Empty(page.FindAll("#vehicle-ask"));
    }

    /// <summary>The point of the mode: a held reading is not put on screen unasked.</summary>
    [Fact]
    public void On_demand_shows_no_battery_until_it_is_asked_for()
    {
        var refresh = Arrange(onDemand: true, held: Car(soc: 64), new FakeVehicleStateRefresh(Car(soc: 64)));

        var page = RenderDashboard();

        Assert.DoesNotContain("Car battery", page.Markup);
        Assert.Single(page.FindAll("#vehicle-ask"));
        Assert.Equal(0, refresh.Asks);
    }

    [Fact]
    public void Asking_fetches_once_and_shows_what_came_back()
    {
        var refresh = Arrange(onDemand: true, held: null, new FakeVehicleStateRefresh(Car(soc: 41, range: 210)));

        var page = RenderDashboard();
        page.Find("#vehicle-ask").Click();

        Assert.Equal(1, refresh.Asks);
        Assert.Contains("Car battery", page.Markup);
        Assert.Contains("41%", page.Markup);
        Assert.Contains("210 km", page.Markup);
    }

    /// <summary>Nothing fetches on its own — the whole contract of the mode, in one assertion.</summary>
    [Fact]
    public void Rendering_never_asks_by_itself()
    {
        var refresh = Arrange(onDemand: true, held: Car(), new FakeVehicleStateRefresh(Car()));

        var page = RenderDashboard();
        page.Render();

        Assert.Equal(0, refresh.Asks);
    }

    [Fact]
    public void A_failed_ask_says_so_rather_than_showing_a_number()
    {
        var refresh = Arrange(onDemand: true, held: null,
            new FakeVehicleStateRefresh(failure: "the portal did not answer"));

        var page = RenderDashboard();
        page.Find("#vehicle-ask").Click();

        Assert.Contains("did not answer", page.Markup);
        Assert.DoesNotContain("Car battery", page.Markup);
    }

    [Fact]
    public void With_no_feed_there_is_nothing_to_ask_and_the_button_is_disabled()
    {
        Arrange(onDemand: true, held: null, new FakeVehicleStateRefresh(canRefresh: false));

        var page = RenderDashboard();

        Assert.Contains("nothing to ask", page.Markup);
        Assert.True(page.Find("#vehicle-ask").HasAttribute("disabled"));
    }

    private IRenderedComponent<Dashboard> RenderDashboard()
    {
        Services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now, TimeZoneInfo.Utc));
        return Render<Dashboard>();
    }
}
