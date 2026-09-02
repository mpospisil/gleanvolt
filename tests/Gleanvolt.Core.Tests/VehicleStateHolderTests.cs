using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Tests;

public class VehicleStateHolderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GetCurrentState_IsNullBeforeAnythingArrives()
    {
        Assert.Null(new VehicleStateHolder().GetCurrentState());
    }

    [Fact]
    public void Set_ReplacesTheCurrentStateAndRaisesUpdated()
    {
        var holder = new VehicleStateHolder();
        var raised = new List<VehicleState>();
        holder.Updated += raised.Add;

        var first = new VehicleState(Now.AddHours(-1), SocPercent: 28);
        var second = new VehicleState(Now, SocPercent: 41);

        holder.Set(first);
        holder.Set(second);

        Assert.Same(second, holder.GetCurrentState());
        Assert.Equal([first, second], raised);
    }

    [Fact]
    public void Set_DoesNotThrowWithNoSubscribers()
    {
        var holder = new VehicleStateHolder();

        holder.Set(new VehicleState(Now));

        Assert.NotNull(holder.GetCurrentState());
    }

    [Fact]
    public void ItIsTheReadSideConsumersDependOn()
    {
        // Consumers take IVehicleTelemetry so they can be handed a stub; only the transport holds the
        // reference that can Set.
        IVehicleTelemetry telemetry = new VehicleStateHolder();

        Assert.Null(telemetry.GetCurrentState());
    }
    [Fact]
    public void The_newest_reading_wins_whichever_source_produced_it()
    {
        // Two feeds, one car. A Home Assistant integration polling the manufacturer's app API and this
        // controller reading the same manufacturer's EU Data Act portal describe the same battery at
        // different resolutions and different lags, and the right answer is whichever saw the car most
        // recently -- not whichever wrote last.
        var holder = new VehicleStateHolder();
        var noon = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

        Assert.True(holder.Set(new VehicleState(noon, SocPercent: 69, SourceId: "id4")));
        Assert.False(holder.Set(new VehicleState(noon.AddMinutes(-20), SocPercent: 60, SourceId: "vw-group")));

        Assert.Equal(69, holder.GetCurrentState()!.SocPercent);
    }

    [Fact]
    public void A_feed_that_goes_quiet_stops_holding_the_other_back()
    {
        // Precedence corrects itself within one reading: a source that stops stops advancing, so the
        // other one's next reading is the newest and takes the card.
        var holder = new VehicleStateHolder();
        var noon = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

        holder.Set(new VehicleState(noon, SocPercent: 69, SourceId: "id4"));

        Assert.True(holder.Set(new VehicleState(noon.AddMinutes(30), SocPercent: 70, SourceId: "vw-group")));
        Assert.Equal(70, holder.GetCurrentState()!.SocPercent);
    }

    [Fact]
    public void A_reading_at_the_same_moment_still_lands()
    {
        // Equal, not older: a source correcting itself within one capture time is an update, and a
        // strict comparison would leave the first word on a reading the source has already revised.
        var holder = new VehicleStateHolder();
        var noon = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

        holder.Set(new VehicleState(noon, SocPercent: 60));

        Assert.True(holder.Set(new VehicleState(noon, SocPercent: 69)));
        Assert.Equal(69, holder.GetCurrentState()!.SocPercent);
    }


}
