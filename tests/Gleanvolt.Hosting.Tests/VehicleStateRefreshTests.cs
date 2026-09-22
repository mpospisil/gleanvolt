using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Vehicles;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// Asking the car because somebody wants to know (issue #168), of the one feed an installation has
/// (issue #212).
///
/// <para>What these pin is the behaviour that differs from polling: a failure hands back the last
/// known reading rather than nothing — because the caller is often a plan, which would rather be built
/// on an old number that says so.</para>
/// </summary>
public class VehicleStateRefreshTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 8, 0, 0, TimeSpan.Zero);

    private sealed class Feed(VehicleState? answer, Exception? throws = null) : IVehicleUpdateService
    {
        public int Asks { get; private set; }

        public string VehicleId => "id4";

        public string Manufacturer => "vw-website";

        public string DisplayName => "volkswagen.de";

        public VehicleSourceHealth Health => VehicleSourceHealth.Ok("answering");

        public TimeSpan NextDelay => TimeSpan.FromMinutes(1);

        public Task<VehicleState?> FetchAsync(CancellationToken cancellationToken)
        {
            Asks++;
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
    public async Task The_one_feed_is_asked_once()
    {
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
}
