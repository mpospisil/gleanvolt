using Gleanvolt.Core.Interfaces;
using Gleanvolt.Providers.Contracts.Weather;
using Gleanvolt.Providers.Server;

namespace Gleanvolt.Hosting.Weather;

/// <summary>
/// The selection is the switch (#424), as for the tariff and the solar forecast: when another weather
/// source is chosen, a connected provider is told goodbye and disconnected, and its next hello is refused
/// until External is chosen again.
/// </summary>
internal sealed class ExternalWeatherSelectionWatcher(
    IWeatherSourceSelection selection,
    ExternalProviderRegistry registry,
    ILogger<ExternalWeatherSelectionWatcher> logger)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        selection.Changed += Revalidate;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        selection.Changed -= Revalidate;
        return Task.CompletedTask;
    }

    private void Revalidate() =>
        _ = registry.RevalidateAsync(WeatherContract.Kind).ContinueWith(
            task => logger.LogWarning("Could not close the weather provider's session ({Reason}).", task.Exception?.GetBaseException().Message),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
}
