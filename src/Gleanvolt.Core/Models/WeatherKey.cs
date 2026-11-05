namespace Gleanvolt.Core.Models;

/// <summary>Where the OpenWeatherMap key in use came from.</summary>
public enum WeatherKeySource
{
    /// <summary>No key: the weather is off and sessions are recorded without it.</summary>
    None,

    /// <summary><c>Weather__ApiKey</c> (<c>WEATHER_API_KEY</c> in the deploy's <c>.env</c>).</summary>
    Environment,

    /// <summary>Saved on Settings, in the secret store; wins over the environment's.</summary>
    Page,
}

/// <summary>What OpenWeatherMap made of a key when it was saved.</summary>
public enum WeatherKeyCheck
{
    /// <summary>Not asked: no key to ask with.</summary>
    NotChecked,

    /// <summary>OpenWeatherMap answered with the weather.</summary>
    Accepted,

    /// <summary>
    /// 401: the key is wrong -- or new. A freshly created key is refused for up to a couple of hours
    /// until OpenWeatherMap activates it, so a refusal is said, not acted on: the key is kept.
    /// </summary>
    Refused,

    /// <summary>Anything else -- no network, a timeout, an outage. Says nothing about the key.</summary>
    Failed,
}

/// <summary>The OpenWeatherMap key as Settings shows it.</summary>
/// <param name="Source">Where the key in use came from.</param>
/// <param name="ApiKey">The key in use, or empty. A secret: shown only behind a login, never logged.</param>
/// <param name="EnvironmentConfigured">Whether the environment holds a key, which takes over on Remove.</param>
/// <param name="SecretProtection">What protects the stored key, in the secret store's own words.</param>
/// <param name="SecretProtectionPhrase"><paramref name="SecretProtection"/> for the web UI.</param>
public sealed record WeatherKeySettings(
    WeatherKeySource Source,
    string ApiKey,
    bool EnvironmentConfigured,
    string SecretProtection,
    Phrase? SecretProtectionPhrase = null)
{
    public bool IsConfigured => Source != WeatherKeySource.None;

    /// <summary>Kept out of <c>ToString</c>, which a log template or a debugger would otherwise print whole.</summary>
    public override string ToString() => $"WeatherKeySettings {{ Source = {Source} }}";
}

/// <summary>What a save on Settings came to.</summary>
/// <param name="Errors">Why nothing was saved; empty when the key was saved.</param>
/// <param name="Persisted">False when the key is in use but the secret store would not keep it.</param>
/// <param name="Check">What OpenWeatherMap said to the saved key.</param>
/// <param name="ErrorPhrases">The same errors for the web UI to say in the viewer's language.</param>
public sealed record WeatherKeySaveResult(
    IReadOnlyList<string> Errors,
    bool Persisted,
    WeatherKeyCheck Check,
    IReadOnlyList<Phrase?>? ErrorPhrases = null)
{
    public bool Saved => Errors.Count == 0;
}
