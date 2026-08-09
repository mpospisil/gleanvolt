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
    private readonly bool _batteryHoldEnabled;
    private readonly IReadOnlyDictionary<string, object?> _device;

    /// <param name="batteryHoldEnabled">
    /// Whether <c>BatteryHold:Enabled</c> is on. When it is off the feature is inert, so the switch is
    /// not published at all rather than published as a control that would do nothing.
    /// </param>
    public HaDiscovery(HomeAssistantOptions options, bool batteryHoldEnabled = false)
    {
        _options = options;
        _batteryHoldEnabled = batteryHoldEnabled;
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
    public string BatteryHoldCommandTopic => $"{_options.BaseTopic}/{_options.DeviceId}/battery_hold/set";
    public string BatteryHoldStateTopic => $"{_options.BaseTopic}/{_options.DeviceId}/battery_hold/state";

    /// <summary>Object ids of the settable numbers, so the worker can subscribe and publish generically.</summary>
    public const string DailyEvTargetNumber = "daily_ev_target";
    public const string SessionEnergyTargetNumber = "session_energy_target";
    public const string MinBatterySocNumber = "min_battery_soc";

    public static readonly IReadOnlyList<string> NumberObjectIds =
        [DailyEvTargetNumber, SessionEnergyTargetNumber, MinBatterySocNumber];

    public string NumberCommandTopic(string objectId) => $"{_options.BaseTopic}/{_options.DeviceId}/{objectId}/set";
    public string NumberStateTopic(string objectId) => $"{_options.BaseTopic}/{_options.DeviceId}/{objectId}/state";

    public const string PayloadOnline = "online";
    public const string PayloadOffline = "offline";
    public const string PayloadOn = "ON";
    public const string PayloadOff = "OFF";

    public string ModeState(ChargeControlMode mode) => mode.ToString();

    public static bool TryParseMode(string? payload, out ChargeControlMode mode) =>
        Enum.TryParse(payload, ignoreCase: true, out mode);

    public static string SwitchState(bool on) => on ? PayloadOn : PayloadOff;

    public static bool TryParseSwitch(string? payload, out bool on)
    {
        on = string.Equals(payload, PayloadOn, StringComparison.OrdinalIgnoreCase);
        return on || string.Equals(payload, PayloadOff, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Discovery config topics that older versions published but are no longer used. Publish an empty
    /// retained payload to each so Home Assistant removes the stale entity (e.g. the old switch).
    /// </summary>
    public IEnumerable<string> RetiredDiscoveryTopics()
    {
        yield return $"{_options.DiscoveryPrefix}/switch/{_options.DeviceId}/charge_control/config";

        // Also retire the battery-hold entities while the feature is off, so turning it off actually
        // removes the switch from HA rather than leaving a retained config behind that does nothing.
        if (!_batteryHoldEnabled)
        {
            yield return $"{_options.DiscoveryPrefix}/switch/{_options.DeviceId}/battery_hold/config";
            yield return $"{_options.DiscoveryPrefix}/sensor/{_options.DeviceId}/battery_hold_target/config";
        }
    }

    /// <summary>The retained discovery configs. Publish each on connect so HA (re)creates the entities.</summary>
    public IEnumerable<(string Topic, string Payload)> DiscoveryMessages()
    {
        var mode = new Dictionary<string, object?>
        {
            ["name"] = "Charge mode — which strategy drives the charger; always starts at Off after a restart",
            ["command_topic"] = ModeCommandTopic,
            ["state_topic"] = ModeStateTopic,
            ["options"] = Enum.GetNames<ChargeControlMode>(),
            ["icon"] = "mdi:ev-station",
        };
        Describe(mode,
            "Which strategy drives the charger. Off leaves its setpoint exactly as its owner set it. "
            + "Solar follows the live surplus once the home battery is full. Forecasted follows the Solcast "
            + "day plan, so the car can start before the battery is full while the battery still reaches "
            + "100% by evening. FastNoBattery charges at the maximum configured current from PV and grid "
            + "with the battery held out of it, and returns itself to Off when the car is full. "
            + "Always starts at Off after a restart.");
        yield return Config("select", "charge_mode", mode);

        yield return Sensor("control_state",
            "Control state — what charge control is doing now: Disabled, Idle, Charging or Paused",
            template: "{{ value_json.state }}",
            description:
            "What charge control is doing right now. Disabled: no mode selected, the charger is the owner's. "
            + "Idle: a mode is selected but it is not acting, most often because the charger's own use-mode is "
            + "not Fast. Charging: a current is being commanded. Paused: the setpoint was dropped to the pause "
            + "current, typically because the surplus fell below what the charger's 6 A floor needs.",
            icon: "mdi:state-machine");

        yield return Sensor("charger_status",
            "Charger status — the charger's own state; SuspendedEv means the car stopped the draw, ChargePaused means we did",
            template: "{{ value_json.charger_status }}",
            description:
            "The charger's own state, read straight from its register. Available: no car. Preparing: plugged in, "
            + "not yet drawing. Charging. SuspendedEv: the car stopped the draw, typically because it reached its "
            + "own charge limit. SuspendedEvse / ChargePaused: the charger stopped it - which is what our pause "
            + "write produces. Finishing: session closing. Faulted / Unavailable: not usable.",
            icon: "mdi:ev-station");

        yield return Sensor("solar_power",
            "Solar power — PV production at the inverter, before the house, battery or car take any",
            template: "{{ value_json.solar_w }}",
            description: "PV production measured at the inverter, before the house, the battery or the car take any of it.",
            unit: "W", deviceClass: "power", stateClass: "measurement");

        yield return Sensor("surplus",
            "Solar surplus — PV left after the house, averaged over 3 minutes; what the car can take without importing or discharging",
            template: "{{ value_json.surplus_w }}",
            description:
            "PV left after household consumption, counting neither battery charging nor EV charging as house load. "
            + "This is the power the car can take without importing from the grid or discharging the battery. It is "
            + "the smoothed 3-minute average the solar modes actually decide on, not the instantaneous value, so a "
            + "passing cloud cannot interrupt a session. Blank when the selected mode does not decide on surplus.",
            unit: "W", deviceClass: "power", stateClass: "measurement");

        yield return Sensor("ev_power",
            "EV charging power — what the charger reports the car actually drawing right now",
            template: "{{ value_json.ev_power_w }}",
            description: "Power the EV charger reports drawing right now - the car's actual consumption, not a setpoint.",
            unit: "W", deviceClass: "power", stateClass: "measurement");

        yield return Sensor("ev_current",
            "EV charging current — measured from the charger's power; what the car really takes, not what was asked for",
            template: "{{ value_json.ev_current_a }}",
            description:
            "Charging current derived from the charger's measured power, phase-aware: what the car is really taking. "
            + "Compare with Target charging current (what was decided) and Active charging current (what the charger "
            + "was set to) - a car may draw less than the setpoint allows, but never more.",
            unit: "A", deviceClass: "current", stateClass: "measurement");

        yield return Sensor("target_current",
            "Target charging current — what the controller decided this cycle; blank when it isn't charging",
            template: "{{ value_json.target_a }}",
            description:
            "The current the controller decided on this cycle, before it was written. Blank whenever it is not "
            + "charging - paused, idle, or in Off.",
            unit: "A", deviceClass: "current");

        yield return Sensor("active_current",
            "Active charging current — the charger's setpoint read back; compare with the target to see whether writes land",
            template: "{{ value_json.active_a }}",
            description:
            "The charger's current setpoint as read back from the hardware: what it was actually left at. This is the "
            + "one to compare against the target - if they disagree for more than a poll or two, a write is not "
            + "landing (or the controller is in dry run).",
            unit: "A", deviceClass: "current");

        yield return Sensor("battery_soc",
            "Battery SOC — home battery charge; 0-100% spans the usable capacity, not the nameplate",
            template: "{{ value_json.soc }}",
            description:
            "Home battery state of charge as the inverter reports it. 0-100% spans the capacity the pack will "
            + "actually cycle - its usable energy, not its nameplate.",
            unit: "%", deviceClass: "battery", stateClass: "measurement");

        yield return Sensor("battery_power",
            "Battery power — positive while the battery charges, negative while it discharges",
            template: "{{ value_json.battery_w }}",
            description:
            "Home battery power. Positive while the battery is charging, negative while it is discharging. With the "
            + "discharge hold armed this should sit at or above roughly -60 W: a working hold still leaves a small "
            + "standby trickle, but the pack is no longer serving the house.",
            unit: "W", deviceClass: "power", stateClass: "measurement");

        yield return Sensor("grid_power",
            "Grid power — positive while importing from the grid, negative while exporting",
            template: "{{ value_json.grid_w }}",
            description:
            "Grid meter power. Positive while importing from the grid, negative while exporting. Note this is the "
            + "opposite of the sign the SolaX register itself uses; it is negated on read so that positive always "
            + "means power flowing into the house.",
            unit: "W", deviceClass: "power", stateClass: "measurement");

        if (_batteryHoldEnabled)
        {
            // The switch reports our own armed state, not a device read-back: the power-control block
            // is write-only (reading it returns the firmware version). A failed write therefore shows
            // up as the switch falling back to OFF, rather than as a success we assumed.
            var hold = new Dictionary<string, object?>
            {
                ["name"] = "Battery discharge hold — stops the battery serving the house; shows the last command written, not a read-back",
                ["command_topic"] = BatteryHoldCommandTopic,
                ["state_topic"] = BatteryHoldStateTopic,
                ["payload_on"] = PayloadOn,
                ["payload_off"] = PayloadOff,
                ["icon"] = "mdi:battery-lock",
            };
            Describe(hold,
                "Stops the home battery serving household load, so the car charges from PV and grid while the "
                + "battery itself can still charge from surplus. Shows the last command the controller successfully "
                + "wrote, not a reading from the inverter - the command register cannot be read back, so a failed "
                + "write shows up as this switch springing back to OFF. The command expires on its own if the "
                + "service stops, which is the failsafe. The FastNoBattery mode arms it automatically; this switch "
                + "is the manual one, and a mode never turns it off for you.");
            yield return Config("switch", "battery_hold", hold);

            yield return Sensor(
                "battery_hold_target", "Battery hold target — the grid-point power commanded to keep the battery out of house load", template: "{{ value_json.hold_target_w }}",
                description:
                "The power target currently commanded at the inverter's grid connection point to keep the battery "
                + "out of the house load: minus whichever is smaller, house load or PV. Blank when no hold is armed.",
                unit: "W", deviceClass: "power");
        }

        // Forecast-driven mode (issue #22). Published unconditionally: the mode is selectable at
        // runtime, so the entities have to exist before it is picked. They simply report nothing while
        // another mode is active.
        yield return Sensor("day_outlook",
            "Day outlook — whether today's sun covers the house, a full battery and the car's target",
            template: "{{ value_json.outlook }}",
            description:
            "How the rest of today looks for the car, judged from the forecast. Surplus: the day covers the house, "
            + "the battery to 100% and the whole EV target. Tight: the car gets something, but less than its target. "
            + "Shortfall: substantially less - the battery keeps priority. NoChargeToday: no window in which the car "
            + "could charge at all. Unknown: no usable forecast. This is what a not-enough-sun notification keys off.",
            icon: "mdi:weather-partly-cloudy");

        yield return Sensor("plan_state",
            "Plan state — one line on why the forecast plan says what it says",
            template: "{{ value_json.plan_reason }}",
            description: "One line on why the day plan says what it says - the same explanation the log carries.",
            icon: "mdi:text-box-outline");

        yield return Sensor("charge_window",
            "Charge window — the next stretch today when the surplus clears the charger's minimum",
            template: "{{ value_json.window }}",
            description:
            "The next stretch of today in which the surplus is forecast to clear the charger's minimum power for long "
            + "enough to be worth starting - the only time the car can charge at all. none when today offers no such "
            + "window.",
            icon: "mdi:clock-outline");

        yield return Sensor("ev_budget",
            "EV energy budget — how much of today's remaining sun the car may have, house and battery served first",
            template: "{{ value_json.ev_budget_kwh }}",
            description:
            "How much of today's remaining sun the car may have: what is left once the house and a 100% battery by "
            + "evening are served, then restricted to the periods where the surplus actually clears the charger's "
            + "minimum power. The restriction matters because the car cannot sip a budget slowly.",
            unit: "kWh", deviceClass: "energy");

        yield return Sensor("ev_expected_today",
            "EV energy expected today — what the car can realistically receive in total today",
            template: "{{ value_json.ev_expected_kwh }}",
            description: "What the car can realistically expect to receive in total today, including what it has already taken.",
            unit: "kWh", deviceClass: "energy");

        yield return Sensor("shortfall",
            "Projected shortfall — how far today falls short of house, a full battery and the EV target",
            template: "{{ value_json.shortfall_kwh }}",
            description:
            "How far today's forecast falls short of the house plus a full battery plus the daily EV target. Above "
            + "zero means the car will not get everything it wanted; the battery keeps priority regardless.",
            unit: "kWh", deviceClass: "energy");

        yield return Sensor("soc_floor",
            "Required SOC floor — the SOC the battery must stay above to still reach 100% by evening",
            template: "{{ value_json.soc_floor }}",
            description:
            "The SOC the home battery must not fall below right now if the sun still to come is to return it to 100% "
            + "by the evening deadline. It climbs towards 100% as the day runs out, which is what squeezes the car "
            + "out of the late afternoon without any scheduling. Battery SOC dropping to this line is what arms the "
            + "discharge hold automatically in the Forecasted mode.",
            unit: "%", icon: "mdi:battery-arrow-down");

        yield return Sensor("forecast_remaining",
            "Forecast remaining today — PV still to come, at the configured confidence and scaled by bias",
            template: "{{ value_json.forecast_remaining_kwh }}",
            description:
            "Forecast PV production still to come today, at the configured confidence band and already scaled by the "
            + "bias below.",
            unit: "kWh", deviceClass: "energy");

        yield return Sensor("tomorrow_forecast",
            "Tomorrow forecast — tomorrow's production, context for whether to wait out a shortfall",
            template: "{{ value_json.tomorrow_kwh }}",
            description:
            "Tomorrow's forecast production. Purely informational: context for whether a shortfall today is worth "
            + "waiting out.",
            unit: "kWh", deviceClass: "energy");

        yield return Sensor("forecast_accuracy",
            "Forecast accuracy — actual against forecast so far today; 100% is on the nose",
            template: "{{ value_json.bias_percent }}",
            description:
            "Actual production against forecast so far today. 100% is on the nose, above means the roof is beating "
            + "the forecast. The rest of the day's forecast is scaled by this, and a sustained large miss makes the "
            + "plan untrusted so the mode falls back to live-solar behaviour.",
            unit: "%", icon: "mdi:chart-bell-curve");

        yield return Sensor("session_energy",
            "Session energy — delivered to the car since it was plugged in; resets when unplugged",
            template: "{{ value_json.session_kwh }}",
            description: "Energy delivered to the car since it was plugged in. Starts afresh when it is unplugged.",
            unit: "kWh", deviceClass: "energy");

        yield return Sensor("loaned_today",
            "Battery loaned today — energy the home battery lent the car, repaid from later sun",
            template: "{{ value_json.loaned_kwh }}",
            description:
            "Energy the home battery has lent the car today so that a surplus below the charger's floor could still "
            + "reach it. The loan is repaid from sun that would otherwise have been exported, and the forecast is "
            + "what makes it safe. Resets at local midnight.",
            unit: "kWh", deviceClass: "energy");

        yield return Sensor("loan_power",
            "Battery loan power — how much of the car's draw the battery is covering right now",
            template: "{{ value_json.loan_w }}",
            description:
            "How much of what the car is drawing right now is being covered by the home battery rather than by live "
            + "sun. Zero outside the Forecasted mode.",
            unit: "W", deviceClass: "power", stateClass: "measurement");

        yield return Number(DailyEvTargetNumber,
            "Daily EV target — how much the car should get on a normal day; the shortfall is measured against it",
            description:
            "How much energy the car should get on a normal day. The forecast plan measures its projected shortfall "
            + "against this. Does not persist across restarts.",
            min: 0, max: 100, step: 1, unit: "kWh", icon: "mdi:car-electric");

        yield return Number(SessionEnergyTargetNumber,
            "Session energy target — stop after this much in one session; 0 means no limit",
            description:
            "Stop charging once this much has gone into the car in a single session (since it was plugged in). "
            + "0 means no limit. Does not persist across restarts.",
            min: 0, max: 100, step: 1, unit: "kWh", icon: "mdi:battery-charging-80");

        yield return Number(MinBatterySocNumber,
            "Minimum battery SOC — the hard floor the forecast plan may never take the battery below",
            description:
            "The hard floor the forecast plan may never take the home battery below, however good the forecast "
            + "looks. Does not persist across restarts.",
            min: 0, max: 100, step: 5, unit: "%", icon: "mdi:battery-arrow-down");

        var carConnected = new Dictionary<string, object?>
        {
            ["name"] = "Car connected — a vehicle is plugged in; says nothing about whether it is drawing",
            ["state_topic"] = StateTopic,
            ["value_template"] = "{{ 'ON' if value_json.car_connected else 'OFF' }}",
            ["device_class"] = "plug",
        };
        Describe(carConnected,
            "ON while a vehicle is plugged in - the charger reporting Preparing, Charging, Suspended, ChargePaused "
            + "or Finishing. It says nothing about whether the car is drawing power.");
        yield return Config("binary_sensor", "car_connected", carConnected);

        var driving = new Dictionary<string, object?>
        {
            ["name"] = "Controller charging the car — we are commanding a current; not the car's actual draw",
            ["state_topic"] = StateTopic,
            ["value_template"] = "{{ 'ON' if value_json.holding else 'OFF' }}",
            ["icon"] = "mdi:transmission-tower",
        };
        Describe(driving,
            "ON while the controller is commanding a charging current, as opposed to having paused or never taken "
            + "control. This reflects our own decision, not the car's behaviour - a car can be plugged in and idle "
            + "while this is ON. See EV charging power for what is actually flowing.");
        yield return Config("binary_sensor", "holding_control", driving);
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
            ["battery_w"] = Math.Round(s.BatteryPowerWatts),
            // Positive = importing, negative = exporting (this project's convention; the SolaX meter
            // register itself uses the opposite sign and is negated on read).
            ["grid_w"] = Math.Round(s.GridPowerWatts),
            ["holding"] = s.HoldingControl,
            ["dry_run"] = s.DryRun,
            ["hold_target_w"] = s.BatteryHoldTargetWatts is null ? null : Math.Round(s.BatteryHoldTargetWatts.Value),
            ["loan_w"] = Math.Round(s.LoanPowerWatts),
            ["session_kwh"] = Math.Round(s.SessionEnergyWh / 1000, 2),
            ["loaned_kwh"] = Math.Round(s.LoanedTodayWh / 1000, 2),
            ["tomorrow_kwh"] = s.TomorrowForecastWh is null ? null : Math.Round(s.TomorrowForecastWh.Value / 1000, 1),
        };

        // The plan block is present only in the forecast-driven mode; in the others the entities go
        // unavailable rather than reporting stale numbers from a plan nothing is acting on.
        if (s.Plan is { } plan)
        {
            payload["outlook"] = plan.Outlook.ToString();
            payload["plan_reason"] = plan.Reason;
            payload["window"] = plan.NextFeasibleWindow is { } window
                ? $"{window.Start.LocalDateTime:HH:mm}-{window.End.LocalDateTime:HH:mm}"
                : "none";
            payload["ev_budget_kwh"] = Math.Round(plan.FeasibleEvEnergyWh / 1000, 1);
            payload["ev_expected_kwh"] = Math.Round(plan.EvExpectedTodayWh / 1000, 1);
            payload["shortfall_kwh"] = Math.Round(plan.ShortfallWh / 1000, 1);
            payload["soc_floor"] = Math.Round(plan.RequiredSocFloorPercent);
            payload["forecast_remaining_kwh"] = Math.Round(plan.RemainingPvWh / 1000, 1);
            payload["bias_percent"] = Math.Round(plan.BiasFactor * 100);
        }

        // Dictionary null values are serialised as JSON null regardless of the ignore condition, so
        // drop them explicitly to keep the payload clean.
        var present = payload.Where(kvp => kvp.Value is not null).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        return JsonSerializer.Serialize(present, Json);
    }

    // A settable number. Unlike the sensors it has its own state topic rather than reading the shared
    // JSON payload, so Home Assistant's optimistic update and our echo agree on one value.
    private (string Topic, string Payload) Number(
        string objectId, string name, string description, double min, double max, double step, string unit, string icon)
    {
        var config = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["command_topic"] = NumberCommandTopic(objectId),
            ["state_topic"] = NumberStateTopic(objectId),
            ["min"] = min,
            ["max"] = max,
            ["step"] = step,
            ["unit_of_measurement"] = unit,
            ["mode"] = "box",
            ["icon"] = icon,
        };
        Describe(config, description);
        return Config("number", objectId, config);
    }

    private (string, string) Sensor(string objectId, string name, string template, string description, string? unit = null, string? deviceClass = null, string? stateClass = null, string? icon = null)
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
        Describe(config, description);
        return Config("sensor", objectId, config);
    }

    /// <summary>
    /// Attaches the entity's description. Home Assistant has no description or tooltip field — hovering
    /// an entity shows its friendly name and nothing else — so the explanation is published as an
    /// attribute instead, where it shows up under Attributes in the entity's more-info dialog.
    ///
    /// <para>The template is a constant: it carries no placeholders, so every state message renders the
    /// same JSON. It is built with the serialiser rather than by hand so anything in the text that
    /// would break the JSON is escaped. Keep descriptions free of Jinja delimiters.</para>
    /// </summary>
    private void Describe(Dictionary<string, object?> config, string description)
    {
        config["json_attributes_topic"] = StateTopic;
        config["json_attributes_template"] = JsonSerializer.Serialize(
            new Dictionary<string, string> { ["description"] = description }, Json);
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
