using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Hosting.Forecasting;

/// <summary>The <c>SolarForecast</c> section: which source the forecast is taken from (issue #387).</summary>
public sealed class SolarForecastOptions
{
    public const string SectionName = "SolarForecast";

    /// <summary>The <see cref="ISolarForecastSource.Id"/> to ask: <see cref="SolarForecastSourceIds"/>. Solcast unless set.</summary>
    public string Source { get; init; } = SolarForecastSourceIds.Solcast;
}

/// <summary>
/// Which <see cref="ISolarForecastSource"/> is active (issue #387): the one <c>SolarForecast:Source</c>
/// names, read again whenever the configuration changes, so a switch needs no restart — the way a
/// tariff strategy is switched. The refresh worker asks only the active source, and
/// <see cref="Switched"/> wakes it to ask the new one at once.
/// </summary>
public sealed class SolarForecastSources : IDisposable
{
    private readonly IReadOnlyList<ISolarForecastSource> _sources;
    private readonly ILogger<SolarForecastSources> _logger;
    private readonly IDisposable? _subscription;
    private volatile ISolarForecastSource? _active;

    public SolarForecastSources(
        IEnumerable<ISolarForecastSource> sources,
        IOptionsMonitor<SolarForecastOptions> options,
        ILogger<SolarForecastSources> logger)
    {
        _sources = [.. sources];
        _logger = logger;
        _active = Find(options.CurrentValue.Source);
        _subscription = options.OnChange(Select);
    }

    /// <summary>Raised when a configuration change selects a different source.</summary>
    public event Action? Switched;

    /// <summary>The source to ask, or null when <c>SolarForecast:Source</c> names none that is registered.</summary>
    public ISolarForecastSource? Active => _active;

    public void Dispose() => _subscription?.Dispose();

    // A reload fires for any change to the configuration, often twice per save: only a different
    // source is news.
    private void Select(SolarForecastOptions options)
    {
        var next = Find(options.Source);
        var previous = Interlocked.Exchange(ref _active, next);

        if (ReferenceEquals(previous, next))
        {
            return;
        }

        _logger.LogInformation(
            "Solar forecast source switched from {Previous} to {Next}.", previous?.Id ?? "none", next?.Id ?? "none");
        Switched?.Invoke();
    }

    private ISolarForecastSource? Find(string? id)
    {
        var wanted = string.IsNullOrWhiteSpace(id) ? SolarForecastSourceIds.Solcast : id.Trim();
        var source = _sources.FirstOrDefault(s => string.Equals(s.Id, wanted, StringComparison.OrdinalIgnoreCase));

        if (source is null)
        {
            _logger.LogWarning(
                "SolarForecast:Source is {Source}, which is not a forecast source ({Known}); no forecast is fetched.",
                wanted,
                string.Join(", ", _sources.Select(s => s.Id)));
        }

        return source;
    }
}
