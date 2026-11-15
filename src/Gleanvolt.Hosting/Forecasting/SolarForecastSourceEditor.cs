using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Configuration;

namespace Gleanvolt.Hosting.Forecasting;

/// <summary>
/// The solar forecast source on Settings (#388): <c>SolarForecast:Source</c>, written to the overrides
/// file for the next start and applied to <see cref="SolarForecastSources"/> at once, so a switch needs
/// no restart. Choosing External is what turns the forecast hub on; choosing anything else closes its
/// provider's session (<c>ExternalSolarForecastSelectionWatcher</c>).
///
/// <para>Saved before it is applied: a choice that cannot be kept is refused rather than used until the
/// next start quietly undoes it. A host without an overrides file (a test host) applies it only.</para>
/// </summary>
public sealed class SolarForecastSourceEditor : ISolarForecastSourceSelection
{
    /// <summary>The configuration key the choice is saved under.</summary>
    public const string Key = SolarForecastOptions.SectionName + ":" + nameof(SolarForecastOptions.Source);

    private readonly SolarForecastSources _sources;
    private readonly PvSystemOverridesProvider? _overrides;
    private readonly ILogger<SolarForecastSourceEditor> _logger;

    public SolarForecastSourceEditor(SolarForecastSources sources, IConfiguration configuration, ILogger<SolarForecastSourceEditor> logger)
    {
        _sources = sources;
        _overrides = PvSystemOverrides.Find(configuration);
        _logger = logger;
        _sources.Switched += () => Changed?.Invoke();
    }

    public string Selected => _sources.Selected;

    public IReadOnlyList<string> Available => _sources.Ids;

    public event Action? Changed;

    public SettingsSaveResult Select(string sourceId)
    {
        if (!_sources.Has(sourceId))
        {
            return SettingsSaveResult.Refused(new ConfigurationProblems(
                $"{sourceId} is not a solar forecast source ({string.Join(", ", _sources.Ids)}).",
                Phrase.Of("SolarForecast.UnknownSource", new PhraseArg.Text(sourceId))));
        }

        var id = sourceId.Trim().ToLowerInvariant();

        if (_overrides is not null)
        {
            using (PvSystemOverrides.Lock(_overrides.Path))
            {
                try
                {
                    var stored = PvSystemOverrides.Read(_overrides.Path);
                    stored[Key] = id;
                    PvSystemOverrides.Write(_overrides.Path, stored);
                }
                catch (InvalidOperationException ex)
                {
                    return SettingsSaveResult.Refused([ex.Message]);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return SettingsSaveResult.Refused(ConfigurationProblems.NotWritten(_overrides.Path, ex.Message));
                }
            }
        }

        _logger.LogInformation("Solar forecast source {Source} chosen on Settings{Saved}.", id,
            _overrides is null ? string.Empty : $"; saved in {_overrides.Path} for the next start");
        _sources.Select(id);
        return SettingsSaveResult.Success;
    }
}
