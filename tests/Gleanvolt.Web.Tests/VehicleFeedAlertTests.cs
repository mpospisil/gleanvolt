using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The band the layout carries on every page when the car feed has stopped and needs a person
/// (issue #140).
///
/// <para>One state only. Everything else a feed can be — old, degraded, waiting for its first
/// delivery — clears itself and belongs beside the reading it qualifies; this one does not clear
/// itself, and the longer it goes unseen the more a stopped feed looks like a parked car.</para>
/// </summary>
public class VehicleFeedAlertTests : PageTest
{
    private readonly ChargeControlStatusHolder _holder = new();

    public VehicleFeedAlertTests() => Services.AddSingleton(_holder);

    /// <summary>A feed whose health the test moves, as a real one's moves between polls.</summary>
    private sealed class StubFeed(VehicleSourceHealth health) : IVehicleUpdateService
    {
        public string VehicleId => "id4";

        public string Manufacturer => "vw-group";

        public VehicleSourceHealth Health { get; set; } = health;

        public TimeSpan NextDelay => TimeSpan.FromMinutes(15);

        public Task<VehicleState?> FetchAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("A render must never fetch.");
    }

    private StubFeed Feed(VehicleSourceHealth health)
    {
        var feed = new StubFeed(health);
        Services.AddSingleton<IVehicleUpdateService>(feed);
        return feed;
    }

    [Fact]
    public void Nothing_is_shown_without_a_feed_at_all()
    {
        // The ordinary installation. A layout that carried an empty band on every page would be a
        // worse dashboard for everyone who has never configured any of this.
        Assert.Empty(Render<VehicleFeedAlert>().Markup.Trim());
    }

    [Theory]
    [InlineData(VehicleSourceState.Ok)]
    [InlineData(VehicleSourceState.Degraded)]
    public void Nothing_is_shown_for_a_state_that_clears_itself(VehicleSourceState state)
    {
        Feed(new VehicleSourceHealth(state, "The portal answered."));

        Assert.Empty(Render<VehicleFeedAlert>().Markup.Trim());
    }

    [Fact]
    public void A_blocked_feed_is_named_with_its_sentence_and_a_way_to_check_it()
    {
        Feed(VehicleSourceHealth.NeedsOwner(
            "The portal is showing something only you can answer (a one-time code)."));

        var page = Render<VehicleFeedAlert>();

        Assert.Contains("Sign-in required", page.Markup);
        Assert.Contains("a one-time code", page.Markup);
        Assert.Equal("/vehicle-portal", page.Find("a").GetAttribute("href"));
    }

    [Fact]
    public void It_appears_on_a_page_that_was_already_open()
    {
        // A browser left open overnight is exactly where this has to show up: reading Health once at
        // first render would leave the page lying by omission until somebody navigated.
        var feed = Feed(VehicleSourceHealth.Ok("The portal answered."));

        var page = Render<VehicleFeedAlert>();
        Assert.Empty(page.Markup.Trim());

        feed.Health = VehicleSourceHealth.NeedsOwner("The portal refused the sign-in.");
        _holder.Set(Statuses.Sample(new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero)));

        Assert.Contains("Sign-in required", page.Markup);
        Assert.Contains("refused the sign-in", page.Markup);
    }
}
