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
///
/// <para>Settings chooses through <see cref="Select"/> (#388), which wins over the configuration for the
/// rest of the process: the choice is saved to the overrides file, which is read only at the next
/// start, so a configuration reload in between would otherwise bring the old source back.</para>
/// </summary>
public sealed class SolarForecastSources : IDisposable
{
    private readonly IReadOnlyList<ISolarForecastSource> _sources;
    private readonly ILogger<SolarForecastSources> _logger;
    private readonly IDisposable? _subscription;
    private volatile ISolarForecastSource? _active;
    private volatile string? _chosen;

    public SolarForecastSources(
        IEnumerable<ISolarForecastSource> sources,
        IOptionsMonitor<SolarForecastOptions> options,
        ILogger<SolarForecastSources> logger)
    {
        _sources = [.. sources];
        _logger = logger;
        Configured = options.CurrentValue.Source;
        _active = Find(Configured);
        _subscription = options.OnChange(next =>
        {
            Configured = next.Source;
            if (_chosen is null)
            {
                Apply(next.Source);
            }
        });
    }

    /// <summary>Raised when a configuration change or <see cref="Select"/> makes a different source active.</summary>
    public event Action? Switched;

    /// <summary>The source to ask, or null when <c>SolarForecast:Source</c> names none that is registered.</summary>
    public ISolarForecastSource? Active => _active;

    /// <summary>What is selected: Settings' choice, or the configuration's, or <c>solcast</c> when neither says.</summary>
    public string Selected => _active?.Id ?? Normalise(_chosen ?? Configured);

    /// <summary>Every registered source's id, in registration order.</summary>
    public IReadOnlyList<string> Ids => [.. _sources.Select(source => source.Id)];

    private string? Configured { get; set; }

    /// <summary>Whether <paramref name="id"/> names a registered source.</summary>
    public bool Has(string? id) => _sources.Any(source => string.Equals(source.Id, Normalise(id), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Makes <paramref name="id"/> the active source for the rest of the process, over the
    /// configuration, and raises <see cref="Switched"/> when that is a change.
    /// </summary>
    public void Select(string id)
    {
        _chosen = Normalise(id);
        Apply(_chosen);
    }

    public void Dispose() => _subscription?.Dispose();

    // A reload fires for any change to the configuration, often twice per save: only a different
    // source is news.
    private void Apply(string? id)
    {
        var next = Find(id);
        var previous = Interlocked.Exchange(ref _active, next);

        if (ReferenceEquals(previous, next))
        {
            return;
        }

        _logger.LogInformation(
            "Solar forecast source switched from {Previous} to {Next}.", previous?.Id ?? "none", next?.Id ?? "none");
        Switched?.Invoke();
    }

    private static string Normalise(string? id) =>
        string.IsNullOrWhiteSpace(id) ? SolarForecastSourceIds.Solcast : id.Trim().ToLowerInvariant();

    private ISolarForecastSource? Find(string? id)
    {
        var wanted = Normalise(id);
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
