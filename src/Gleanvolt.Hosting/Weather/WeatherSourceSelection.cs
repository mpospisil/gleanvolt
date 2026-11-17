using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Infrastructure.OpenWeather;
using Microsoft.Extensions.Options;

namespace Gleanvolt.Hosting.Weather;

/// <summary>
/// The weather source (#424): <c>Weather:Source</c>, chosen on Settings or in the setup wizard, written
/// to the overrides file for the next start and used at once, so a switch needs no restart. Choosing
/// External is what turns the weather hub on; choosing anything else closes its provider's session
/// (<see cref="ExternalWeatherSelectionWatcher"/>).
///
/// <para><b>Nothing chosen.</b> An installation that has never chosen keeps what it had before the choice
/// existed, worked out on every read: OpenWeather while a key is configured (on Settings or in
/// <c>Weather__ApiKey</c>), nothing otherwise. So an upgrade changes nothing, and a key saved later
/// turns the weather on as it always did.</para>
///
/// <para>Saved before it is applied: a choice that cannot be kept is refused rather than used until the
/// next start quietly undoes it. A host without an overrides file (a test host) applies it only.</para>
/// </summary>
public sealed class WeatherSourceSelection : IWeatherSourceSelection, IDisposable
{
    /// <summary>The configuration key the choice is saved under.</summary>
    public const string Key = WeatherOptions.SectionName + ":" + nameof(WeatherOptions.Source);

    private readonly WeatherKeyStore _key;
    private readonly PvSystemOverridesProvider? _overrides;
    private readonly ILogger<WeatherSourceSelection> _logger;
    private readonly IDisposable? _subscription;
    private readonly Lock _gate = new();
    private WeatherSource? _configured;
    private WeatherSource? _chosen;
    private WeatherSource _last;

    public WeatherSourceSelection(
        IOptionsMonitor<WeatherOptions> options,
        WeatherKeyStore key,
        IConfiguration configuration,
        ILogger<WeatherSourceSelection> logger)
    {
        _key = key;
        _overrides = PvSystemOverrides.Find(configuration);
        _logger = logger;
        _configured = Parse(options.CurrentValue.Source, warn: true);
        _last = Selected;

        // A reload fires for any change to the configuration, often twice per save: only a different
        // source is news, and a choice made on the page wins until the next start.
        _subscription = options.OnChange(next =>
        {
            _configured = Parse(next.Source, warn: false);
            Raise();
        });
    }

    public WeatherSource Selected => _chosen ?? _configured ?? (_key.Current is null ? WeatherSource.NotProvided : WeatherSource.OpenWeather);

    public bool IsChosen => _chosen is not null || _configured is not null;

    public event Action? Changed;

    public SettingsSaveResult Select(WeatherSource source)
    {
        if (!Enum.IsDefined(source))
        {
            return SettingsSaveResult.Refused(new ConfigurationProblems(
                $"{(int)source} is not a weather source.",
                Phrase.Of("Weather.UnknownSource", new PhraseArg.Text(((int)source).ToString(System.Globalization.CultureInfo.InvariantCulture)))));
        }

        if (_overrides is not null)
        {
            using (PvSystemOverrides.Lock(_overrides.Path))
            {
                try
                {
                    var stored = PvSystemOverrides.Read(_overrides.Path);
                    stored[Key] = source.ToString();
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

        _logger.LogInformation("Weather source {Source} chosen{Saved}.", source,
            _overrides is null ? string.Empty : $"; saved in {_overrides.Path} for the next start");
        _chosen = source;
        Raise();
        return SettingsSaveResult.Success;
    }

    public void Dispose() => _subscription?.Dispose();

    /// <summary><c>Weather:Source</c> as a source, or null when it is unset or names none.</summary>
    public static WeatherSource? Parse(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Enum.TryParse<WeatherSource>(value.Trim(), ignoreCase: true, out var source)
        && Enum.IsDefined(source)
        && !int.TryParse(value, out _)
            ? source
            : null;

    private WeatherSource? Parse(string? value, bool warn)
    {
        var source = Parse(value);

        if (warn && source is null && !string.IsNullOrWhiteSpace(value))
        {
            _logger.LogWarning(
                "{Key} is \"{Value}\", which names no weather source (NotProvided, OpenWeather, External); ignoring it.", Key, value);
        }

        return source;
    }

    private void Raise()
    {
        WeatherSource previous;
        var next = Selected;

        lock (_gate)
        {
            previous = _last;
            _last = next;
        }

        if (previous == next)
        {
            return;
        }

        _logger.LogInformation("Weather source switched from {Previous} to {Next}.", previous, next);
        Changed?.Invoke();
    }
}
