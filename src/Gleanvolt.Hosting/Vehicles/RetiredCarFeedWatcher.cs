using Gleanvolt.Core.Interfaces;
using Gleanvolt.Providers.Contracts.Vehicle;
using Gleanvolt.Providers.Server;

namespace Gleanvolt.Hosting.Vehicles;

/// <summary>
/// Builds <see cref="RetiredCarFeed"/> at start, so the move to a car plugin (#477) happens then rather than when
/// somebody opens the Car page, and ends its notice when a car plugin connects. <c>ExternalWeatherSelectionWatcher</c>'s
/// arrangement.
/// </summary>
internal sealed class RetiredCarFeedWatcher(RetiredCarFeed retired, ExternalProviderRegistry registry) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        registry.Changed += Connected;
        Connected();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        registry.Changed -= Connected;
        return Task.CompletedTask;
    }

    private void Connected()
    {
        if (retired.Notice is not null && registry.Find(VehicleContract.Kind) is { State: ExternalProviderState.Connected })
        {
            retired.ProviderConnected();
        }
    }
}
