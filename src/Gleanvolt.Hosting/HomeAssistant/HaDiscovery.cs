using System.Text.Json;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Configuration;

namespace Gleanvolt.Hosting.HomeAssistant;

/// <summary>
/// Builds the MQTT topics and payloads for the Home Assistant integration: the retained discovery
/// configs that make HA auto-create the entities and the JSON state payload the sensors read from.
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
            ["manufacturer"] = "gleanvolt-controller",
            ["model"] = "Live-solar charge control",
            // Shown on the device page in HA, so "which build is on the Pi?" is answerable without
            // an ssh session. Device metadata, not an entity -- it adds no row to any dashboard.
            ["sw_version"] = BuildInfo.Describe(),
        };
    }

    public string AvailabilityTopic => $"{_options.BaseTopic}/{_options.DeviceId}/availability";
    public string StateTopic => $"{_options.BaseTopic}/{_options.DeviceId}/state";
    public string BatteryHoldCommandTopic => $"{_options.BaseTopic}/{_options.DeviceId}/battery_hold/set";
    public string BatteryHoldStateTopic => $"{_options.BaseTopic}/{_options.DeviceId}/battery_hold/state";
    public string StopServiceCommandTopic => $"{_options.BaseTopic}/{_options.DeviceId}/stop_service/set";

    /// <summary>Object ids of the settable numbers, so the worker can subscribe and publish generically.</summary>
    public const string DailyEvTargetNumber = "daily_ev_target";
    public const string SessionEnergyTargetNumber = "session_energy_target";
    public const string MinBatterySocNumber = "min_battery_soc";
    public const string ResumeMarginNumber = "resume_margin";
    public const string TargetEnergyNumber = "target_energy";

    public static readonly IReadOnlyList<string> NumberObjectIds =
        [DailyEvTargetNumber, SessionEnergyTargetNumber, MinBatterySocNumber, ResumeMarginNumber, TargetEnergyNumber];

    /// <summary>Object id of the departure-time text entity, which the worker subscribes to like a number.</summary>
    public const string TargetDepartureText = "target_departure";

    public static readonly IReadOnlyList<string> TextObjectIds = [TargetDepartureText];

    /// <summary>
    /// The strategy buttons, in the order they are published: object id -> the mode each one starts.
    /// <c>Targeted</c> is deliberately absent — it needs an amount and a departure, so its action stays
    /// on <see cref="ActivateTargetCommandTopic"/>, next to the entities that carry them.
    /// </summary>
    public static readonly IReadOnlyList<(string ObjectId, ChargeControlMode Mode, string Name, string Icon)> StartButtons =
    [
        ("start_solar", ChargeControlMode.Solar, "Charge solar", "mdi:solar-power"),
        ("start_forecasted", ChargeControlMode.Forecasted, "Charge forecasted", "mdi:weather-partly-cloudy"),
        ("start_fast_no_battery", ChargeControlMode.FastNoBattery, "Charge fast", "mdi:lightning-bolt"),
    ];

    /// <summary>
    /// The off button. Deliberately not <c>stop_service</c>, which stays what it is: the one-way "take
    /// the controller off the air" button in the config panel. This one only stops the car.
    /// </summary>
    public const string ChargeOffButton = "charge_off";

    public string ButtonCommandTopic(string objectId) => $"{_options.BaseTopic}/{_options.DeviceId}/{objectId}/set";

    public string ChargeOffCommandTopic => ButtonCommandTopic(ChargeOffButton);

    public string NumberCommandTopic(string objectId) => $"{_options.BaseTopic}/{_options.DeviceId}/{objectId}/set";
    public string NumberStateTopic(string objectId) => $"{_options.BaseTopic}/{_options.DeviceId}/{objectId}/state";

    /// <summary>Same shape as the number topics; separate methods only so call sites read as what they are.</summary>
    public string TextCommandTopic(string objectId) => NumberCommandTopic(objectId);
    public string TextStateTopic(string objectId) => NumberStateTopic(objectId);

    public string ActivateTargetCommandTopic => $"{_options.BaseTopic}/{_options.DeviceId}/activate_target/set";

    public const string PayloadOnline = "online";
    public const string PayloadOffline = "offline";
    public const string PayloadOn = "ON";
    public const string PayloadOff = "OFF";

    /// <summary>
    /// Home Assistant's MQTT payload meaning "no value": it sets the sensor's state to <c>unknown</c>
    /// rather than storing the literal string (<c>PAYLOAD_NONE</c> in HA's mqtt component).
    /// </summary>
    public const string PayloadNone = "None";

    /// <summary>
    /// The <c>value_template</c> for a metric <see cref="StateJson"/> may omit — a nullable field, or
    /// any of the plan block outside the forecast-driven mode.
    /// <para>
    /// Home Assistant renders a sensor's template on <b>every</b> state message, whether or not the
    /// entity is available, so an unguarded <c>value_json.x</c> logs a "'dict object' has no attribute
    /// 'x'" warning on every publish — once per <c>StatusInterval</c>, per missing field, which fills
    /// the log on an idle site. Jinja's <c>default</c> filter short-circuits on an undefined variable
    /// without stringifying it (so nothing is logged), and substitutes
    /// <see cref="PayloadNone"/> — which is exactly the "report nothing" these entities were always
    /// meant to show.
    /// </para>
    /// Keep using a plain <c>value_json.x</c> template for keys that are always present; the
    /// asymmetry is the documentation of which metrics are optional.
    /// </summary>
    private static string Optional(string key) => $"{{{{ value_json.{key} | default('{PayloadNone}') }}}}";

    /// <summary>
    /// What Home Assistant sends when an MQTT button is pressed. Matched exactly on every button topic,
    /// and nothing else is accepted. The rule was written for <c>stop_service</c>, which cannot be
    /// undone from Home Assistant — once the service is down it publishes nothing and listens to
    /// nothing — and it holds for the rest: a stray retained payload or a hand-typed message must not
    /// be able to start the car charging either.
    /// </summary>
    public const string PayloadPress = "PRESS";

    public static bool IsPress(string? payload) =>
        string.Equals(payload, PayloadPress, StringComparison.Ordinal);

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

        // The charge-mode select (issue #89). A mode is no longer something to pick: it is what a
        // button did, reported by the charge_mode sensor below. Retiring the config topic is what makes
        // Home Assistant delete the entity on its own, so nobody has to go and remove it by hand.
        yield return $"{_options.DiscoveryPrefix}/select/{_options.DeviceId}/charge_mode/config";

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
        // One button per strategy, plus Off (issue #89). Charging is started by an action and by
        // nothing else: each of these writes the charger's use-mode Fast and then selects its mode, so
        // a press does something on a charger sitting in Green rather than waiting for the wallbox to
        // have been set by hand.
        foreach (var (objectId, _, name, icon) in StartButtons)
        {
            yield return Button(objectId, name, icon);
        }

        yield return Button(ChargeOffButton, "Charge off", "mdi:stop-circle-outline");

        // The mode survives as state, read-only: the same string the select used to hold, from the same
        // field of the same payload.
        yield return Sensor("charge_mode", "Charge mode", template: "{{ value_json.mode }}", icon: "mdi:ev-station");
        yield return Sensor("control_state", "Control state", template: "{{ value_json.state }}", icon: "mdi:state-machine");
        yield return Sensor("charger_status", "Charger status", template: "{{ value_json.charger_status }}", icon: "mdi:ev-station");
        yield return Sensor("solar_power", "Solar power", template: "{{ value_json.solar_w }}", unit: "W", deviceClass: "power", stateClass: "measurement");
        // Deliberately next to the measured figure and published in every mode: the pair is the point,
        // and a comparison that only exists while the Forecasted mode is driving would be useless for
        // deciding whether to select it in the first place.
        yield return Sensor("forecast_power", "Forecast solar power", template: "{{ value_json.forecast_solar_w }}", unit: "W", deviceClass: "power", stateClass: "measurement");
        yield return Sensor("surplus", "Solar surplus", template: Optional("surplus_w"), unit: "W", deviceClass: "power", stateClass: "measurement");
        yield return Sensor("ev_power", "EV charging power", template: "{{ value_json.ev_power_w }}", unit: "W", deviceClass: "power", stateClass: "measurement");
        yield return Sensor("ev_current", "EV charging current", template: "{{ value_json.ev_current_a }}", unit: "A", deviceClass: "current", stateClass: "measurement");
        yield return Sensor("target_current", "Target charging current", template: Optional("target_a"), unit: "A", deviceClass: "current");
        yield return Sensor("active_current", "Active charging current", template: Optional("active_a"), unit: "A", deviceClass: "current");
        yield return Sensor("battery_soc", "Battery SOC", template: "{{ value_json.soc }}", unit: "%", deviceClass: "battery", stateClass: "measurement");
        yield return Sensor("battery_power", "Battery power", template: "{{ value_json.battery_w }}", unit: "W", deviceClass: "power", stateClass: "measurement");
        yield return Sensor("grid_power", "Grid power", template: "{{ value_json.grid_w }}", unit: "W", deviceClass: "power", stateClass: "measurement");

        if (_batteryHoldEnabled)
        {
            // The switch reports our own armed state, not a device read-back: the power-control block
            // is write-only (reading it returns the firmware version). A failed write therefore shows
            // up as the switch falling back to OFF, rather than as a success we assumed.
            yield return Config("switch", "battery_hold", new Dictionary<string, object?>
            {
                ["name"] = "Battery discharge hold",
                ["command_topic"] = BatteryHoldCommandTopic,
                ["state_topic"] = BatteryHoldStateTopic,
                ["payload_on"] = PayloadOn,
                ["payload_off"] = PayloadOff,
                ["icon"] = "mdi:battery-lock",
            });

            yield return Sensor(
                "battery_hold_target", "Battery hold target", template: Optional("hold_target_w"),
                unit: "W", deviceClass: "power");
        }

        // Forecast-driven mode (issue #22). Published unconditionally: the mode is selectable at
        // runtime, so the entities have to exist before it is picked. They simply report nothing while
        // another mode is active.
        yield return Sensor("day_outlook", "Day outlook", template: Optional("outlook"), icon: "mdi:weather-partly-cloudy");
        yield return Sensor("plan_state", "Plan state", template: Optional("plan_reason"), icon: "mdi:text-box-outline");
        yield return Sensor("charge_window", "Charge window", template: Optional("window"), icon: "mdi:clock-outline");
        yield return Sensor("ev_budget", "EV energy budget", template: Optional("ev_budget_kwh"), unit: "kWh", deviceClass: "energy");
        yield return Sensor("ev_expected_today", "EV energy expected today", template: Optional("ev_expected_kwh"), unit: "kWh", deviceClass: "energy");
        yield return Sensor("shortfall", "Projected shortfall", template: Optional("shortfall_kwh"), unit: "kWh", deviceClass: "energy");
        yield return Sensor("soc_floor", "Required SOC floor", template: Optional("soc_floor"), unit: "%", icon: "mdi:battery-arrow-down");
        // The unclamped floor next to the one in force: together they say whether the car is being held
        // back by the forecast or merely by the configured minimum, which is the first thing to look at
        // when a session pauses.
        yield return Sensor("soc_floor_traj", "Trajectory SOC floor", template: Optional("soc_floor_traj"), unit: "%", icon: "mdi:chart-line-variant");
        yield return Sensor("forecast_remaining", "Forecast remaining today", template: Optional("forecast_remaining_kwh"), unit: "kWh", deviceClass: "energy");
        yield return Sensor("tomorrow_forecast", "Tomorrow forecast", template: Optional("tomorrow_kwh"), unit: "kWh", deviceClass: "energy");
        yield return Sensor("forecast_accuracy", "Forecast accuracy", template: Optional("bias_percent"), unit: "%", icon: "mdi:chart-bell-curve");
        yield return Sensor("session_energy", "Session energy", template: "{{ value_json.session_kwh }}", unit: "kWh", deviceClass: "energy");
        yield return Sensor("loaned_today", "Battery loaned today", template: "{{ value_json.loaned_kwh }}", unit: "kWh", deviceClass: "energy");
        yield return Sensor("loan_power", "Battery loan power", template: "{{ value_json.loan_w }}", unit: "W", deviceClass: "power", stateClass: "measurement");

        yield return Number(DailyEvTargetNumber, "Daily EV target", min: 0, max: 100, step: 1, unit: "kWh", icon: "mdi:car-electric");
        yield return Number(SessionEnergyTargetNumber, "Session energy target", min: 0, max: 100, step: 1, unit: "kWh", icon: "mdi:battery-charging-80");
        yield return Number(MinBatterySocNumber, "Minimum battery SOC", min: 0, max: 100, step: 5, unit: "%", icon: "mdi:battery-arrow-down");
        yield return Number(ResumeMarginNumber, "SOC resume margin", min: 0, max: 50, step: 1, unit: "%", icon: "mdi:battery-arrow-up");

        // Targeted charging (issue #80). The mode needs no button of its own in the row above -- it
        // needs an amount, a time, and a press to apply both, which is what these three are. Published
        // unconditionally, like the forecast entities, because the mode can be started at any moment
        // and the controls have to exist before it is.
        yield return Number(TargetEnergyNumber, "Target energy", min: 0, max: 100, step: 0.5, unit: "kWh", icon: "mdi:battery-clock");

        // A text entity rather than a datetime one: MQTT discovery has no datetime platform. "07:00"
        // means the next 07:00, which is what somebody typing it at 22:00 means by it; a full
        // "2026-08-11 07:00" is there for the case where it isn't.
        yield return Text(
            TargetDepartureText,
            "Departure time",
            pattern: @"^(\d{4}-\d{2}-\d{2}[ T])?\d{1,2}:\d{2}$",
            icon: "mdi:clock-outline");

        // Pressing this is what makes the two above real: it sets the request and then starts the mode,
        // the same order and the same seam the web page uses. The object id is unchanged from #80 on
        // purpose -- existing dashboards and automations keep working across this upgrade.
        yield return Button("activate_target", "Activate target", "mdi:play-circle-outline");

        yield return Sensor("target_plan_state", "Target plan state", template: Optional("target_reason"), icon: "mdi:text-box-outline");
        // Alongside target_solar rather than instead of it: the pair is the point. A zero solar share
        // against a non-zero surplus is the weak-sun day -- sun the roof will produce that the charger
        // cannot run on unaided -- and reporting only the share describes it as a dark one.
        yield return Sensor("target_forecast_surplus", "Target forecast surplus", template: Optional("target_forecast_surplus_kwh"), unit: "kWh", deviceClass: "energy");
        yield return Sensor("target_solar", "Target solar energy", template: Optional("target_solar_kwh"), unit: "kWh", deviceClass: "energy");
        yield return Sensor("target_grid", "Target grid energy", template: Optional("target_grid_kwh"), unit: "kWh", deviceClass: "energy");
        yield return Sensor("target_expected", "Target expected", template: Optional("target_expected_kwh"), unit: "kWh", deviceClass: "energy");
        yield return Sensor("target_shortfall", "Target shortfall", template: Optional("target_shortfall_kwh"), unit: "kWh", deviceClass: "energy");
        yield return Sensor("target_pace", "Target charge pace", template: Optional("target_pace_kw"), unit: "kW", deviceClass: "power");
        yield return Sensor("target_grid_start", "Charging from", template: Optional("target_grid_start"), icon: "mdi:transmission-tower-import");

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

        // Stops the whole service, gracefully -- the charger paused, the session closed, the store
        // flushed -- and it then stays stopped until somebody starts the container again on the Pi.
        // Home Assistant cannot start it back: the service is what speaks MQTT, so pressing this is
        // the last thing this device does until an ssh session (deploy/README.md, "Stopping and
        // starting the controller").
        //
        // entity_category "config" keeps it off the auto-generated dashboard card and in the device's
        // configuration panel instead, which is the right amount of friction for a one-way control.
        // The shared availability topic does the rest of the work: within a few seconds of the press
        // the button greys out, which is how a dashboard says "it worked".
        yield return Config("button", "stop_service", new Dictionary<string, object?>
        {
            ["name"] = "Stop service",
            ["command_topic"] = StopServiceCommandTopic,
            ["payload_press"] = PayloadPress,
            ["entity_category"] = "config",
            ["icon"] = "mdi:stop-circle-outline",
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
            ["forecast_solar_w"] = Math.Round(s.ForecastSolarPowerWatts),
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
            payload["soc_floor_traj"] = Math.Round(plan.TrajectorySocFloorPercent);
            payload["forecast_remaining_kwh"] = Math.Round(plan.RemainingPvWh / 1000, 1);
            payload["bias_percent"] = Math.Round(plan.BiasFactor * 100);
        }

        // The target block follows the same rule the plan block does: present only while the mode that
        // owns it is driving, so no dashboard can show a target nothing is working towards.
        if (s.TargetedPlan is { } target)
        {
            payload["target_reason"] = target.Reason;
            payload["target_forecast_surplus_kwh"] = Math.Round(target.ForecastSurplusWh / 1000, 1);
            payload["target_solar_kwh"] = Math.Round(target.SolarEnergyWh / 1000, 1);
            payload["target_grid_kwh"] = Math.Round(target.GridEnergyWh / 1000, 1);
            payload["target_expected_kwh"] = Math.Round(target.ExpectedEnergyWh / 1000, 1);
            payload["target_shortfall_kwh"] = Math.Round(target.ShortfallWh / 1000, 1);
            payload["target_pace_kw"] = Math.Round(target.RequiredPaceWatts / 1000, 2);
            payload["target_grid_start"] = target.GridStart is { } start
                ? $"{start.LocalDateTime:HH:mm}"
                : "none";
        }

        // Dictionary null values are serialised as JSON null regardless of the ignore condition, so
        // drop them explicitly to keep the payload clean.
        var present = payload.Where(kvp => kvp.Value is not null).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        return JsonSerializer.Serialize(present, Json);
    }

    /// <summary>
    /// Parses a departure the way somebody types it: a bare <c>HH:mm</c> means the <b>next</b>
    /// occurrence of that time — which is what "07:00" typed at 22:00 means — and a full
    /// <c>yyyy-MM-dd HH:mm</c> means exactly itself. Anything else is refused, so a stray payload on
    /// the topic cannot silently become a departure.
    ///
    /// <para>Composed through <paramref name="zone"/> rather than the machine's local time, so the
    /// answer is the site's clock and a DST boundary between now and then cannot move it by an hour.</para>
    /// </summary>
    public static bool TryParseDeparture(string? payload, DateTimeOffset now, TimeZoneInfo zone, out DateTimeOffset departure)
    {
        departure = default;

        var text = payload?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var styles = System.Globalization.DateTimeStyles.None;
        var localNow = TimeZoneInfo.ConvertTime(now, zone);

        if (DateTime.TryParseExact(text, ["HH:mm", "H:mm"], culture, styles, out var timeOnly))
        {
            var candidate = localNow.Date + timeOnly.TimeOfDay;
            departure = Compose(candidate, zone);

            // Today's 07:00 is in the past at 22:00, and the request that matters is tomorrow's.
            if (departure <= now)
            {
                departure = Compose(candidate.AddDays(1), zone);
            }

            return true;
        }

        string[] stampFormats = ["yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss"];
        if (DateTime.TryParseExact(text, stampFormats, culture, styles, out var stamp))
        {
            departure = Compose(stamp, zone);
            return true;
        }

        return false;
    }

    private static DateTimeOffset Compose(DateTime local, TimeZoneInfo zone) =>
        new(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone.GetUtcOffset(local));

    // A press and nothing else: an MQTT button has no state topic, so what a dashboard shows after a
    // press is whatever the entities it acted on report next.
    private (string Topic, string Payload) Button(string objectId, string name, string icon) =>
        Config("button", objectId, new Dictionary<string, object?>
        {
            ["name"] = name,
            ["command_topic"] = ButtonCommandTopic(objectId),
            ["payload_press"] = PayloadPress,
            ["icon"] = icon,
        });

    // A free-text control. Home Assistant has no datetime platform over MQTT discovery, so the
    // departure arrives as text and is parsed here; the pattern only keeps the obvious typos out of
    // the round trip, and TryParseDeparture is what actually decides.
    private (string Topic, string Payload) Text(string objectId, string name, string pattern, string icon) =>
        Config("text", objectId, new Dictionary<string, object?>
        {
            ["name"] = name,
            ["command_topic"] = TextCommandTopic(objectId),
            ["state_topic"] = TextStateTopic(objectId),
            ["pattern"] = pattern,
            ["max"] = 16,
            ["icon"] = icon,
        });

    // A settable number. Unlike the sensors it has its own state topic rather than reading the shared
    // JSON payload, so Home Assistant's optimistic update and our echo agree on one value.
    private (string Topic, string Payload) Number(
        string objectId, string name, double min, double max, double step, string unit, string icon) =>
        Config("number", objectId, new Dictionary<string, object?>
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
        });

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
