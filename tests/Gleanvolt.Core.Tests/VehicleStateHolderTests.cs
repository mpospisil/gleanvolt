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
}
