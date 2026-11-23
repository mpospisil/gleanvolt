using Gleanvolt.Providers.Client;
using Microsoft.Extensions.Configuration;

namespace Gleanvolt.Chargers;

/// <summary>Where a setting in use came from.</summary>
public enum SettingSource
{
    /// <summary>Not set.</summary>
    None,

    /// <summary>Saved on the plugin's page.</summary>
    Page,

    /// <summary>The plugin's configuration: its settings file, the environment or the command line.</summary>
    Configuration,
}

/// <summary>
/// One setting the owner enters on the plugin's page (the charger's address, its model, its password), kept with
/// the SDK's store in the data directory, owner-only. The configuration's value is used until one is saved, so a
/// container can be set up from its environment alone.
/// </summary>
public sealed class SavedSetting
{
    private readonly IProviderSecretStore _files;
    private readonly string _fileName;
    private readonly string? _configured;
    private volatile string? _saved;

    /// <param name="files">The SDK's store.</param>
    /// <param name="fileName">The file in the data directory: letters, digits, '.', '-' and '_'.</param>
    /// <param name="configuration">The plugin's configuration.</param>
    /// <param name="key">The configuration key, e.g. <c>Solax:Host</c>.</param>
    public SavedSetting(IProviderSecretStore files, string fileName, IConfiguration configuration, string key)
    {
        _files = files;
        _fileName = fileName;
        _configured = Clean(configuration[key]);
        _saved = Clean(files.Read(fileName));
    }

    /// <summary>The value in use, the page's or the configuration's, or null.</summary>
    public string? Current => _saved ?? _configured;

    /// <summary>Where <see cref="Current"/> came from.</summary>
    public SettingSource Source => _saved is not null ? SettingSource.Page
        : _configured is not null ? SettingSource.Configuration
        : SettingSource.None;

    /// <summary>Raised when the value in use changes.</summary>
    public event Action? Changed;

    /// <summary>Makes <paramref name="value"/> the one in use and saves it. False when it could not be written; it is in use either way.</summary>
    public bool Save(string value)
    {
        _saved = Clean(value);
        var written = _saved is null ? _files.Delete(_fileName) : _files.Write(_fileName, _saved);
        Changed?.Invoke();
        return written;
    }

    /// <summary>Forgets the page's value. The configuration's, if any, is in use again.</summary>
    public void Clear()
    {
        _saved = null;
        _files.Delete(_fileName);
        Changed?.Invoke();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>A device's address on the LAN: a host, and a port when it is not the device's usual one.</summary>
/// <param name="Host">A host name or an IPv4 address.</param>
/// <param name="Port">The port.</param>
public sealed record DeviceAddress(string Host, int Port)
{
    /// <summary>
    /// <paramref name="text"/> as an address, <paramref name="defaultPort"/> when it names none: <c>192.168.1.6</c>,
    /// <c>charger.local:8080</c>, or an <c>http://</c> address with nothing after the host. Null when it is none of these.
    /// </summary>
    public static DeviceAddress? Parse(string? text, int defaultPort)
    {
        var trimmed = text?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "http://" + trimmed;
        }

        if (!Uri.TryCreate(trimmed + "/", UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp
            || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Host.Length == 0 || uri.UserInfo.Length > 0)
        {
            return null;
        }

        return new DeviceAddress(uri.Host, uri.IsDefaultPort && !trimmed.EndsWith(":80", StringComparison.Ordinal) ? defaultPort : uri.Port);
    }

    /// <summary>The address as the owner writes it.</summary>
    public override string ToString() => $"{Host}:{Port}";

    /// <summary>The address as an HTTP base address.</summary>
    public Uri ToHttpBase() => new($"http://{Host}:{Port}/");
}
