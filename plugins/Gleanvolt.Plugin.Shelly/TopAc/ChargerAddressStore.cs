using Gleanvolt.Providers.Client;

namespace Gleanvolt.Plugin.Shelly.TopAc;

/// <summary>Where the address in use came from.</summary>
public enum AddressSource
{
    /// <summary>No address.</summary>
    None,

    /// <summary>Saved on the plugin's page.</summary>
    Page,

    /// <summary><c>Shelly:Host</c> in the configuration.</summary>
    Configuration,
}

/// <summary>
/// The charger's address (#429). Gleanvolt has none for a charger behind a plugin, so the owner enters it on
/// the plugin's page, which saves it with the SDK's store beside the page's keys; <c>Shelly:Host</c> in the
/// configuration is used until one is saved. A host name or IP address, with a port when it is not 80.
/// </summary>
public sealed class ChargerAddressStore
{
    /// <summary>The file's name in the data directory.</summary>
    public const string FileName = "charger-address";

    private readonly IProviderSecretStore _files;
    private readonly Uri? _configured;
    private volatile Uri? _saved;

    public ChargerAddressStore(IProviderSecretStore files, IConfiguration configuration)
    {
        _files = files;
        _configured = Parse(configuration["Shelly:Host"]);
        _saved = Parse(files.Read(FileName));
    }

    /// <summary>The charger's base address in use, the page's or the configuration's, or null.</summary>
    public Uri? Current => _saved ?? _configured;

    /// <summary>Where <see cref="Current"/> came from.</summary>
    public AddressSource Source => _saved is not null ? AddressSource.Page
        : _configured is not null ? AddressSource.Configuration
        : AddressSource.None;

    /// <summary>Raised when the address in use changes.</summary>
    public event Action? Changed;

    /// <summary>
    /// <paramref name="text"/> as a charger's base address: <c>192.168.1.50</c>, <c>shelly.local:8080</c> or a
    /// full <c>http://</c> address; null when it is none of these.
    /// </summary>
    public static Uri? Parse(string? text)
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

        return Uri.TryCreate(trimmed + "/", UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Host.Length > 0
            ? uri
            : null;
    }

    /// <summary>Makes <paramref name="address"/> the one in use and saves it. False when it could not be written; it is in use either way.</summary>
    public bool Save(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        _saved = address;
        var written = _files.Write(FileName, address.AbsoluteUri);
        Changed?.Invoke();
        return written;
    }

    /// <summary>Forgets the page's address. The configuration's, if any, is in use again.</summary>
    public void Clear()
    {
        _saved = null;
        _files.Delete(FileName);
        Changed?.Invoke();
    }
}
