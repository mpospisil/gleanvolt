using System.Text.Json;
using Solax.Core.Enums;
using Solax.Core.Models;
using Solax.Worker.Configuration;
using Solax.Worker.HomeAssistant;

namespace Solax.Worker.Tests;

public class HaDiscoveryTests
{
    private static readonly HomeAssistantOptions Options = new()
    {
        BaseTopic = "solax",
        DeviceId = "solax_controller",
        DiscoveryPrefix = "homeassistant",
    };

    private static readonly HaDiscovery Discovery = new(Options);

    private static readonly HaDiscovery DiscoveryWithBatteryHold = new(Options, batteryHoldEnabled: true);

    private static ChargeControlStatus Status(
        ChargeControlMode mode = ChargeControlMode.Solar,
        ChargeControlState state = ChargeControlState.Charging,
        double? surplus = 4180.7,
        int? target = 6,
        int? active = 16,
        bool holdActive = true,
        double? holdTarget = -2450.4,
        SolarDayPlan? plan = null,
        double loanWatts = 0) =>
        new(mode, DryRun: true, HoldingControl: true, state, surplus, target, active, BatterySocPercent: 98.6,
            ChargerStatus: EvChargerStatus.Charging, CarConnected: true, SolarPowerWatts: 7010.4,
            EvChargerPowerWatts: 10784.9, EvChargingCurrentAmps: 16, BatteryPowerWatts: -1250.2,
            GridPowerWatts: 1601.4,
            BatteryHoldEnabled: true, BatteryHoldRequested: true, BatteryHoldActive: holdActive,
            BatteryHoldTargetWatts: holdTarget, Plan: plan, LoanPowerWatts: loanWatts, SessionEnergyWh: 8400,
            LoanedTodayWh: 1800, TomorrowForecastWh: 24500, Timestamp: DateTimeOffset.UtcNow);

    [Fact]
    public void Topics_FollowTheConfiguredPrefixes()
    {
        Assert.Equal("solax/solax_controller/availability", Discovery.AvailabilityTopic);
        Assert.Equal("solax/solax_controller/state", Discovery.StateTopic);
        Assert.Equal("solax/solax_controller/charge_mode/set", Discovery.ModeCommandTopic);
        Assert.Equal("solax/solax_controller/charge_mode/state", Discovery.ModeStateTopic);
    }

    [Fact]
    public void DiscoveryMessages_IncludeAModeSelectWithEveryMode()
    {
        var messages = Discovery.DiscoveryMessages().ToList();

        var selectMsg = messages.Single(m => m.Topic == "homeassistant/select/solax_controller/charge_mode/config");
        using var json = JsonDocument.Parse(selectMsg.Payload);
        var s = json.RootElement;

        Assert.Equal("solax_controller_charge_mode", s.GetProperty("unique_id").GetString());
        Assert.Equal(Discovery.ModeCommandTopic, s.GetProperty("command_topic").GetString());
        Assert.Equal(Discovery.ModeStateTopic, s.GetProperty("state_topic").GetString());
        var options = s.GetProperty("options").EnumerateArray().Select(o => o.GetString()).ToArray();
        Assert.Equal(["Off", "Solar", "Forecasted", "FastNoBattery"], options);

        Assert.Contains(messages, m => m.Topic == "homeassistant/sensor/solax_controller/control_state/config");
        Assert.Contains(messages, m => m.Topic == "homeassistant/binary_sensor/solax_controller/holding_control/config");
        Assert.DoesNotContain(messages, m => m.Topic.Contains("/switch/"));
    }

    [Theory]
    [InlineData("homeassistant/sensor/solax_controller/charger_status/config")]
    [InlineData("homeassistant/sensor/solax_controller/solar_power/config")]
    [InlineData("homeassistant/sensor/solax_controller/ev_power/config")]
    [InlineData("homeassistant/sensor/solax_controller/ev_current/config")]
    [InlineData("homeassistant/binary_sensor/solax_controller/car_connected/config")]
    [InlineData("homeassistant/sensor/solax_controller/grid_power/config")]
    [InlineData("homeassistant/sensor/solax_controller/battery_power/config")]
    public void DiscoveryMessages_IncludeTheTelemetrySensors(string topic) =>
        Assert.Contains(Discovery.DiscoveryMessages(), m => m.Topic == topic);

