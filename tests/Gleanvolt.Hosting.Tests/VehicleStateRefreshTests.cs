using Microsoft.Extensions.Time.Testing;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Vehicles;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// Asking the car because somebody wants to know (issue #168), of the one feed an installation has
/// (issue #212).
///
/// <para>What these pin is the behaviour that differs from polling: the feed is asked through
/// <c>AskAsync</c>, which a charge-gated feed answers while parked, and a failure hands back the last
/// known reading rather than nothing — because the caller is often a plan, which would rather be built
/// on an old number that says so.</para>
/// </summary>
public class VehicleStateRefreshTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 8, 0, 0, TimeSpan.Zero);

    private sealed class Feed(
        VehicleState? answer, Exception? throws = null, VehicleSourceHealth? health = null,
        Func<CancellationToken, Task<VehicleState?>>? ask = null) : IVehicleUpdateService
    {
        public int Asks { get; private set; }

        public string VehicleId => "id4";

        public string Manufacturer => "vw-website";

        public string DisplayName => "volkswagen.de";

        public VehicleSourceHealth Health => health ?? VehicleSourceHealth.Ok("answering");

        public TimeSpan NextDelay => TimeSpan.FromMinutes(1);

        public bool DeliversOnlyWhileCharging => true;

        // The clock's path: a parked car's gated feed returns nothing here.
        public Task<VehicleState?> FetchAsync(CancellationToken cancellationToken) =>
            Task.FromResult<VehicleState?>(null);

        public Task<VehicleState?> AskAsync(CancellationToken cancellationToken)
        {
            Asks++;

            if (ask is not null)
            {
                return ask(cancellationToken);
            }

            return throws is not null ? Task.FromException<VehicleState?>(throws) : Task.FromResult(answer);
        }
    }

    private static VehicleState At(DateTimeOffset when, double soc) =>
        new(when, SocPercent: soc, SourceId: "vw-website");

    private static VehicleStateRefresh Refresh(IVehicleUpdateService? feed, VehicleStateHolder? holder = null) =>
        new(new ConfiguredVehicleFeed(feed), holder ?? new VehicleStateHolder());

    [Fact]
    public async Task With_no_feed_there_is_nothing_to_ask()
    {
        var refresh = Refresh(null);

        Assert.False(refresh.CanRefresh);
        var result = await refresh.RefreshAsync();

        Assert.False(result.Succeeded);
        Assert.Null(result.State);
    }

    [Fact]
    public async Task A_parked_car_s_charge_gated_feed_is_asked_rather_than_polled()
    {
        // #212: with the portal gone, a parked ID.4 has only volkswagen.de. The owner's ask reads it
        // once; the clock (FetchAsync) would have returned nothing.
        var feed = new Feed(At(Now, 55));

        var result = await Refresh(feed).RefreshAsync();

        Assert.Equal(1, feed.Asks);
        Assert.True(result.Succeeded);
        Assert.Equal(55, result.State!.SocPercent);
    }

    [Fact]
    public async Task The_reading_reaches_the_holder_so_everything_else_sees_it()
    {
        var holder = new VehicleStateHolder();

        await Refresh(new Feed(At(Now, 55)), holder).RefreshAsync();

        Assert.Equal(55, holder.GetCurrentState()!.SocPercent);
    }

    /// <summary>
    /// Nothing answered, but something was known. The old reading goes back marked not-fresh, so a
    /// plan can use it and say so while a page can decline to show it.
    /// </summary>
    [Fact]
    public async Task A_failed_ask_hands_back_the_last_known_reading()
    {
        var holder = new VehicleStateHolder();
        holder.Set(At(Now.AddHours(-4), 62));

        var result = await Refresh(new Feed(null, new InvalidOperationException("unreachable")), holder)
            .RefreshAsync();

        Assert.False(result.Succeeded);
        Assert.False(result.IsFresh);
        Assert.Equal(62, result.State!.SocPercent);
        Assert.Contains("unreachable", result.Message);
    }

    [Fact]
    public async Task An_empty_answer_names_the_feed_in_the_owner_s_words()
    {
        var result = await Refresh(new Feed(null)).RefreshAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("volkswagen.de", result.Message);
    }

    /// <summary>A parked car's reading, captured at its last charge three days before <see cref="Now"/>.</summary>
    private static VehicleStateHolder HoldingLastCharge()
    {
        var holder = new VehicleStateHolder();
        holder.Set(At(Now.AddDays(-3), 71));
        return holder;
    }

    private static VehicleStateRefresh Planning(IVehicleUpdateService feed, VehicleStateHolder holder, TimeProvider time) =>
        new(new ConfiguredVehicleFeed(feed), holder, time: time);

    [Fact]
    public async Task Planning_asks_the_car_when_the_reading_is_its_last_charge_s()
    {
        var time = new FakeTimeProvider(Now);
        var holder = HoldingLastCharge();
        var feed = new Feed(At(Now, 38));

        await Planning(feed, holder, time).PrepareForPlanningAsync();

        Assert.Equal(1, feed.Asks);
        Assert.Equal(38, holder.GetCurrentState()!.SocPercent);
    }

    [Fact]
    public async Task Previews_in_quick_succession_share_one_ask()
    {
        // "What if I leave at eight?" is several presses; each one must not spend a request.
        var time = new FakeTimeProvider(Now);
        var feed = new Feed(At(Now.AddDays(-3), 71));
        var refresh = Planning(feed, HoldingLastCharge(), time);

        await refresh.PrepareForPlanningAsync();
        time.Advance(TimeSpan.FromMinutes(2));
        await refresh.PrepareForPlanningAsync();

        Assert.Equal(1, feed.Asks);

        time.Advance(VehicleStateRefresh.PlanningReuse);
        await refresh.PrepareForPlanningAsync();

        Assert.Equal(2, feed.Asks);
    }

    [Fact]
    public async Task A_reading_the_car_captured_moments_ago_is_planned_from_as_it_is()
    {
        // Mid-charge the feed is polled every few minutes: nothing to add by asking again.
        var time = new FakeTimeProvider(Now);
        var holder = new VehicleStateHolder();
        holder.Set(At(Now.AddMinutes(-2), 55));
        var feed = new Feed(At(Now, 56));

        await Planning(feed, holder, time).PrepareForPlanningAsync();

        Assert.Equal(0, feed.Asks);
    }

    [Fact]
    public async Task A_feed_waiting_on_its_owner_is_not_asked_for_them()
    {
        var time = new FakeTimeProvider(Now);
        var feed = new Feed(
            At(Now, 38), health: VehicleSourceHealth.NeedsOwner("volkswagen.de wants a one-time code."));

        await Planning(feed, HoldingLastCharge(), time).PrepareForPlanningAsync();

        Assert.Equal(0, feed.Asks);
    }

    [Fact]
    public async Task A_car_that_does_not_answer_in_time_leaves_the_held_reading_to_plan_from()
    {
        var time = new FakeTimeProvider(Now);
        var holder = HoldingLastCharge();
        var feed = new Feed(null, ask: async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        });

        var planning = Planning(feed, holder, time).PrepareForPlanningAsync();
        await Task.Delay(50);
        time.Advance(VehicleStateRefresh.PlanningWait);
        await planning.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(71, holder.GetCurrentState()!.SocPercent);
    }

    [Fact]
    public async Task A_car_that_does_not_answer_leaves_the_held_reading_to_plan_from()
    {
        var time = new FakeTimeProvider(Now);
        var holder = HoldingLastCharge();

        await Planning(new Feed(null, new HttpRequestException("unreachable")), holder, time)
            .PrepareForPlanningAsync();

        Assert.Equal(71, holder.GetCurrentState()!.SocPercent);
    }
}
