using Gleanvolt.Providers.Client.Weather;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Weather;

namespace Gleanvolt.Plugin.OpenWeather.OpenWeather;

/// <summary>
/// The weather for Gleanvolt (#424): what the SDK asks after every hello and every
/// <see cref="IWeatherClient.RefreshRequested"/>. It reads OpenWeatherMap at most once every
/// <see cref="OpenWeatherOptions.MinimumTimeBetweenReads"/> and answers sooner requests with that reading,
/// so a reconnecting Gleanvolt costs no burst of calls.
///
/// <para>Every failure becomes a status Gleanvolt shows on its Settings: no key or a refused key needs the
/// owner (<see cref="ProviderStatus.NeedsOwner"/>); an outage or a rate limit only says the weather is not
/// being read. Nothing is retried on a clock: the next request from Gleanvolt is the next try.</para>
/// </summary>
public sealed class OpenWeatherFeed : IWeatherSource
{
    private readonly OpenWeatherClient _client;
    private readonly ApiKeyStore _key;
    private readonly OpenWeatherOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<OpenWeatherFeed> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private WeatherData? _last;
    private WeatherSite? _lastSite;
    private DateTimeOffset? _lastReadAt;
    private ProviderStatus? _status;
    private Words? _statusWords;

    public OpenWeatherFeed(OpenWeatherClient client, ApiKeyStore key, OpenWeatherOptions options, TimeProvider time, ILogger<OpenWeatherFeed> logger)
    {
        _client = client;
        _key = key;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>Raised when the status or the last reading changes.</summary>
    public event Action? Changed;

    /// <summary>The newest reading, or null.</summary>
    public WeatherData? LastReading => _last;

    /// <summary>When OpenWeatherMap was last asked, whatever it answered.</summary>
    public DateTimeOffset? LastReadAt => _lastReadAt;

    /// <summary>How the feed is doing, as told to Gleanvolt; null before the first request.</summary>
    public ProviderStatus? Status => _status;

    /// <summary>What went wrong, in the page's language; null when healthy.</summary>
    public Words? StatusWords => _statusWords;

    public async Task<WeatherData?> GetReadingAsync(WeatherSite site, CancellationToken cancellationToken)
    {
        if (_key.Current is not { } key)
        {
            NeedsOwner(PageText.NoKey, ProviderStatusCodes.NotSetUp);
            return null;
        }

        if (site.Latitude is not { } latitude || site.Longitude is not { } longitude)
        {
            NeedsOwner(PageText.NoLocation, ProviderStatusCodes.NotSetUp);
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Another request may have read while this one waited; the same site's reading is reused.
            if (TooSoon() && site == _lastSite)
            {
                return _last;
            }

            _lastReadAt = _time.GetUtcNow();
            _lastSite = site;

            try
            {
                _last = await _client.GetCurrentAsync(key, latitude, longitude, cancellationToken).ConfigureAwait(false);
                SetStatus(new ProviderStatus(true), null);
                return _last;
            }
            catch (ReadFailedException ex)
            {
                _logger.LogWarning("Reading OpenWeatherMap failed: {Reason}", ex.Message);
                _last = null;

                switch (ex.Failure)
                {
                    case ReadFailure.Refused:
                        NeedsOwner(PageText.KeyRefused, ProviderStatusCodes.Refused);
                        break;
                    case ReadFailure.RateLimited:
                        Unhealthy(PageText.RateLimited, ProviderStatusCodes.RateLimited);
                        break;
                    case ReadFailure.ServiceChanged:
                        Unhealthy(PageText.ServiceChanged, ProviderStatusCodes.ServiceChanged);
                        break;
                    default:
                        Unhealthy(PageText.Unreachable.Format(ex.Message), ProviderStatusCodes.Unreachable);
                        break;
                }

                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The key changed on the page: the next request reads again rather than reuse a reading.</summary>
    public void KeyChanged()
    {
        _lastReadAt = null;
        _last = null;

        if (_key.Current is null)
        {
            NeedsOwner(PageText.NoKey, ProviderStatusCodes.NotSetUp);
        }
        else
        {
            Changed?.Invoke();
        }
    }

    private bool TooSoon() => _lastReadAt is { } at && _time.GetUtcNow() - at < _options.MinimumTimeBetweenReads;

    private void NeedsOwner(Words what, string code) =>
        SetStatus(new ProviderStatus(false, what.En, NeedsOwner: true, Code: code, Messages: what.All), what);

    private void Unhealthy(Words what, string code) =>
        SetStatus(new ProviderStatus(false, what.En, Code: code, Messages: what.All), what);

    private void SetStatus(ProviderStatus status, Words? words)
    {
        _status = status;
        _statusWords = words;
        Changed?.Invoke();
    }
}

/// <summary>
/// Tells Gleanvolt how the feed is doing (#424): after every hello, since a new session starts with no
/// status in Gleanvolt, and whenever the status changes.
/// </summary>
public sealed class StatusReporter : IHostedService
{
    private readonly WeatherProviderConnection _connection;
    private readonly OpenWeatherFeed _feed;
    private readonly ILogger<StatusReporter> _logger;
    private ProviderStatus? _reported;

    public StatusReporter(WeatherProviderConnection connection, OpenWeatherFeed feed, ILogger<StatusReporter> logger)
    {
        _connection = connection;
        _feed = feed;
        _logger = logger;

        // Here rather than at start: the first hello can come before it.
        connection.Welcomed += welcome => _ = ReportAsync(force: true);
        feed.Changed += () => _ = ReportAsync(force: false);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ReportAsync(bool force)
    {
        if (_feed.Status is not { } status || (!force && status == _reported))
        {
            return;
        }

        try
        {
            await _connection.ReportStatusAsync(status).ConfigureAwait(false);
            _reported = status;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not report the status to Gleanvolt ({Reason}).", ex.Message);
        }
    }
}
