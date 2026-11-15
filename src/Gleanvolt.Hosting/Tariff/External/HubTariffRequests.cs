using Gleanvolt.Core.Interfaces;
using Gleanvolt.Providers.Contracts.Tariff;
using Gleanvolt.Providers.Server;
using Microsoft.AspNetCore.SignalR;
using RefreshRequest = Gleanvolt.Providers.Contracts.RefreshRequest;

namespace Gleanvolt.Hosting.Tariff.External;

/// <summary>
/// Asks the attached tariff provider for fresh data (#401) through <c>IHubContext</c>: the request is
/// written and this returns. The answer is a publish of its own; one that does not come within
/// <see cref="Timeout"/> shows as unanswered on Settings.
/// </summary>
internal sealed class HubTariffRequests(IServiceProvider services, TimeProvider time) : IExternalTariffRequests
{
    /// <summary>How long a provider has to answer.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public async Task<bool> RequestRefreshAsync(CancellationToken cancellationToken)
    {
        // Resolved here rather than injected: the registry and the hub context are built after the
        // strategies that use this.
        var sessions = services.GetRequiredService<ExternalProviderRegistry>().For(TariffContract.Kind);

        if (sessions?.BeginRequest(Timeout) is not { } request)
        {
            return false;
        }

        await services.GetRequiredService<IHubContext<TariffHub, ITariffClient>>()
            .Clients.Client(request.ConnectionId)
            .RefreshRequested(new RefreshRequest(request.Id, time.GetUtcNow()))
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        return true;
    }
}
