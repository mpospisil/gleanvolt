using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// The Home Assistant (MQTT) link and the REST API on Settings and in the setup wizard. Like the
/// installation's settings, every save is for the next start: the plain settings go to the overrides
/// file, the broker password and the API keys to the secret store, and a restart applies them.
/// </summary>
public interface IIntegrationsEditor
{
    IntegrationSettings Read();

    /// <summary>
    /// Saves plain settings from <see cref="IntegrationSettingKeys.Plain"/>, and the broker password when
    /// <paramref name="mqttPassword"/> is not null (empty removes the saved one). Refused, with the
    /// reasons, when the next start would not accept the result.
    /// </summary>
    SettingsSaveResult Save(IReadOnlyDictionary<string, string> values, string? mqttPassword = null);

    /// <summary>Takes a plain key back to what the environment or appsettings.json say.</summary>
    SettingsSaveResult Revert(string key);

    /// <summary>
    /// Connects to the broker once with these settings and disconnects. A null password uses the one
    /// the next start would read, so a test does not mean typing it again.
    /// </summary>
    Task<MqttTestResult> TestMqttAsync(
        string host, int port, string? username, string? password, CancellationToken cancellationToken = default);

    /// <summary>Creates a random key for a new client, saved for the next start.</summary>
    ApiKeyCreateResult CreateApiKey(string name);

    /// <summary>Removes a key saved here. The environment's keys are not this page's to remove.</summary>
    SettingsSaveResult RemoveApiKey(string name);
}
