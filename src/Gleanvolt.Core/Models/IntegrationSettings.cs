namespace Gleanvolt.Core.Models;

/// <summary>
/// The Home Assistant (MQTT) link and the REST API, as Settings and the setup wizard edit them. Both are
/// read once at startup -- the MQTT worker connects with what it was given, and the API's routes are
/// mapped or not -- so a save is written for the next start, as the installation's settings are.
/// </summary>
public static class IntegrationSettingKeys
{
    public const string MqttEnabled = "HomeAssistant:Enabled";
    public const string MqttBrokerHost = "HomeAssistant:BrokerHost";
    public const string MqttBrokerPort = "HomeAssistant:BrokerPort";
    public const string MqttUsername = "HomeAssistant:Username";
    public const string MqttDiscoveryPrefix = "HomeAssistant:DiscoveryPrefix";

    /// <summary>A secret: kept in the secret store, never in the overrides file.</summary>
    public const string MqttPassword = "HomeAssistant:Password";

    public const string ApiEnabled = "Api:Enabled";

    /// <summary>The API's keys, one per client, under their names: <c>Api:Keys:&lt;name&gt;</c>. Secrets.</summary>
    public const string ApiKeysSection = "Api:Keys";

    /// <summary>The Home Assistant link's plain settings, in the order the form shows them.</summary>
    public static IReadOnlyList<string> Mqtt { get; } =
        [MqttEnabled, MqttBrokerHost, MqttBrokerPort, MqttUsername, MqttDiscoveryPrefix];

    /// <summary>Every plain (non-secret) key the editor writes to the overrides file.</summary>
    public static IReadOnlyList<string> Plain { get; } = [.. Mqtt, ApiEnabled];

    public static bool IsPlain(string key) => Plain.Contains(key, StringComparer.OrdinalIgnoreCase);
}

/// <summary>The Home Assistant link and the API as the next start would read them, beside what is running.</summary>
/// <param name="OverridesPath">The file the plain settings are saved to.</param>
/// <param name="Settings">Every key in <see cref="IntegrationSettingKeys.Plain"/>.</param>
/// <param name="MqttPassword">Whether a broker password is set, where it comes from, and whether it is running.</param>
/// <param name="ApiKeys">The API keys the next start reads, the environment's and the page's.</param>
/// <param name="SecretProtection">What protects the saved secrets, in the secret store's own words.</param>
/// <param name="Unavailable">Why nothing can be edited here, when nothing can.</param>
public sealed record IntegrationSettings(
    string OverridesPath,
    IReadOnlyList<ConfiguredSetting> Settings,
    SecretSettingState MqttPassword,
    IReadOnlyList<ApiKeyEntry> ApiKeys,
    string SecretProtection,
    Phrase? SecretProtectionPhrase = null,
    string? Unavailable = null)
{
    public ConfiguredSetting this[string key] =>
        Settings.First(setting => string.Equals(setting.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>How many saved changes the running process does not have yet.</summary>
    public int PendingCount(IEnumerable<string> keys) =>
        keys.Count(key => this[key].IsPending);
}

/// <summary>A secret setting, described without its value.</summary>
/// <param name="IsSet">Whether the next start has one.</param>
/// <param name="Source">Where it comes from: <see cref="SettingSource.SecretStore"/> when saved here.</param>
/// <param name="IsPending">Whether it differs from what is running, so a restart is needed.</param>
public sealed record SecretSettingState(bool IsSet, SettingSource Source, bool IsPending);

/// <summary>One API key.</summary>
/// <param name="Name">What the client is called; recorded as the source of what it does.</param>
/// <param name="Key">The key itself. A secret: shown only behind a login.</param>
/// <param name="Source">The environment's, or saved here (<see cref="SettingSource.SecretStore"/>).</param>
/// <param name="IsPending">True when it is not what the running process accepts yet -- new, or changed.</param>
public sealed record ApiKeyEntry(string Name, string Key, SettingSource Source, bool IsPending)
{
    public override string ToString() => $"ApiKeyEntry {{ Name = {Name}, Source = {Source} }}";
}

/// <summary>What a broker connection test came to.</summary>
public sealed record MqttTestResult(bool Connected, string Message, Phrase? MessagePhrase = null);

/// <summary>A key created on the page, or why not.</summary>
public sealed record ApiKeyCreateResult(SettingsSaveResult Result, string? Key)
{
    public override string ToString() => $"ApiKeyCreateResult {{ Saved = {Result.Saved} }}";
}
