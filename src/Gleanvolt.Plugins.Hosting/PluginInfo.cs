using System.Reflection;
using Gleanvolt.Providers.Client;
using Microsoft.Extensions.Configuration;

namespace Gleanvolt.Plugins;

/// <summary>What a plugin is.</summary>
/// <param name="ProviderId">Its id in the hello, stable for good: Gleanvolt remembers the first it accepts.</param>
/// <param name="Name">What Gleanvolt and the page show, e.g. "SolaX X3-EVC".</param>
/// <param name="Section">Its configuration section, e.g. <c>Solax</c>: <c>Solax:Host</c>, <c>Solax:DataDirectory</c>.</param>
/// <param name="Port">The page's port when <c>Urls</c> is not set.</param>
/// <param name="DefaultDevicePort">
/// For a plugin that talks to a device on the LAN, the device's own port when an address names none: 502 for Modbus, 80
/// for HTTP. 0 otherwise.
/// </param>
public sealed record PluginInfo(string ProviderId, string Name, string Section, int Port, int DefaultDevicePort = 0)
{
    /// <summary>The plugin's version, from its assembly: the release tag's, or 0.0.0-dev.</summary>
    public string Version { get; init; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0-dev";

    /// <summary>The assembly with the plugin's page.</summary>
    public Assembly PageAssembly { get; init; } = Assembly.GetEntryAssembly()!;
}

/// <summary>The settings a plugin's page saves, each one readable from the configuration too.</summary>
public sealed class PluginSettings
{
    private readonly IProviderSecretStore _files;
    private readonly IConfiguration _configuration;
    private readonly string _section;
    private readonly Dictionary<string, SavedSetting> _settings = new(StringComparer.Ordinal);

    public PluginSettings(IProviderSecretStore files, IConfiguration configuration, PluginInfo plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        _files = files;
        _configuration = configuration;
        _section = plugin.Section;
    }

    /// <summary>The setting <paramref name="name"/>: <c>&lt;Section&gt;:&lt;Name&gt;</c>, or what the page saved under that name.</summary>
    public SavedSetting Get(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_settings)
        {
            if (!_settings.TryGetValue(name, out var setting))
            {
                var key = $"{_section}:{char.ToUpperInvariant(name[0])}{name[1..]}";
                setting = new SavedSetting(_files, "setting-" + name, _configuration, key);
                _settings[name] = setting;
            }

            return setting;
        }
    }
}