    [Fact]
    public void StateJson_IncludesTheTelemetryFields()
    {
        using var json = JsonDocument.Parse(Discovery.StateJson(Status()));
        var s = json.RootElement;

        Assert.Equal("Charging", s.GetProperty("charger_status").GetString());
        Assert.True(s.GetProperty("car_connected").GetBoolean());
        Assert.Equal(7010, s.GetProperty("solar_w").GetDouble());
        Assert.Equal(10785, s.GetProperty("ev_power_w").GetDouble());
        Assert.Equal(16, s.GetProperty("ev_current_a").GetInt32());
    }

    [Fact]
    public void BatteryHoldTopics_FollowTheConfiguredPrefixes()
    {
        Assert.Equal("solax/solax_controller/battery_hold/set", Discovery.BatteryHoldCommandTopic);
        Assert.Equal("solax/solax_controller/battery_hold/state", Discovery.BatteryHoldStateTopic);
    }

    [Fact]
    public void DiscoveryMessages_PublishTheBatteryHoldSwitch_OnlyWhenTheFeatureIsEnabled()
    {
        const string switchTopic = "homeassistant/switch/solax_controller/battery_hold/config";

        // Off: the feature is inert, so publishing a switch would offer a control that does nothing.
        Assert.DoesNotContain(Discovery.DiscoveryMessages(), m => m.Topic == switchTopic);
        Assert.Contains(switchTopic, Discovery.RetiredDiscoveryTopics());

        var messages = DiscoveryWithBatteryHold.DiscoveryMessages().ToList();
        var switchMsg = messages.Single(m => m.Topic == switchTopic);
        using var json = JsonDocument.Parse(switchMsg.Payload);
        var s = json.RootElement;

        Assert.Equal("solax_controller_battery_hold", s.GetProperty("unique_id").GetString());
        Assert.Equal(Discovery.BatteryHoldCommandTopic, s.GetProperty("command_topic").GetString());
        Assert.Equal(Discovery.BatteryHoldStateTopic, s.GetProperty("state_topic").GetString());
        Assert.Equal("ON", s.GetProperty("payload_on").GetString());
        Assert.Equal("OFF", s.GetProperty("payload_off").GetString());

        Assert.Contains(messages, m => m.Topic == "homeassistant/sensor/solax_controller/battery_hold_target/config");
        Assert.DoesNotContain(switchTopic, DiscoveryWithBatteryHold.RetiredDiscoveryTopics());
    }

    [Theory]
    [InlineData("ON", true)]
    [InlineData("off", false)]
    public void TryParseSwitch_AcceptsThePayloads(string payload, bool expected)
    {
        Assert.True(HaDiscovery.TryParseSwitch(payload, out var on));
        Assert.Equal(expected, on);
    }

    [Fact]
    public void TryParseSwitch_RejectsUnknown() =>
        Assert.False(HaDiscovery.TryParseSwitch("maybe", out _));

    [Fact]
    public void StateJson_CarriesTheBatteryHoldTargetAndPower()
    {
        using var json = JsonDocument.Parse(Discovery.StateJson(Status()));
        var s = json.RootElement;

        Assert.Equal(-1250, s.GetProperty("battery_w").GetDouble());
        Assert.Equal(-2450, s.GetProperty("hold_target_w").GetDouble());
    }

    [Fact]
    public void StateJson_CarriesGridPower_PositiveWhenImporting()
    {
        using var json = JsonDocument.Parse(Discovery.StateJson(Status()));

        Assert.Equal(1601, json.RootElement.GetProperty("grid_w").GetDouble());
    }

    [Fact]
    public void StateJson_OmitsTheHoldTargetWhenNotHeld()
    {
        using var json = JsonDocument.Parse(Discovery.StateJson(Status(holdActive: false, holdTarget: null)));

        Assert.False(json.RootElement.TryGetProperty("hold_target_w", out _));
    }

