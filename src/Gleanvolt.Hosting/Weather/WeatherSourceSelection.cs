using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Configuration;
using Microsoft.Extensions.Options;

namespace Gleanvolt.Hosting.Weather;

/// <summary>
/// The weather source (#424): <c>Weather:Source</c>, chosen on Settings or in the setup wizard, written
/// to the overrides file for the next start and used at once, so a switch needs no restart. Choosing
/// External is what turns the weather hub on; choosing NotProvided closes its provider's session
/// (<see cref="ExternalWeatherSelectionWatcher"/>).
///
/// <para><b>The built-in is gone (#425).</b> An installation that used it, by choosing OpenWeather or by
/// having a key while nothing was chosen, is moved to External when this is first built at start: the
/// choice is rewritten, the OpenWeatherMap key Settings kept is deleted from the secret store (it is
/// not handed to the plugin, which keeps its own), and the log and Settings say that the weather now
/// comes from the OpenWeather plugin. Settings keeps saying so, across restarts, until a weather
/// provider connects. A key still in <c>Weather__ApiKey</c> is not read; one warning a start says so.</para>
///
/// <para>Saved before it is applied: a choice that cannot be kept is refused rather than used until the
/// next start quietly undoes it. A host without an overrides file (a test host) applies it only.</para>
/// </summary>
public sealed class WeatherSourceSelection : IWeatherSourceSelection, IDisposable
{
    /// <summary>The configuration key the choice is saved under.</summary>
    public const string Key = WeatherOptions.SectionName + ":" + nameof(WeatherOptions.Source);

    /// <summary>
    /// Set in the overrides file when the built-in was moved to External (#425), and removed once a weather
    /// provider connects: what keeps the notice on Settings across restarts.
    /// </summary>
    public const string RetiredNoticeKey = WeatherOptions.SectionName + ":BuiltInRetired";

    /// <summary>The configuration key of the built-in's OpenWeatherMap key, which is no longer read.</summary>
    public const string RetiredApiKey = WeatherOptions.SectionName + ":ApiKey";

    /// <summary>The value the source had for the built-in.</summary>
    public const string RetiredSource = "OpenWeather";

    // The placeholder appsettings once shipped for the key: not a key anyone set.
    private const string PlaceholderApiKey = "your-openweathermap-api-key";

    private readonly PvSystemOverridesProvider? _overrides;
    private readonly ILogger<WeatherSourceSelection> _logger;
    private readonly IDisposable? _subscription;
    private readonly Lock _gate = new();
    private WeatherSource? _configured;
    private WeatherSource? _chosen;
    private WeatherSource _last;
    private bool _retired;

    public WeatherSourceSelection(
        IOptionsMonitor<WeatherOptions> options,
        ISecretStore secrets,
        IConfiguration configuration,
        ILogger<WeatherSourceSelection> logger)
    {
        _overrides = PvSystemOverrides.Find(configuration);
        _logger = logger;
        _retired = string.Equals(configuration[RetiredNoticeKey], "true", StringComparison.OrdinalIgnoreCase);

        var configured = options.CurrentValue.Source;
        var keyInEnvironment = configuration[RetiredApiKey] is { } key && !string.IsNullOrWhiteSpace(key) && key.Trim() != PlaceholderApiKey;
        var keyOnSettings = !string.IsNullOrWhiteSpace(secrets.Read(SecretNames.WeatherApiKey));

        if (IsRetired(configured) || (string.IsNullOrWhiteSpace(configured) && (keyInEnvironment || keyOnSettings)))
        {
            MoveToPlugin(configured);
        }
        else
        {
            _configured = Parse(configured, warn: true);
        }

        if (keyOnSettings)
        {
            // Not handed to the plugin: it keeps its own key, entered on its own page.
            var deleted = secrets.Delete(SecretNames.WeatherApiKey);
            _logger.LogInformation(deleted
                ? "The OpenWeatherMap key saved on Settings was deleted: Gleanvolt no longer calls OpenWeatherMap. Enter it on the OpenWeather plugin's page."
                : "The OpenWeatherMap key saved on Settings could not be deleted from the secret store; it is no longer read.");
        }

        if (keyInEnvironment)
        {
            _logger.LogWarning(
                "{Key} (WEATHER_API_KEY) is set, and Gleanvolt no longer reads it: OpenWeatherMap comes from the OpenWeather plugin, "
                + "which takes the key on its own page or as OPENWEATHER_API_KEY. The compose file passes WEATHER_API_KEY to the plugin "
                + "while OPENWEATHER_API_KEY is unset; remove it from Gleanvolt's own configuration.", RetiredApiKey);
        }

        _last = Selected;

        // A reload fires for any change to the configuration, often twice per save: only a different
        // source is news, and a choice made on the page wins until the next start.
        _subscription = options.OnChange(next =>
        {
            _configured = IsRetired(next.Source) ? WeatherSource.External : Parse(next.Source, warn: false);
            Raise();
        });
    }

