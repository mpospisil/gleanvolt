using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Vehicle;
using Microsoft.AspNetCore.SignalR;

namespace Gleanvolt.Providers.Testing;

/// <summary>The SignalR face of <see cref="VehicleTestHub"/>: the three methods a car plugin calls.</summary>
internal sealed class TestVehicleHub(VehicleTestHub.Holder holder) : Hub<IVehicleClient>, IVehicleHub
{
    private VehicleTestHub State => holder.Hub ?? throw new HubException("The test hub is still starting.");

    public Task<VehicleWelcome> Hello(ProviderHello hello)
    {
        var welcome = State.Welcome(hello, Context.ConnectionId);

        if (!welcome.Accepted)
        {
            // As Gleanvolt: the answer is written first, so the plugin can log why, and then the connection closes.
            _ = CloseSoonAsync(Context);
        }

        return Task.FromResult(welcome);
    }

    public Task ReportStatus(ProviderStatus status)
    {
        RequireAttached();
        State.Reported(status);
        return Task.CompletedTask;
    }

    public Task<PublishResult> Publish(VehicleReading reading)
    {
        RequireAttached();
        return Task.FromResult(State.Published(reading));
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        State.Detached(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    private void RequireAttached()
    {
        if (!State.IsCurrent(Context.ConnectionId))
        {
            throw new HubException("Say Hello first: this connection has no attached session.");
        }
    }

    private static async Task CloseSoonAsync(HubCallerContext context)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
        context.Abort();
    }
}
