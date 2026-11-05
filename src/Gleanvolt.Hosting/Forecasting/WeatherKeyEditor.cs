using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Infrastructure.OpenWeather;

namespace Gleanvolt.Hosting.Forecasting;

/// <summary>
/// The OpenWeatherMap key on Settings and in the setup wizard: the store that holds it and the service
/// that checks it, behind the one interface the page needs.
///
/// <para>A save is kept whatever the check says. A key created a minute ago is refused for up to a
/// couple of hours until OpenWeatherMap activates it, and making the owner come back to paste it again
/// would gain nothing: the page says what the check found, and the next session uses the key.</para>
/// </summary>
public sealed class WeatherKeyEditor : IWeatherKeyEditor
{
    private readonly WeatherKeyStore _key;
    private readonly OpenWeatherMapService _weather;
    private readonly ILogger<WeatherKeyEditor> _logger;

    public WeatherKeyEditor(WeatherKeyStore key, OpenWeatherMapService weather, ILogger<WeatherKeyEditor> logger)
    {
        _key = key;
        _weather = weather;
        _logger = logger;
    }

    public WeatherKeySettings Read() =>
        new(_key.Source, _key.Current ?? string.Empty, _key.EnvironmentConfigured, _key.Protection, _key.ProtectionPhrase);

    public async Task<WeatherKeySaveResult> SaveAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        var key = apiKey.Trim();

        if (key.Length == 0)
        {
            return new WeatherKeySaveResult(
                ["Enter the API key from your OpenWeatherMap account."],
                Persisted: false,
                WeatherKeyCheck.NotChecked,
                [Phrase.Of("Weather.EnterKey")]);
        }

        var persisted = _key.Save(key);
        if (!persisted)
        {
            _logger.LogWarning(
                "The OpenWeatherMap key saved on Settings is in use, but the secret store would not keep it; a restart will forget it.");
        }

        var check = await _weather.CheckKeyAsync(key, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("OpenWeatherMap key saved on Settings; checking it: {Check}.", check);

        return new WeatherKeySaveResult([], persisted, check);
    }

    public void Remove()
    {
        _key.Clear();
        _logger.LogInformation(
            _key.Current is null
                ? "OpenWeatherMap key removed on Settings; sessions are recorded without the weather."
                : "OpenWeatherMap key removed on Settings; the environment's key is in use again.");
    }
}
