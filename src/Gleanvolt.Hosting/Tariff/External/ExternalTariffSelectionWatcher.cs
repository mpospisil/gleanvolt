using Gleanvolt.Core.Interfaces;
using Gleanvolt.Providers.Contracts.Tariff;
using Gleanvolt.Providers.Server;

namespace Gleanvolt.Hosting.Tariff.External;

/// <summary>
/// The selection is the switch (#401): after every tariff save, a provider whose kind is no longer
/// selected is told goodbye and disconnected, and its next hello is refused until External is chosen
/// again.
/// </summary>
internal sealed class ExternalTariffSelectionWatcher(ITariffEditor editor, ExternalProviderRegistry registry, ILogger<ExternalTariffSelectionWatcher> logger)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        editor.Saved += Revalidate;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        editor.Saved -= Revalidate;
        return Task.CompletedTask;
    }

    private void Revalidate() =>
        _ = registry.RevalidateAsync(TariffContract.Kind).ContinueWith(
            task => logger.LogWarning("Could not close the tariff provider's session ({Reason}).", task.Exception?.GetBaseException().Message),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
}