    public WeatherSource Selected => _chosen ?? _configured ?? WeatherSource.NotProvided;

    public bool BuiltInRetired => _retired;

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
            try
            {
                Store(stored => stored[Key] = source.ToString());
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

        _logger.LogInformation("Weather source {Source} chosen{Saved}.", source,
            _overrides is null ? string.Empty : $"; saved in {_overrides.Path} for the next start");
        _chosen = source;
        Raise();
        return SettingsSaveResult.Success;
    }

    /// <summary>
    /// A weather provider connected (#425): the notice about the built-in has done its job. Removed from
    /// the overrides file too, so the next start doesn't bring it back. Nothing when there is no notice.
    /// </summary>
    public void ProviderConnected()
    {
        lock (_gate)
        {
            if (!_retired)
            {
                return;
            }

            _retired = false;
        }

        if (_overrides is not null)
        {
            try
            {
                Store(stored => stored.Remove(RetiredNoticeKey));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger.LogWarning("Could not clear {Key} in {Path} ({Reason}); the notice about the built-in OpenWeather comes back at the next start.",
                    RetiredNoticeKey, _overrides.Path, ex.Message);
            }
        }

        _logger.LogInformation("A weather provider connected; the notice about the built-in OpenWeather is gone.");
        Changed?.Invoke();
    }

    public void Dispose() => _subscription?.Dispose();

    /// <summary>
    /// <c>Weather:Source</c> as a source, or null when it is unset or names none. <c>OpenWeather</c>, the
    /// built-in's, names none: a value is moved from it only at start.
    /// </summary>
    public static WeatherSource? Parse(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Enum.TryParse<WeatherSource>(value.Trim(), ignoreCase: true, out var source)
        && Enum.IsDefined(source)
        && !int.TryParse(value, out _)
            ? source
            : null;

    /// <summary>Whether <paramref name="value"/> is the removed built-in's source.</summary>
    public static bool IsRetired(string? value) =>
        string.Equals(value?.Trim(), RetiredSource, StringComparison.OrdinalIgnoreCase);

    private void MoveToPlugin(string? configured)
    {
        _configured = WeatherSource.External;
        _retired = true;

        string saved;
        if (_overrides is null)
        {
            saved = "for this run";
        }
        else
        {
            try
            {
                Store(stored =>
                {
                    stored[Key] = nameof(WeatherSource.External);
                    stored[RetiredNoticeKey] = "true";
                });
                saved = $"saved in {_overrides.Path}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                saved = $"for this run only, since {_overrides.Path} could not be written ({ex.Message})";
            }
        }

        _logger.LogWarning(
            "The built-in OpenWeather is gone (#425), and this installation used it ({Was}); the weather source is now External, {Saved}. "
            + "OpenWeatherMap's weather now comes from the OpenWeather plugin: run it (GLEANVOLT_PLUGINS=openweather and GLEANVOLT_PROVIDER_TOKEN "
            + "in the deploy's .env, or its image or release zip), then enter the OpenWeatherMap key on its page under Settings -> Weather. "
            + "Until then sessions are recorded without weather.",
            string.IsNullOrWhiteSpace(configured) ? "an OpenWeatherMap key" : $"{Key}={configured.Trim()}", saved);
    }

    private void Store(Action<Dictionary<string, string>> change)
    {
        var path = _overrides!.Path;

        using (PvSystemOverrides.Lock(path))
        {
            var stored = PvSystemOverrides.Read(path);
            change(stored);
            PvSystemOverrides.Write(path, stored);
        }
    }

    private WeatherSource? Parse(string? value, bool warn)
    {
        var source = Parse(value);

        if (warn && source is null && !string.IsNullOrWhiteSpace(value))
        {
            _logger.LogWarning(
                "{Key} is \"{Value}\", which names no weather source (NotProvided, External); ignoring it.", Key, value);
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
