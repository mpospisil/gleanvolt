using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Charger;
using Microsoft.AspNetCore.SignalR;

namespace Gleanvolt.Providers.Testing;

/// <summary>The SignalR face of <see cref="ChargerTestHub"/>: the four methods a charger plugin calls.</summary>
internal sealed class TestChargerHub(ChargerTestHub.Holder holder) : Hub<IChargerClient>, IChargerHub
{
    private ChargerTestHub State => holder.Hub ?? throw new HubException("The test hub is still starting.");

    public Task<ChargerWelcome> Hello(ProviderHello hello)
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

    public Task<PublishResult> Publish(ChargerReading reading)
    {
        RequireAttached();
        return Task.FromResult(State.Published(reading));
    }

    public Task CommandDone(ChargerCommandResult result)
    {
        RequireAttached();
        State.CommandDone(result);
        return Task.CompletedTask;
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
