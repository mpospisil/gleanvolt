using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Vehicles;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// Asking the car because somebody wants to know (issue #168).
///
/// <para>The same <c>FetchAsync</c> the polling worker calls on a clock, called for a different
/// reason. What these pin is the behaviour that differs from polling: every feed is asked rather than
/// the first that answers, and a failure hands back the last known reading rather than nothing —
/// because the caller is often a plan, which would rather be built on an old number that says so.</para>
/// </summary>
public class VehicleStateRefreshTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 8, 0, 0, TimeSpan.Zero);

    private sealed class Feed(string manufacturer, VehicleState? answer, Exception? throws = null)
        : IVehicleUpdateService
    {
        public int Fetches { get; private set; }

        public string VehicleId => "id4";

        public string Manufacturer => manufacturer;

        public VehicleSourceHealth Health => VehicleSourceHealth.Ok("answering");

        public TimeSpan NextDelay => TimeSpan.FromMinutes(15);

        public Task<VehicleState?> FetchAsync(CancellationToken cancellationToken)
        {
            Fetches++;
            return throws is not null ? Task.FromException<VehicleState?>(throws) : Task.FromResult(answer);
        }
    }

    private static VehicleState At(DateTimeOffset when, double soc, string source) =>
        new(when, SocPercent: soc, SourceId: source);

    [Fact]
    public async Task With_no_feed_there_is_nothing_to_ask()
    {
        var refresh = new VehicleStateRefresh([], new VehicleStateHolder());

        Assert.False(refresh.CanRefresh);
        var result = await refresh.RefreshAsync();

        Assert.False(result.Succeeded);
        Assert.Null(result.State);
    }

    /// <summary>
    /// Every feed, not the first that answers: they carry different fields, and the holder keeps the
    /// newest reading rather than the newest source.
    /// </summary>
    [Fact]
    public async Task Every_feed_is_asked()
    {
        var first = new Feed("vw-group", At(Now.AddMinutes(-30), 40, "vw-group"));
        var second = new Feed("mqtt", At(Now.AddMinutes(-5), 41, "mqtt"));

        await new VehicleStateRefresh([first, second], new VehicleStateHolder()).RefreshAsync();

        Assert.Equal(1, first.Fetches);
        Assert.Equal(1, second.Fetches);
    }

    [Fact]
    public async Task The_newest_reading_wins_regardless_of_the_order_asked()
    {
        var newest = At(Now.AddMinutes(-5), 41, "mqtt");
        var refresh = new VehicleStateRefresh(
            [new Feed("mqtt", newest), new Feed("vw-group", At(Now.AddHours(-3), 40, "vw-group"))],
            new VehicleStateHolder());

        var result = await refresh.RefreshAsync();

        Assert.True(result.Succeeded);
        Assert.True(result.IsFresh);
        Assert.Equal(41, result.State!.SocPercent);
        Assert.Equal("mqtt", result.Source);
    }

    [Fact]
    public async Task The_reading_reaches_the_holder_so_everything_else_sees_it()
    {
        var holder = new VehicleStateHolder();

        await new VehicleStateRefresh([new Feed("vw-group", At(Now, 55, "vw-group"))], holder).RefreshAsync();

        Assert.Equal(55, holder.GetCurrentState()!.SocPercent);
    }

    /// <summary>One feed failing must not cost the answer another one gave.</summary>
    [Fact]
    public async Task A_broken_feed_does_not_lose_a_good_answer()
    {
        var refresh = new VehicleStateRefresh(
            [new Feed("broken", null, new InvalidOperationException("boom")),
             new Feed("vw-group", At(Now, 55, "vw-group"))],
            new VehicleStateHolder());

        var result = await refresh.RefreshAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(55, result.State!.SocPercent);
    }

    /// <summary>
    /// Nothing answered, but something was known. The old reading goes back marked not-fresh, so a
    /// plan can use it and say so while a page can decline to show it.
    /// </summary>
    [Fact]
    public async Task A_failed_ask_hands_back_the_last_known_reading()
    {
        var holder = new VehicleStateHolder();
        holder.Set(At(Now.AddHours(-4), 62, "vw-group"));

        var result = await new VehicleStateRefresh(
            [new Feed("vw-group", null, new InvalidOperationException("unreachable"))], holder).RefreshAsync();

        Assert.False(result.Succeeded);
        Assert.False(result.IsFresh);
        Assert.Equal(62, result.State!.SocPercent);
        Assert.Contains("unreachable", result.Message);
    }
}