    [Fact]
    public void DiscoveryMessages_NeverContainNullFields()
    {
        // Home Assistant rejects a whole config on any null field (e.g. "icon": null), which silently
        // drops the entity. So every payload must omit unset keys rather than send null.
        foreach (var (topic, payload) in DiscoveryWithBatteryHold.DiscoveryMessages())
        {
            using var json = JsonDocument.Parse(payload);
            foreach (var property in json.RootElement.EnumerateObject())
            {
                Assert.False(property.Value.ValueKind == JsonValueKind.Null, $"{topic} has null field '{property.Name}'");
            }
        }
    }

    [Fact]
    public void RetiredDiscoveryTopics_IncludeTheOldSwitch()
    {
        Assert.Contains("homeassistant/switch/solax_controller/charge_control/config", Discovery.RetiredDiscoveryTopics());
    }

    [Theory]
    [InlineData("Off", ChargeControlMode.Off)]
    [InlineData("solar", ChargeControlMode.Solar)]
    public void TryParseMode_AcceptsTheOptionStrings(string payload, ChargeControlMode expected)
    {
        Assert.True(HaDiscovery.TryParseMode(payload, out var mode));
        Assert.Equal(expected, mode);
    }

    [Fact]
    public void TryParseMode_RejectsUnknown() =>
        Assert.False(HaDiscovery.TryParseMode("nonsense", out _));

    [Fact]
    public void ModeState_RoundTripsTheEnumName() =>
        Assert.Equal("Solar", Discovery.ModeState(ChargeControlMode.Solar));

    [Fact]
    public void StateJson_SerialisesEveryFieldTheSensorsReference()
    {
        using var json = JsonDocument.Parse(Discovery.StateJson(Status()));
        var s = json.RootElement;

        Assert.Equal("Charging", s.GetProperty("state").GetString());
        Assert.Equal("Solar", s.GetProperty("mode").GetString());
        Assert.Equal(4181, s.GetProperty("surplus_w").GetDouble());
        Assert.Equal(6, s.GetProperty("target_a").GetInt32());
        Assert.Equal(16, s.GetProperty("active_a").GetInt32());
        Assert.Equal(99, s.GetProperty("soc").GetDouble());
        Assert.True(s.GetProperty("holding").GetBoolean());
    }

    [Fact]
    public void StateJson_OmitsNullMetricsWhenIdle()
    {
        var status = Status(mode: ChargeControlMode.Off, state: ChargeControlState.Disabled, surplus: null, target: null, active: null);

        using var json = JsonDocument.Parse(Discovery.StateJson(status));

        Assert.False(json.RootElement.TryGetProperty("target_a", out _));
        Assert.Equal("Disabled", json.RootElement.GetProperty("state").GetString());
    }

    private static SolarDayPlan TestPlan(DayOutlook outlook = DayOutlook.Tight) => new(
        RemainingPvWh: 11_000,
        ShoulderEnergyWh: 3400,
        PlateauEnergyWh: 11_000,
        PlateauClaimedByBatteryWh: 500,
        ExpectedHouseWh: 2300,
        BatteryToFullWh: 3900,
        EvBudgetWh: 10_500,
        FeasibleEvEnergyWh: 10_500,
        // Local-kind instants: the payload renders the window in local time, so a UTC literal would
        // make this test depend on the machine's timezone.
        NextFeasibleWindow: (new DateTimeOffset(new DateTime(2026, 7, 27, 12, 30, 0, DateTimeKind.Local)), new DateTimeOffset(new DateTime(2026, 7, 27, 15, 50, 0, DateTimeKind.Local))),
        RequiredSocFloorPercent: 62,
        ShortfallWh: 4400,
        EvExpectedTodayWh: 10_500,
        EvTargetWh: 15_000,
        Outlook: outlook,
        BiasFactor: 0.94,
        Deadline: new DateTimeOffset(2026, 7, 27, 19, 0, 0, TimeSpan.Zero),
        ForecastAsOf: new DateTimeOffset(2026, 7, 27, 9, 0, 0, TimeSpan.Zero),
        IsUsable: true,
        Reason: "Tight day: 10.5kWh for the car");

