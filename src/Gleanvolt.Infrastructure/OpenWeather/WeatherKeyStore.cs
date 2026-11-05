using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Infrastructure.OpenWeather;

/// <summary>
/// The OpenWeatherMap key in use, shared by Settings and <see cref="OpenWeatherMapService"/>.
///
/// <para>The same arrangement as the Solcast account: the page's key wins over the environment's,
/// because <c>docker-compose.yml</c> always sets <c>Weather__ApiKey</c>, empty or not, and the page
/// could otherwise never change it. The service reads <see cref="Current"/> on every call, so a key
/// saved on the page is used by the next session without a restart.</para>
///
/// <para>A failure to save is not allowed to take the process down: the key in memory works until the
/// process ends, and the caller says a restart will forget it.</para>
/// </summary>
public sealed class WeatherKeyStore
{
    /// <summary>The value <c>.env.example</c> ships with.</summary>
    public const string PlaceholderKey = "your-openweathermap-api-key";

    private readonly ISecretStore _secrets;
    private readonly string? _environment;
    private volatile string? _saved;

    public WeatherKeyStore(ISecretStore secrets, IOptions<WeatherOptions> options)
    {
        _secrets = secrets;
        // .env.example's placeholder is no key: copied as is, it would be sent and refused.
        _environment = Clean(options.Value.ApiKey) is { } key && key != PlaceholderKey ? key : null;
        _saved = Clean(secrets.Read(SecretNames.WeatherApiKey));
    }

    /// <summary>The key in use -- the page's, else the environment's -- or null when there is none.</summary>
    public string? Current => _saved ?? _environment;

    /// <summary>Where <see cref="Current"/> came from.</summary>
    public WeatherKeySource Source => _saved is not null
        ? WeatherKeySource.Page
        : _environment is not null ? WeatherKeySource.Environment : WeatherKeySource.None;

    /// <summary>Whether the environment holds a key, which is in use whenever the page holds none.</summary>
    public bool EnvironmentConfigured => _environment is not null;

    /// <summary>What protects the saved key. Never the key itself.</summary>
    public string Protection => _secrets.Describe();

    /// <summary><see cref="Protection"/> for the web UI.</summary>
    public Phrase? ProtectionPhrase => _secrets.DescribePhrase();

    /// <summary>
    /// Makes this the key in use and writes it to the secret store. Returns whether it stuck -- the key
    /// is in use either way.
    /// </summary>
    public bool Save(string apiKey)
    {
        var key = Clean(apiKey) ?? throw new ArgumentException("An OpenWeatherMap key cannot be empty.", nameof(apiKey));
        _saved = key;
        return _secrets.Write(SecretNames.WeatherApiKey, key);
    }

    /// <summary>Forgets the page's key, in memory and on disk. The environment's is in use again, if any.</summary>
    public void Clear()
    {
        _saved = null;
        _secrets.Delete(SecretNames.WeatherApiKey);
    }

    private static string? Clean(string? key) => string.IsNullOrWhiteSpace(key) ? null : key.Trim();
}
