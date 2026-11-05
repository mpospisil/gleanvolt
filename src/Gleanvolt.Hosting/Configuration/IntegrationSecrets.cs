using System.Text.Json;
using Microsoft.Extensions.Configuration.CommandLine;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Hosting.Configuration;

/// <summary>
/// The broker password and the API keys saved on Settings, put into configuration at startup from the
/// secret store -- beside the overrides file in the order, so a value saved on the page beats the one a
/// long-forgotten <c>.env</c> carries, and under the command line, which beats everything.
///
/// <para>Configuration rather than a service reading the store, because both are consumed as options
/// bound once at startup (<c>HomeAssistantOptions</c>, <c>ApiOptions</c>) by code that has no reason to
/// know where a value came from. The vehicle account password arrives the same way; see
/// <see cref="VehicleAccountSecret"/>.</para>
/// </summary>
public static class IntegrationSecrets
{
    public static void AddIntegrationSecrets(this IConfigurationManager configuration, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var source = new IntegrationSecretsSource(VehicleAccountSecret.Open(configuration, contentRoot));

        var commandLine = configuration.Sources
            .Select((candidate, index) => (candidate, index))
            .LastOrDefault(entry => entry.candidate is CommandLineConfigurationSource);

        if (commandLine.candidate is null)
        {
            configuration.Sources.Add(source);
        }
        else
        {
            configuration.Sources.Insert(commandLine.index, source);
        }
    }

    /// <summary>The API keys saved on Settings, by name. Unreadable is none: the page asks again.</summary>
    public static Dictionary<string, string> ReadApiKeys(ISecretStore secrets)
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (secrets.Read(SecretNames.ApiKeys) is not { } stored)
        {
            return keys;
        }

        try
        {
            foreach (var (name, key) in JsonSerializer.Deserialize<Dictionary<string, string>>(stored) ?? [])
            {
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(key))
                {
                    keys[name] = key;
                }
            }
        }
        catch (JsonException)
        {
            // Nothing usable; the environment's keys, if any, still are.
        }

        return keys;
    }

    /// <summary>Writes the page's API keys; none at all removes the secret.</summary>
    public static bool WriteApiKeys(ISecretStore secrets, IReadOnlyDictionary<string, string> keys) =>
        keys.Count == 0
            ? secrets.Delete(SecretNames.ApiKeys)
            : secrets.Write(SecretNames.ApiKeys, JsonSerializer.Serialize(keys));
}

/// <summary>See <see cref="IntegrationSecrets"/>.</summary>
public sealed class IntegrationSecretsSource(ISecretStore secrets) : IConfigurationSource
{
    public ISecretStore Secrets { get; } = secrets;

    public IConfigurationProvider Build(IConfigurationBuilder builder) => new IntegrationSecretsProvider(Secrets);
}

/// <summary>See <see cref="IntegrationSecrets"/>. Its own type, so the editor can tell its values apart.</summary>
public sealed class IntegrationSecretsProvider(ISecretStore secrets) : ConfigurationProvider
{
    public override void Load()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (secrets.Read(SecretNames.MqttPassword) is { Length: > 0 } password)
        {
            data[IntegrationSettingKeys.MqttPassword] = password;
        }

        foreach (var (name, key) in IntegrationSecrets.ReadApiKeys(secrets))
        {
            data[$"{IntegrationSettingKeys.ApiKeysSection}:{name}"] = key;
        }

        Data = data;
    }
}
