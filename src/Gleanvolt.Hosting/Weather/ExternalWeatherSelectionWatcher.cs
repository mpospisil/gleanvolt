using Gleanvolt.Core.Interfaces;
using Gleanvolt.Providers.Contracts.Weather;
using Gleanvolt.Providers.Server;

namespace Gleanvolt.Hosting.Weather;

/// <summary>
/// The selection is the switch (#424), as for the tariff and the solar forecast: when another weather
/// source is chosen, a connected provider is told goodbye and disconnected, and its next hello is refused
/// until External is chosen again. And a provider that connects ends the notice about the removed
/// built-in (#425).
/// </summary>
internal sealed class ExternalWeatherSelectionWatcher(
    WeatherSourceSelection selection,
    ExternalProviderRegistry registry,
    ILogger<ExternalWeatherSelectionWatcher> logger)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        selection.Changed += Revalidate;
        registry.Changed += Connected;
        Connected();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        selection.Changed -= Revalidate;
        registry.Changed -= Connected;
        return Task.CompletedTask;
    }

    private void Connected()
    {
        if (selection.BuiltInRetired
            && registry.Find(WeatherContract.Kind) is { State: Gleanvolt.Core.Interfaces.ExternalProviderState.Connected })
        {
            selection.ProviderConnected();
        }
    }

    private void Revalidate() =>
        _ = registry.RevalidateAsync(WeatherContract.Kind).ContinueWith(
            task => logger.LogWarning("Could not close the weather provider's session ({Reason}).", task.Exception?.GetBaseException().Message),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
}
