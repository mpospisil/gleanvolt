using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Configuration;

namespace Gleanvolt.Hosting.Vehicles;

/// <summary>
/// The built-in car feeds are gone (#477): volkswagen.de, MyŠkoda and the VW Group Data Act portal are car plugins
/// from gleanvolt-cars, read through <c>Vehicle:External</c>. <see cref="Weather.WeatherSourceSelection"/>'s arrangement for
/// the built-in OpenWeather (#425), applied to the car.
///
/// <para><b>At the first start that finds a built-in enabled</b> (<c>Vehicle:Website:Enabled</c>,
/// <c>Vehicle:Skoda:Enabled</c>, <c>Vehicle:DataAct:Enabled</c> or <c>VW_ENABLED</c>), <c>Vehicle:External:Enabled</c>
/// is written to the overrides file, with <see cref="MovedKey"/> naming the plugin, so this happens once and a feed
/// the owner picks afterwards is not undone by a <c>.env</c> that still says so. <see cref="NoticeKey"/> keeps the
/// Car page naming the plugin to run, across restarts, until a car plugin connects. One warning in the log.</para>
///
/// <para><b>At every start</b> the built-ins' secrets are deleted from the secret store, whatever feed is chosen, and
/// not handed over: the owner enters the password or key on the plugin's page. The old keys are ignored, not refused;
/// one warning a start names the flags still on, since compose goes on handing their non-secret values to the
/// plugins and the deploy script reads the flags to run the plugin.</para>
/// </summary>
public sealed class RetiredCarFeed : IRetiredCarFeed
{
    /// <summary>Set when a built-in was moved to a plugin: the plugin's id. Never removed, so it happens once.</summary>
    public const string MovedKey = "Vehicle:RetiredFeed";

    /// <summary>Set with <see cref="MovedKey"/> and removed once a car plugin connects: what keeps the Car page's notice.</summary>
    public const string NoticeKey = "Vehicle:RetiredFeedNotice";

    /// <summary>The removed built-ins' sections, each with the plugin that replaces it and its .env flag.</summary>
    public static IReadOnlyList<(string Section, CarFeedPlugin Plugin, string Flag)> Retired { get; } =
    [
        ("Vehicle:Website", CarFeedPlugin.Volkswagen, "VW_WEBSITE_ENABLED"),
        ("Vehicle:Skoda", CarFeedPlugin.Skoda, "SKODA_ENABLED"),
        ("Vehicle:DataAct", CarFeedPlugin.VwGroup, "VW_ENABLED"),
    ];

    // The car's own MQTT topic, removed in #486: its switch and the topic.
    private const string MqttEnabledKey = "Vehicle:Enabled";
    private const string MqttTopicKey = "Ev:Vehicles:0:Telemetry:Topic";

    // The Data Act portal's own fallback: VW_ENABLED read straight from the environment, as the built-in did.
    private const string DataActFallback = "VW_ENABLED";

    private readonly PvSystemOverridesProvider? _overrides;
    private readonly ILogger<RetiredCarFeed> _logger;
    private readonly Lock _gate = new();
    private CarFeedPlugin? _notice;

