using System.Text.Json;
using Solax.Core.Enums;
using Solax.Core.Models;
using Solax.Worker.Configuration;

namespace Solax.Worker.HomeAssistant;

/// <summary>
/// Builds the MQTT topics and payloads for the Home Assistant integration: the retained discovery
/// configs that make HA auto-create the entities, the JSON state payload, and the charge-mode state.
/// Pure — no I/O — so it can be unit-tested.
/// </summary>
public sealed class HaDiscovery
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private readonly HomeAssistantOptions _options;
    private readonly IReadOnlyDictionary<string, object?> _device;

    public HaDiscovery(HomeAssistantOptions options)
    {
        _options = options;
        _device = new Dictionary<string, object?>
        {
            ["identifiers"] = new[] { options.DeviceId },
            ["name"] = options.DeviceName,
            ["manufacturer"] = "solax-controller",
            ["model"] = "Live-solar charge control",
        };
    }

    public string AvailabilityTopic => $"{_options.BaseTopic}/{_options.DeviceId}/availability";
    public string StateTopic => $"{_options.BaseTopic}/{_options.DeviceId}/state";
    public string ModeCommandTopic => $"{_options.BaseTopic}/{_options.DeviceId}/charge_mode/set";
    public string ModeStateTopic => $"{_options.BaseTopic}/{_options.DeviceId}/charge_mode/state";

    public const string PayloadOnline = "online";
    public const string PayloadOffline = "offline";

    public string ModeState(ChargeControlMode mode) => mode.ToString();

    public static bool TryParseMode(string? payload, out ChargeControlMode mode) =>
        Enum.TryParse(payload, ignoreCase: true, out mode);

    /// <summary>
    /// Discovery config topics that older versions published but are no longer used. Publish an empty
    /// retained payload to each so Home Assistant removes the stale entity (e.g. the old switch).
    /// </summary>
    public IEnumerable<string> RetiredDiscoveryTopics()
    {
        yield return $"{_options.DiscoveryPrefix}/switch/{_options.DeviceId}/charge_control/config";
    }

    /// <summary>The retained discovery configs. Publish each on connect so HA (re)creates the entities.</summary>
    public IEnumerable<(string Topic, string Payload)> DiscoveryMessages()
    {
        yield return Config("select", "charge_mode", new Dictionary<string, object?>
        {
            ["name"] = "Charge mode",
            ["command_topic"] = ModeCommandTopic,
            ["state_topic"] = ModeStateTopic,
            ["options"] = Enum.GetNames<ChargeControlMode>(),
            ["icon"] = "mdi:ev-station",
        });

        yield return Sensor("control_state", "Control state", template: "{{ value_json.state }}", icon: "mdi:state-machine");
        yield return Sensor("charger_status", "Charger status", template: "{{ value_json.charger_status }}", icon: "mdi:ev-station");
        yield return Sensor("solar_power", "Solar power", template: "{{ value_json.solar_w }}", unit: "W", deviceClass: "power", stateClass: "measurement");
        yield return Sensor("surplus", "Solar surplus", template: "{{ value_json.surplus_w }}", unit: "W", deviceClass: "power", stateClass: "measurement");
        yield return Sensor("ev_power", "EV charging power", template: "{{ value_json.ev_power_w }}", unit: "W", deviceClass: "power", stateClass: "measurement");
        yield return Sensor("ev_current", "EV charging current", template: "{{ value_json.ev_current_a }}", unit: "A", deviceClass: "current", stateClass: "measurement");
        yield return Sensor("target_current", "Target charging current", template: "{{ value_json.target_a }}", unit: "A", deviceClass: "current");
        yield return Sensor("active_current", "Active charging current", template: "{{ value_json.active_a }}", unit: "A", deviceClass: "current");
        yield return Sensor("battery_soc", "Battery SOC", template: "{{ value_json.soc }}", unit: "%", deviceClass: "battery", stateClass: "measurement");

        yield return Config("binary_sensor", "car_connected", new Dictionary<string, object?>
        {
            ["name"] = "Car connected",
            ["state_topic"] = StateTopic,
            ["value_template"] = "{{ 'ON' if value_json.car_connected else 'OFF' }}",
            ["device_class"] = "plug",
        });

        yield return Config("binary_sensor", "holding_control", new Dictionary<string, object?>
        {
            ["name"] = "Charging now",
            ["state_topic"] = StateTopic,
            ["value_template"] = "{{ 'ON' if value_json.holding else 'OFF' }}",
            ["icon"] = "mdi:transmission-tower",
        });
    }

    /// <summary>The JSON state payload the sensors read from (via value_template). Null metrics are omitted.</summary>
    public string StateJson(ChargeControlStatus s)
    {
        var payload = new Dictionary<string, object?>
        {
            ["state"] = s.State.ToString(),
            ["mode"] = s.Mode.ToString(),
            ["charger_status"] = s.ChargerStatus.ToString(),
            ["car_connected"] = s.CarConnected,
            ["solar_w"] = Math.Round(s.SolarPowerWatts),
            ["surplus_w"] = s.SurplusWatts is null ? null : Math.Round(s.SurplusWatts.Value),
            ["ev_power_w"] = Math.Round(s.EvChargerPowerWatts),
            ["ev_current_a"] = s.EvChargingCurrentAmps,
            ["target_a"] = s.TargetCurrentAmps,
            ["active_a"] = s.ActiveCurrentAmps,
            ["soc"] = Math.Round(s.BatterySocPercent),
            ["holding"] = s.HoldingControl,
            ["dry_run"] = s.DryRun,
        };

        // Dictionary null values are serialised as JSON null regardless of the ignore condition, so
        // drop them explicitly to keep the payload clean.
        var present = payload.Where(kvp => kvp.Value is not null).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        return JsonSerializer.Serialize(present, Json);
    }

    private (string, string) Sensor(string objectId, string name, string template, string? unit = null, string? deviceClass = null, string? stateClass = null, string? icon = null)
    {
        var config = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["state_topic"] = StateTopic,
            ["value_template"] = template,
            ["unit_of_measurement"] = unit,
            ["device_class"] = deviceClass,
            ["state_class"] = stateClass,
            ["icon"] = icon,
        };
        return Config("sensor", objectId, config);
    }

    private (string Topic, string Payload) Config(string component, string objectId, Dictionary<string, object?> config)
    {
        config["unique_id"] = $"{_options.DeviceId}_{objectId}";
        config["availability_topic"] = AvailabilityTopic;
        config["payload_available"] = PayloadOnline;
        config["payload_not_available"] = PayloadOffline;
        config["device"] = _device;

        // Dictionary null values are serialised as JSON null regardless of the ignore condition, and
        // Home Assistant rejects the whole config on a null field (e.g. "icon": null). Drop them.
        var present = config.Where(kvp => kvp.Value is not null).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        var topic = $"{_options.DiscoveryPrefix}/{component}/{_options.DeviceId}/{objectId}/config";
        return (topic, JsonSerializer.Serialize(present, Json));
    }
}
