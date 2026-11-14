using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using MQTTnet;
using MQTTnet.Adapter;
using MQTTnet.Client;
using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Hosting.Configuration;

/// <summary>
/// What a process started with, for the Home Assistant link and the API: the editor's "running" side.
/// Taken at registration, while it is certainly what the options were bound from.
/// </summary>
public sealed record IntegrationsSnapshot(
    IReadOnlyDictionary<string, string?> Plain,
    string? MqttPassword,
    IReadOnlyDictionary<string, string> ApiKeys)
{
    public static IntegrationsSnapshot From(IConfiguration configuration) => new(
        IntegrationSettingKeys.Plain.ToDictionary(key => key, key => configuration[key], StringComparer.OrdinalIgnoreCase),
        configuration[IntegrationSettingKeys.MqttPassword],
        configuration.GetSection(IntegrationSettingKeys.ApiKeysSection).GetChildren()
            .Where(child => !string.IsNullOrWhiteSpace(child.Value))
            .ToDictionary(child => child.Key, child => child.Value!, StringComparer.OrdinalIgnoreCase));

    public override string ToString() => "IntegrationsSnapshot";
}

/// <summary>
/// <see cref="IIntegrationsEditor"/>: the plain settings in the overrides file, under the lock every
/// writer of it shares, and the broker password and API keys in the secret store.
///
/// <para><b>Checked as the next start would check it.</b> Home Assistant on needs a broker and the
/// installation's id (the topic segment two installations on one broker are told apart by); the API on
/// needs a key. A save that would make the next start refuse is refused here instead, with the same
/// reason, rather than discovered after a restart.</para>
/// </summary>
public sealed partial class IntegrationsEditor : IIntegrationsEditor
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(6);

    // A created API key: 24 letters and digits, about 143 bits -- beyond guessing against a server, and
    // short enough to paste into a client's settings without scrolling. No punctuation, so a
    // double-click selects all of it.
    private const string KeyAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private const int KeyLength = 24;

    private readonly IConfigurationRoot? _configuration;
    private readonly PvSystemOverridesProvider? _overrides;
    private readonly IntegrationsSnapshot _running;
    private readonly ISecretStore _secrets;
    private readonly ILogger<IntegrationsEditor> _logger;

    public IntegrationsEditor(
        IConfiguration configuration,
        IntegrationsSnapshot running,
        ISecretStore secrets,
        ILogger<IntegrationsEditor> logger)
    {
        _configuration = configuration as IConfigurationRoot;
        _overrides = PvSystemOverrides.Find(configuration);
        _running = running;
        _secrets = secrets;
        _logger = logger;
    }

    private static Lock Gate => PvSystemOverrides.Gate;

    public IntegrationSettings Read()
    {
        if (_configuration is null || _overrides is null)
        {
            return new IntegrationSettings(
                string.Empty,
                [.. IntegrationSettingKeys.Plain.Select(key => new ConfiguredSetting(
                    key, _running.Plain[key], _running.Plain[key], SettingSource.Default, string.Empty, _running.Plain[key]))],
                new SecretSettingState(!string.IsNullOrEmpty(_running.MqttPassword), SettingSource.Default, false),
                [],
                _secrets.Describe(),
                _secrets.DescribePhrase(),
                "This host does not read a web UI overrides file, so an edit here would never be applied.");
        }

        lock (Gate)
        {
            Dictionary<string, string> stored;

            try
            {
                stored = PvSystemOverrides.Read(_overrides.Path);
            }
            catch (InvalidOperationException ex)
            {
                return new IntegrationSettings(
                    _overrides.Path, Describe(new Dictionary<string, string>()), Password(), ApiKeys(), _secrets.Describe(), _secrets.DescribePhrase(), ex.Message);
            }

            return new IntegrationSettings(
                _overrides.Path, Describe(stored), Password(), ApiKeys(), _secrets.Describe(), _secrets.DescribePhrase());
        }
    }

    public SettingsSaveResult Save(IReadOnlyDictionary<string, string> values, string? mqttPassword = null)
    {
        var refused = values.Keys.Where(key => !IntegrationSettingKeys.IsPlain(key)).ToList();
        if (refused.Count > 0)
        {
            return SettingsSaveResult.Refused(ConfigurationProblems.NotEditable(refused));
        }

        var result = Change(stored =>
        {
            foreach (var (key, raw) in values)
            {
                var value = raw.Trim();
                var canonical = IntegrationSettingKeys.Plain.First(
                    candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase));

                // Empty means "not set here", as for a key the form never touched: the environment or
                // the default applies. Every key here is a flag, a port or a broker setting, none of
                // which means anything empty -- stored as "", it overrode the environment with nothing,
                // and an empty port stopped the next start (#396). And the same as the key would be
                // anyway is not an edit, as on the installation's form.
                if (value.Length == 0
                    || string.Equals(value, Underlying(canonical) ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    stored.Remove(canonical);
                }
                else
                {
                    stored[canonical] = value;
                }
            }

            // Any empty one an earlier version saved goes with the next save, whichever field it is.
            foreach (var key in IntegrationSettingKeys.Plain)
            {
                if (stored.TryGetValue(key, out var left) && left.Trim().Length == 0)
                {
                    stored.Remove(key);
                }
            }
        });

        if (!result.Saved || mqttPassword is null)
        {
            return result;
        }

        var saved = mqttPassword.Length == 0
            ? _secrets.Delete(SecretNames.MqttPassword)
            : _secrets.Write(SecretNames.MqttPassword, mqttPassword);

        if (!saved)
        {
            return SettingsSaveResult.Refused(SecretNotSaved("MQTT password"));
        }

        _logger.LogInformation(
            mqttPassword.Length == 0
                ? "MQTT broker password removed on Settings; it takes effect on the next start."
                : "MQTT broker password saved on Settings; it takes effect on the next start.");

        return result;
    }

    public SettingsSaveResult Revert(string key) =>
        IntegrationSettingKeys.IsPlain(key)
            ? Change(stored => stored.Remove(key))
            : SettingsSaveResult.Refused(ConfigurationProblems.NotEditable([key]));

    public async Task<MqttTestResult> TestMqttAsync(
        string host, int port, string? username, string? password, CancellationToken cancellationToken = default)
    {
        host = host.Trim();
        var address = $"{host}:{port}";

        if (host.Length == 0 || port is < 1 or > 65535)
        {
            var problem = MqttProblems(host, port.ToString(CultureInfo.InvariantCulture));
            return new MqttTestResult(false, problem[0], problem.Phrases[0]);
        }

        password ??= NextPassword()?.Value;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TestTimeout);

        try
        {
            using var client = new MqttFactory().CreateMqttClient();

            // A client id of its own, so the test never knocks the running link off the broker -- which
            // is what a second connection under the same id would do.
            var builder = new MqttClientOptionsBuilder()
                .WithTcpServer(host, port)
                .WithClientId("gleanvolt-test-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)))
                .WithTimeout(TestTimeout);

            if (!string.IsNullOrWhiteSpace(username))
            {
                builder.WithCredentials(username.Trim(), password);
            }

            await client.ConnectAsync(builder.Build(), timeout.Token).ConfigureAwait(false);
            await client.DisconnectAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);

            return new MqttTestResult(true, $"Connected to {address}.", Phrase.Of("Mqtt.TestConnected", new PhraseArg.Text(address)));
        }
        catch (MqttConnectingFailedException ex)
            when (ex.ResultCode is MqttClientConnectResultCode.BadUserNameOrPassword or MqttClientConnectResultCode.NotAuthorized)
        {
            return new MqttTestResult(
                false,
                $"{address} answered, and refused the user name or password.",
                Phrase.Of("Mqtt.TestBadCredentials", new PhraseArg.Text(address)));
        }
        catch (MqttConnectingFailedException ex)
        {
            return new MqttTestResult(
                false,
                $"{address} answered, and refused the connection ({ex.ResultCode}).",
                Phrase.Of("Mqtt.TestRefused", new PhraseArg.Text(address), new PhraseArg.Text(ex.ResultCode.ToString())));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var reason = ex is OperationCanceledException ? "no answer" : ex.Message;
            return new MqttTestResult(
                false,
                $"{address} did not answer ({reason}).",
                Phrase.Of("Probe.NoAnswer", new PhraseArg.Text(address), new PhraseArg.Text(reason)));
        }
    }

    public ApiKeyCreateResult CreateApiKey(string name)
    {
        name = name.Trim();

        if (!KeyName().IsMatch(name))
        {
            return new ApiKeyCreateResult(
                SettingsSaveResult.Refused(new ConfigurationProblems(
                    $"'{name}' is not a usable key name: letters, digits, '-', '_' and '.', up to 40 characters.",
                    Phrase.Of("Config.ApiKeyBadName", new PhraseArg.Text(name)))),
                null);
        }

        lock (Gate)
        {
            if (ApiKeys().Any(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return new ApiKeyCreateResult(
                    SettingsSaveResult.Refused(new ConfigurationProblems(
                        $"There is already a key called '{name}'.",
                        Phrase.Of("Config.ApiKeyExists", new PhraseArg.Text(name)))),
                    null);
            }

            var saved = IntegrationSecrets.ReadApiKeys(_secrets);
            var key = RandomNumberGenerator.GetString(KeyAlphabet, KeyLength);
            saved[name] = key;

            if (!IntegrationSecrets.WriteApiKeys(_secrets, saved))
            {
                return new ApiKeyCreateResult(SettingsSaveResult.Refused(SecretNotSaved("API key")), null);
            }

            _logger.LogInformation("API key {Name} created on Settings; it is accepted from the next start.", name);
            return new ApiKeyCreateResult(SettingsSaveResult.Success, key);
        }
    }

    public SettingsSaveResult RemoveApiKey(string name)
    {
        lock (Gate)
        {
            var saved = IntegrationSecrets.ReadApiKeys(_secrets);

            if (!saved.Remove(name))
            {
                return SettingsSaveResult.Refused(new ConfigurationProblems(
                    $"'{name}' is not a key saved here; a key from the environment is removed there.",
                    Phrase.Of("Config.ApiKeyNotHere", new PhraseArg.Text(name))));
            }

            // The last key of an API that is on: the next start would refuse a keyless API.
            if (IsOn(Next(IntegrationSettingKeys.ApiEnabled, Stored())?.Value)
                && !ApiKeys().Any(entry => !string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return SettingsSaveResult.Refused(new ConfigurationProblems(
                    "The API is on, and this is its last key. Turn the API off first, or create another key.",
                    Phrase.Of("Config.ApiLastKey")));
            }

            if (!IntegrationSecrets.WriteApiKeys(_secrets, saved))
            {
                return SettingsSaveResult.Refused(SecretNotSaved("API key"));
            }

            _logger.LogInformation("API key {Name} removed on Settings; it is refused from the next start.", name);
            return SettingsSaveResult.Success;
        }
    }

    // One read-modify-write of the overrides file: apply the change, check the result would start, write it.
    private SettingsSaveResult Change(Action<Dictionary<string, string>> change)
    {
        if (_configuration is null || _overrides is null)
        {
            return SettingsSaveResult.Refused(ConfigurationProblems.NoOverridesFile());
        }

        using (PvSystemOverrides.Lock(_overrides.Path))
        {
            Dictionary<string, string> stored;

            try
            {
                stored = PvSystemOverrides.Read(_overrides.Path);
            }
            catch (InvalidOperationException ex)
            {
                return SettingsSaveResult.Refused([ex.Message]);
            }

            var before = new Dictionary<string, string>(stored, StringComparer.OrdinalIgnoreCase);
            change(stored);

            if (stored.Count == before.Count
                && stored.All(entry => before.TryGetValue(entry.Key, out var old) && old == entry.Value))
            {
                return SettingsSaveResult.Success;
            }

            var problems = Check(stored);
            if (problems.Count > 0)
            {
                return SettingsSaveResult.Refused(problems);
            }

            try
            {
                PvSystemOverrides.Write(_overrides.Path, stored);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return SettingsSaveResult.Refused(ConfigurationProblems.NotWritten(_overrides.Path, ex.Message));
            }

            _logger.LogInformation(
                "Home Assistant / API settings saved from the web UI in {Path}; they take effect on the next start.",
                _overrides.Path);

            return SettingsSaveResult.Success;
        }
    }

    // What the next start would refuse, judged on the configuration it would read.
    private ConfigurationProblems Check(IReadOnlyDictionary<string, string> stored)
    {
        var problems = new ConfigurationProblems();

        if (IsOn(Next(IntegrationSettingKeys.MqttEnabled, stored)?.Value))
        {
            var host = Next(IntegrationSettingKeys.MqttBrokerHost, stored)?.Value?.Trim() ?? string.Empty;
            var port = Next(IntegrationSettingKeys.MqttBrokerPort, stored)?.Value?.Trim() ?? "1883";
            var broker = MqttProblems(host, port);
            for (var index = 0; index < broker.Count; index++)
            {
                problems.Add(broker[index], broker.Phrases[index]!);
            }

            if (string.IsNullOrWhiteSpace(Next(PvSystemSettingKeys.Id, stored)?.Value))
            {
                problems.Add(
                    "Home Assistant needs the installation's id (Installation → Id): it is the topic segment this "
                    + "system publishes under, so two installations on one broker do not overwrite each other.",
                    Phrase.Of("Config.MqttNeedsPvId"));
            }
        }
        // Off, the port still has to bind as a number at the next start, so anything set -- empty
        // included -- is held to it (#396). Unset is fine: the default applies.
        else if (Next(IntegrationSettingKeys.MqttBrokerPort, stored)?.Value?.Trim() is { } port
            && !(int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 65535))
        {
            problems.Add($"The broker port ('{port}') must be a number from 1 to 65535.",
                Phrase.Of("Config.MqttBadPort", new PhraseArg.Text(port)));
        }

        var enabled = Next(IntegrationSettingKeys.MqttEnabled, stored)?.Value?.Trim();
        var api = Next(IntegrationSettingKeys.ApiEnabled, stored)?.Value?.Trim();

        foreach (var flag in new[] { enabled, api })
        {
            if (flag is { Length: > 0 } && !bool.TryParse(flag, out _))
            {
                problems.Add($"'{flag}' is not true or false.", Phrase.Of("Config.NotABoolean", new PhraseArg.Text(flag)));
            }
        }

        if (IsOn(api) && ApiKeys().Count == 0)
        {
            problems.Add(
                "The API cannot be turned on without a key: create one below first.",
                Phrase.Of("Config.ApiNoKeys"));
        }

        return problems;
    }

    private static ConfigurationProblems MqttProblems(string host, string port)
    {
        var problems = new ConfigurationProblems();

        if (host.Length == 0)
        {
            problems.Add("Home Assistant needs the MQTT broker's address.", Phrase.Of("Config.MqttNoBroker"));
        }

        if (!(int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 65535))
        {
            problems.Add($"The broker port ('{port}') must be a number from 1 to 65535.",
                Phrase.Of("Config.MqttBadPort", new PhraseArg.Text(port)));
        }

        return problems;
    }

    private static bool IsOn(string? value) => bool.TryParse(value?.Trim(), out var on) && on;

    private Dictionary<string, string> Stored()
    {
        try
        {
            return _overrides is null ? [] : PvSystemOverrides.Read(_overrides.Path);
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    private SecretSettingState Password()
    {
        var next = NextPassword();
        return new SecretSettingState(
            next is not null,
            next?.Source ?? SettingSource.Default,
            !string.Equals(next?.Value ?? string.Empty, _running.MqttPassword ?? string.Empty, StringComparison.Ordinal));
    }

    // The page's password, else whatever the configuration below the page's secrets says.
    private (string Value, SettingSource Source)? NextPassword()
    {
        if (_secrets.Read(SecretNames.MqttPassword) is { Length: > 0 } saved)
        {
            return (saved, SettingSource.SecretStore);
        }

        return Next(IntegrationSettingKeys.MqttPassword, Stored()) is { Value: { Length: > 0 } value } found
            ? (value, found.Source)
            : null;
    }

    // The keys the next start accepts: the environment's, with the page's on top under the same name.
    private List<ApiKeyEntry> ApiKeys()
    {
        var keys = new Dictionary<string, (string Key, SettingSource Source)>(StringComparer.OrdinalIgnoreCase);

        if (_configuration is not null)
        {
            foreach (var provider in _configuration.Providers)
            {
                if (provider is IntegrationSecretsProvider or PvSystemOverridesProvider)
                {
                    continue;
                }

                foreach (var name in provider.GetChildKeys([], IntegrationSettingKeys.ApiKeysSection).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (provider.TryGet($"{IntegrationSettingKeys.ApiKeysSection}:{name}", out var key) && !string.IsNullOrWhiteSpace(key))
                    {
                        keys[name] = (key, SourceOf(provider));
                    }
                }
            }
        }

        foreach (var (name, key) in IntegrationSecrets.ReadApiKeys(_secrets))
        {
            keys[name] = (key, SettingSource.SecretStore);
        }

        return
        [
            .. keys.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase).Select(entry => new ApiKeyEntry(
                entry.Key,
                entry.Value.Key,
                entry.Value.Source,
                !(_running.ApiKeys.TryGetValue(entry.Key, out var running) && running == entry.Value.Key))),
        ];
    }

    private List<ConfiguredSetting> Describe(IReadOnlyDictionary<string, string> stored) =>
    [
        .. IntegrationSettingKeys.Plain.Select(key =>
        {
            var next = Next(key, stored);
            return new ConfiguredSetting(
                key,
                next?.Value,
                _running.Plain[key],
                next?.Source ?? SettingSource.Default,
                next?.Detail ?? string.Empty,
                Underlying(key));
        }),
    ];

    private string? Underlying(string key) => Next(key, new Dictionary<string, string>())?.Value;

    // The value the next start reads for `key`, and which provider supplies it: the last one that has
    // it wins, as in ConfigurationRoot itself. The same walk as PvSystemEditor's.
    private (string? Value, SettingSource Source, string Detail)? Next(string key, IReadOnlyDictionary<string, string> stored)
    {
        if (_configuration is null)
        {
            return null;
        }

        foreach (var provider in _configuration.Providers.Reverse())
        {
            if (provider is PvSystemOverridesProvider)
            {
                if (stored.TryGetValue(key, out var saved))
                {
                    return (saved, SettingSource.WebUi, _overrides!.Path);
                }

                continue;
            }

            if (provider is IntegrationSecretsProvider)
            {
                continue;
            }

            if (provider.TryGet(key, out var value))
            {
                var source = SourceOf(provider);
                return (value, source, source switch
                {
                    SettingSource.Environment => PvSystemSettingKeys.EnvironmentVariable(key),
                    SettingSource.CommandLine => "command line",
                    _ => provider is FileConfigurationProvider file
                        ? Path.GetFileName(file.Source.Path) ?? "a settings file"
                        : provider.ToString() ?? string.Empty,
                });
            }
        }

        return null;
    }

    private static SettingSource SourceOf(IConfigurationProvider provider) => provider switch
    {
        EnvironmentVariablesConfigurationProvider => SettingSource.Environment,
        CommandLineConfigurationProvider => SettingSource.CommandLine,
        _ => SettingSource.AppSettings,
    };

    private static ConfigurationProblems SecretNotSaved(string what) => new(
        $"The {what} secret could not be saved to the secret store. Check the data directory's permissions.",
        Phrase.Of("Config.SetupSecretNotSaved", new PhraseArg.Text(what)));

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,39}$")]
    private static partial Regex KeyName();
}