    [Theory]
    [InlineData("homeassistant/sensor/solax_controller/day_outlook/config")]
    [InlineData("homeassistant/sensor/solax_controller/plan_state/config")]
    [InlineData("homeassistant/sensor/solax_controller/charge_window/config")]
    [InlineData("homeassistant/sensor/solax_controller/ev_budget/config")]
    [InlineData("homeassistant/sensor/solax_controller/ev_expected_today/config")]
    [InlineData("homeassistant/sensor/solax_controller/shortfall/config")]
    [InlineData("homeassistant/sensor/solax_controller/soc_floor/config")]
    [InlineData("homeassistant/sensor/solax_controller/forecast_remaining/config")]
    [InlineData("homeassistant/sensor/solax_controller/tomorrow_forecast/config")]
    [InlineData("homeassistant/sensor/solax_controller/forecast_accuracy/config")]
    [InlineData("homeassistant/sensor/solax_controller/session_energy/config")]
    [InlineData("homeassistant/sensor/solax_controller/loaned_today/config")]
    [InlineData("homeassistant/sensor/solax_controller/loan_power/config")]
    public void DiscoveryMessages_IncludeTheForecastPlanSensors(string topic) =>
        Assert.Contains(Discovery.DiscoveryMessages(), m => m.Topic == topic);

    [Theory]
    [InlineData("daily_ev_target")]
    [InlineData("session_energy_target")]
    [InlineData("min_battery_soc")]
    public void DiscoveryMessages_IncludeTheSettableNumbers(string objectId)
    {
        var message = Discovery.DiscoveryMessages()
            .Single(m => m.Topic == $"homeassistant/number/solax_controller/{objectId}/config");

        using var json = JsonDocument.Parse(message.Payload);
        var s = json.RootElement;

        Assert.Equal(Discovery.NumberCommandTopic(objectId), s.GetProperty("command_topic").GetString());
        Assert.Equal(Discovery.NumberStateTopic(objectId), s.GetProperty("state_topic").GetString());
        Assert.Equal($"solax_controller_{objectId}", s.GetProperty("unique_id").GetString());
    }

    [Fact]
    public void NumberTopics_FollowTheConfiguredPrefixes()
    {
        Assert.Equal("solax/solax_controller/daily_ev_target/set", Discovery.NumberCommandTopic("daily_ev_target"));
        Assert.Equal("solax/solax_controller/daily_ev_target/state", Discovery.NumberStateTopic("daily_ev_target"));
    }

    [Fact]
    public void StateJson_CarriesThePlanWhenTheForecastModeIsDriving()
    {
        using var json = JsonDocument.Parse(Discovery.StateJson(Status(mode: ChargeControlMode.Forecasted, plan: TestPlan(), loanWatts: 1140)));
        var s = json.RootElement;

        Assert.Equal("Tight", s.GetProperty("outlook").GetString());
        Assert.Equal("12:30-15:50", s.GetProperty("window").GetString());
        Assert.Equal(10.5, s.GetProperty("ev_budget_kwh").GetDouble());
        Assert.Equal(4.4, s.GetProperty("shortfall_kwh").GetDouble());
        Assert.Equal(62, s.GetProperty("soc_floor").GetDouble());
        Assert.Equal(11, s.GetProperty("forecast_remaining_kwh").GetDouble());
        Assert.Equal(94, s.GetProperty("bias_percent").GetDouble());
        Assert.Equal(1140, s.GetProperty("loan_w").GetDouble());
        Assert.Equal(8.4, s.GetProperty("session_kwh").GetDouble());
        Assert.Equal(1.8, s.GetProperty("loaned_kwh").GetDouble());
        Assert.Equal(24.5, s.GetProperty("tomorrow_kwh").GetDouble());
    }

    [Fact]
    public void StateJson_OmitsThePlanBlockInTheOtherModes()
    {
        // Reporting a stale plan nothing is acting on would be worse than reporting nothing.
        using var json = JsonDocument.Parse(Discovery.StateJson(Status()));

        Assert.False(json.RootElement.TryGetProperty("outlook", out _));
        Assert.False(json.RootElement.TryGetProperty("soc_floor", out _));
    }

    [Fact]
    public void TryParseMode_AcceptsForecasted()
    {
        Assert.True(HaDiscovery.TryParseMode("forecasted", out var mode));
        Assert.Equal(ChargeControlMode.Forecasted, mode);
    }
}

