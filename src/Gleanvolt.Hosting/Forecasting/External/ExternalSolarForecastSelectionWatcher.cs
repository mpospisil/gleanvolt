using Gleanvolt.Providers.Contracts.SolarForecast;
using Gleanvolt.Providers.Server;

namespace Gleanvolt.Hosting.Forecasting.External;

/// <summary>
/// The selection is the switch (#388), as for the tariff: when another forecast source becomes active,
/// a connected provider is told goodbye and disconnected, and its next hello is refused until External
/// is chosen again.
/// </summary>
internal sealed class ExternalSolarForecastSelectionWatcher(
    SolarForecastSources sources,
    ExternalProviderRegistry registry,
    ILogger<ExternalSolarForecastSelectionWatcher> logger)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        sources.Switched += Revalidate;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        sources.Switched -= Revalidate;
        return Task.CompletedTask;
    }

    private void Revalidate() =>
        _ = registry.RevalidateAsync(SolarForecastContract.Kind).ContinueWith(
            task => logger.LogWarning("Could not close the solar forecast provider's session ({Reason}).", task.Exception?.GetBaseException().Message),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
}