    public RetiredCarFeed(IConfiguration configuration, ISecretStore secrets, ILogger<RetiredCarFeed> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(secrets);

        _overrides = PvSystemOverrides.Find(configuration);
        _logger = logger;

        if (Moving(configuration) is { } plugin)
        {
            MoveToPlugin(plugin);
        }
        else if (Flag(configuration[NoticeKey]))
        {
            _notice = CarFeedPlugin.Find(configuration[MovedKey]);
        }

        foreach (var name in SecretNames.RetiredVehicleSecrets)
        {
            if (string.IsNullOrEmpty(secrets.Read(name)))
            {
                continue;
            }

            _logger.LogInformation(secrets.Delete(name)
                ? "The built-in car feed's {Secret} was deleted from the secret store: Gleanvolt no longer reads the car itself. "
                  + "Enter the password or key on the car plugin's page."
                : "The built-in car feed's {Secret} could not be deleted from the secret store; it is no longer read.", name);
        }

        // #486: the car's own MQTT topic is not read any more. An installation that chose it is charge-only now. What
        // the Car page saved for it is taken out of the overrides file, which nothing can edit it in any more.
        var removedTopic = false;
        if (_overrides is not null && Saved(_overrides.Path, MqttEnabledKey, MqttTopicKey))
        {
            try
            {
                Store(stored =>
                {
                    stored.Remove(MqttEnabledKey);
                    stored.Remove(MqttTopicKey);
                });
                removedTopic = true;
                _logger.LogWarning(
                    "Gleanvolt no longer reads the car over MQTT (#486): the topic saved on the Car page was removed from {Path}. "
                    + "The car is not read unless a car plugin does it, with 'A car plugin' chosen on the Car page.", _overrides.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger.LogWarning("Could not remove the car's MQTT topic from {Path} ({Reason}); it is ignored.", _overrides.Path, ex.Message);
            }
        }

        if (!removedTopic && Flag(configuration[MqttEnabledKey]))
        {
            _logger.LogWarning(
                "Gleanvolt no longer reads the car over MQTT (#486): {Enabled} (VEHICLE_ENABLED) and the car's topic are "
                + "ignored, and the car is not read unless a car plugin does it, with 'A car plugin' chosen on the Car page. "
                + "Remove VEHICLE_ENABLED from .env; this warning repeats at every start until then.",
                MqttEnabledKey);
        }

        if (StillSet(configuration) is { Count: > 0 } left)
        {
            _logger.LogWarning(
                "{Keys} {Verb} set, and Gleanvolt reads no car itself any more (#477): the car is read by a car plugin, with "
                + "'A car plugin' chosen on the Car page. Compose hands the built-in's VIN and username to the plugin; once the "
                + "plugin's page has its account, name the plugin in GLEANVOLT_PLUGINS and remove these from .env. This "
                + "warning repeats at every start until then.",
                string.Join(", ", left), left.Count == 1 ? "is" : "are");
        }
    }

    public CarFeedPlugin? Notice
    {
        get
        {
            lock (_gate)
            {
                return _notice;
            }
        }
    }

    public event Action? Changed;

    /// <summary>
    /// The plugin a start moves this installation to, or null when it moves nothing: no built-in is enabled, or one
    /// was moved before. Read at registration too, so the first start after the upgrade runs the plugin's feed.
    /// </summary>
    public static CarFeedPlugin? Moving(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return string.IsNullOrWhiteSpace(configuration[MovedKey]) ? Enabled(configuration) : null;
    }

    /// <summary>The plugin that replaces the built-in <paramref name="configuration"/> enables, in the order one was preferred.</summary>
    public static CarFeedPlugin? Enabled(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        foreach (var (section, plugin, _) in Retired)
        {
            if (Flag(configuration[section + ":Enabled"]) || (plugin == CarFeedPlugin.VwGroup && Flag(configuration[DataActFallback])))
            {
                return plugin;
            }
        }

        return null;
    }

    /// <summary>
    /// A car plugin connected: the notice has done its job. Removed from the overrides file too, so the next start
    /// doesn't bring it back. Nothing when there is no notice.
    /// </summary>
    public void ProviderConnected()
    {
        lock (_gate)
        {
            if (_notice is null)
            {
                return;
            }

            _notice = null;
        }

        if (_overrides is not null)
        {
            try
            {
                Store(stored => stored.Remove(NoticeKey));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger.LogWarning("Could not clear {Key} in {Path} ({Reason}); the notice about the built-in car feed comes back at the next start.",
                    NoticeKey, _overrides.Path, ex.Message);
            }
        }

        _logger.LogInformation("A car plugin connected; the notice about the built-in car feed is gone.");
        Changed?.Invoke();
    }

    private void MoveToPlugin(CarFeedPlugin plugin)
    {
        _notice = plugin;

        string saved;
        if (_overrides is null)
        {
            saved = "for this run";
        }
        else
        {
            try
            {
                Store(stored =>
                {
                    stored[EvSettingKeys.ExternalEnabled] = "true";
                    stored[MovedKey] = plugin.Id;
                    stored[NoticeKey] = "true";
                });
                saved = $"saved in {_overrides.Path}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                saved = $"for this run only, since {_overrides.Path} could not be written ({ex.Message})";
            }
        }

        _logger.LogWarning(
            "The built-in car feeds are gone (#477), and this installation used one; the car is now read by a car plugin "
            + "(Vehicle:External), {Saved}. Run the {Plugin} plugin from gleanvolt-cars ({Id} in GLEANVOLT_PLUGINS, or its "
            + "image or release zip), accept it under Settings -> Plugins, then enter the {Secret} on its page. Until then "
            + "the car is not read, and targets are in kWh.",
            saved, plugin.Name, plugin.Id, plugin.Secret == CarFeedSecret.ApiKey ? "API key" : "password");
    }

    // Each built-in still switched on, by the name an owner finds in .env.
    private static List<string> StillSet(IConfiguration configuration)
    {
        var found = Retired
            .Where(retired => Flag(configuration[retired.Section + ":Enabled"]))
            .Select(retired => retired.Flag)
            .ToList();

        if (Flag(configuration[DataActFallback]) && !found.Contains(DataActFallback))
        {
            found.Add(DataActFallback);
        }

        return found;
    }

    private void Store(Action<Dictionary<string, string>> change)
    {
        var path = _overrides!.Path;

        using (PvSystemOverrides.Lock(path))
        {
            var stored = PvSystemOverrides.Read(path);
            change(stored);
            PvSystemOverrides.Write(path, stored);
        }
    }

    // Whether the overrides file holds any of the keys. A missing or unreadable file holds none.
    private static bool Saved(string path, params string[] keys)
    {
        try
        {
            var stored = PvSystemOverrides.Read(path);
            return keys.Any(stored.ContainsKey);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool Flag(string? value) => bool.TryParse(value, out var parsed) && parsed;
}
