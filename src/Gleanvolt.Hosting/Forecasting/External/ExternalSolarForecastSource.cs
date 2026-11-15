using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Providers.Contracts.SolarForecast;
using Gleanvolt.Providers.Server;
using Microsoft.AspNetCore.SignalR;
using RefreshRequest = Gleanvolt.Providers.Contracts.RefreshRequest;

namespace Gleanvolt.Hosting.Forecasting.External;

/// <summary>
/// The external provider as a forecast source (#388): the refresh worker's request goes out to the
/// attached provider as <see cref="ISolarForecastClient.RefreshRequested"/> through <c>IHubContext</c>,
/// and this returns. The answer is a publish of its own on <see cref="SolarForecastHub"/>; one that
/// does not come within <see cref="Timeout"/> shows as unanswered on Settings.
///
/// <para>With no provider attached nothing is sent, and the store keeps the last forecast, which the
/// Overview then shows as from a disconnected provider. There is no fallback to Solcast.</para>
/// </summary>
internal sealed class ExternalSolarForecastSource(IServiceProvider services, TimeProvider time, ILogger<ExternalSolarForecastSource> logger)
    : ISolarForecastSource
{
    /// <summary>How long a provider has to answer.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public string Id => SolarForecastSourceIds.External;

    public async Task RequestRefreshAsync(CancellationToken cancellationToken = default)
    {
        // Resolved here rather than injected: the registry is built from the kinds, and a kind's
        // switch reads the sources this is one of.
        var sessions = services.GetRequiredService<ExternalProviderRegistry>().For(SolarForecastContract.Kind);

        if (sessions?.BeginRequest(Timeout) is not { } request)
        {
            logger.LogDebug("No solar forecast provider is connected; nothing to ask.");
            return;
        }

        try
        {
            await services.GetRequiredService<IHubContext<SolarForecastHub, ISolarForecastClient>>()
                .Clients.Client(request.ConnectionId)
                .RefreshRequested(new RefreshRequest(request.Id, time.GetUtcNow()))
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A source does not throw for a failed request: the store keeps what it has, and the
            // request shows as unanswered when its time runs out.
            logger.LogWarning("Could not ask the solar forecast provider for a forecast ({Reason}).", ex.Message);
        }
    }
}
