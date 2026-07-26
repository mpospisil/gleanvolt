using System.Text.Json;
using Solax.Core.Enums;
using Solax.Core.Models;
using Solax.Worker.Configuration;
using Solax.Worker.HomeAssistant;

namespace Solax.Worker.Tests;

public class HaDiscoveryTests
{
    private static readonly HaDiscovery Discovery = new(new HomeAssistantOptions
    {
        BaseTopic = "solax",
        DeviceId = "solax_controller",
        DiscoveryPrefix = "homeassistant",
    });

    private static ChargeControlStatus Status(
        ChargeControlMode mode = ChargeControlMode.Solar,
        ChargeControlState state = ChargeControlState.Charging,
        double? surplus = 4180.7,
        int? target = 6,
        int? active = 16) =>
        new(mode, DryRun: true, HoldingControl: true, state, surplus, target, active, BatterySocPercent: 98.6,
            ChargerStatus: EvChargerStatus.Charging, CarConnected: true, SolarPowerWatts: 7010.4,
            EvChargerPowerWatts: 10784.9, EvChargingCurrentAmps: 16, Timestamp: DateTimeOffset.UtcNow);

    [Fact]
    public void Topics_FollowTheConfiguredPrefixes()
    {
        Assert.Equal("solax/solax_controller/availability", Discovery.AvailabilityTopic);
        Assert.Equal("solax/solax_controller/state", Discovery.StateTopic);
        Assert.Equal("solax/solax_controller/charge_mode/set", Discovery.ModeCommandTopic);
        Assert.Equal("solax/solax_controller/charge_mode/state", Discovery.ModeStateTopic);
    }

    [Fact]
    public void DiscoveryMessages_IncludeAModeSelectWithTheThreeOptions()
    {
        var messages = Discovery.DiscoveryMessages().ToList();

        var selectMsg = messages.Single(m => m.Topic == "homeassistant/select/solax_controller/charge_mode/config");
        using var json = JsonDocument.Parse(selectMsg.Payload);
        var s = json.RootElement;

        Assert.Equal("solax_controller_charge_mode", s.GetProperty("unique_id").GetString());
        Assert.Equal(Discovery.ModeCommandTopic, s.GetProperty("command_topic").GetString());
        Assert.Equal(Discovery.ModeStateTopic, s.GetProperty("state_topic").GetString());
        var options = s.GetProperty("options").EnumerateArray().Select(o => o.GetString()).ToArray();
        Assert.Equal(["Off", "Solar"], options);

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
}
