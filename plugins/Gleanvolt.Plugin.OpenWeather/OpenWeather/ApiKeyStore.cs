using Gleanvolt.Providers.Client;

namespace Gleanvolt.Plugin.OpenWeather.OpenWeather;

/// <summary>Where the key in use came from.</summary>
public enum ApiKeySource
{
    /// <summary>No key.</summary>
    None,

    /// <summary>Saved on the plugin's page.</summary>
    Page,

    /// <summary><c>OpenWeather:ApiKey</c> (or <c>OPENWEATHER_API_KEY</c>) in the configuration.</summary>
    Environment,
}

/// <summary>
/// The OpenWeatherMap key (#424). In Gleanvolt it is <c>Weather:ApiKey</c>, on Settings and in the secret
/// store; here the owner enters it on the plugin's page, and it is kept through the SDK's secret store
/// (#416). The page's key wins over the configuration's, as Gleanvolt's Settings did. Never logged, never
/// shown back.
/// </summary>
public sealed class ApiKeyStore
{
    /// <summary>The secret's name in the data directory.</summary>
    public const string FileName = "api-key";

    private readonly IProviderSecretStore _files;
    private readonly string? _environment;
    private volatile string? _saved;

    public ApiKeyStore(IProviderSecretStore files, IConfiguration configuration)
    {
        _files = files;
        _environment = Clean(configuration["OpenWeather:ApiKey"]) ?? Clean(configuration["OPENWEATHER_API_KEY"]);
        _saved = Clean(files.Read(FileName));
    }

    /// <summary>The key in use, the page's or the configuration's, or null.</summary>
    public string? Current => _saved ?? _environment;

    /// <summary>Where <see cref="Current"/> came from.</summary>
    public ApiKeySource Source => _saved is not null ? ApiKeySource.Page
        : _environment is not null ? ApiKeySource.Environment
        : ApiKeySource.None;

    /// <summary>Whether the files are encrypted at rest (Windows) or only closed to other users.</summary>
    public bool IsEncrypted => _files.IsEncrypted;

    /// <summary>Makes <paramref name="key"/> the key in use and saves it. False when it could not be written; it is in use either way.</summary>
    public bool Save(string key)
    {
        var clean = Clean(key) ?? throw new ArgumentException("An OpenWeatherMap key cannot be empty.", nameof(key));
        _saved = clean;
        return _files.Write(FileName, clean);
    }

    /// <summary>Forgets the page's key. The configuration's, if any, is in use again.</summary>
    public void Clear()
    {
        _saved = null;
        _files.Delete(FileName);
    }

    private static string? Clean(string? key) => string.IsNullOrWhiteSpace(key) ? null : key.Trim();
}
