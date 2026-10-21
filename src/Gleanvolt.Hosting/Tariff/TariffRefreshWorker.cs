using Gleanvolt.Core.Interfaces;

namespace Gleanvolt.Hosting.Tariff;

/// <summary>
/// Keeps the selected tariff strategy fed (issue #265), when it is one that fetches
/// (<see cref="IRefreshingTariffProvider"/>): once at startup, again whenever the settings are saved,
/// and every <see cref="IRefreshingTariffProvider.RefreshInterval"/> in between. Strategies that are
/// not selected are never fetched for, and with a strategy that does not fetch, this waits for a save.
///
/// <para>A failed fetch is logged and changes nothing: the strategy keeps what its last good fetch
/// left, and decides itself when that is too old to answer from.</para>
/// </summary>
public sealed class TariffRefreshWorker : BackgroundService
{
    private readonly ITariffEditor _settings;
    private readonly TariffSchedule _schedule;
    private readonly ILogger<TariffRefreshWorker> _logger;
    private readonly SemaphoreSlim _saved = new(0, 1);

    public TariffRefreshWorker(ITariffEditor settings, TariffSchedule schedule, ILogger<TariffRefreshWorker> logger)
    {
        _settings = settings;
        _schedule = schedule;
        _logger = logger;
        _settings.Saved += Wake;
    }

    public override void Dispose()
    {
        _settings.Saved -= Wake;
        _saved.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Refreshes the selected strategy once, if it fetches, and says how long until the next time:
    /// its interval, or <see cref="Timeout.InfiniteTimeSpan"/> when there is nothing to fetch for.
    /// </summary>
    public async Task<TimeSpan> RefreshOnceAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.Read();

        if (_settings.Providers.FirstOrDefault(provider => settings.Selects(provider.Id)) is not IRefreshingTariffProvider provider)
        {
            return Timeout.InfiniteTimeSpan;
        }

        try
        {
            await provider.RefreshAsync(settings.SectionFor(provider.Id), cancellationToken).ConfigureAwait(false);
            _schedule.Invalidate();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "The {Provider} tariff provider could not refresh ({Reason}); it answers from its last good fetch.",
                provider.Name,
                ex.Message);
        }

        return provider.RefreshInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = await RefreshOnceAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await _saved.WaitAsync(wait, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    // A save may select a different strategy or change the selected one's settings: fetch now.
    private void Wake()
    {
        try
        {
            if (_saved.CurrentCount == 0)
            {
                _saved.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // Two saves at once; one wake-up is enough.
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }
}
