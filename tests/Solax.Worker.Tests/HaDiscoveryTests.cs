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

    [Fact]
    public void Topics_FollowTheConfiguredPrefixes()
    {
        Assert.Equal("solax/solax_controller/availability", Discovery.AvailabilityTopic);
        Assert.Equal("solax/solax_controller/state", Discovery.StateTopic);
        Assert.Equal("solax/solax_controller/charge_control/set", Discovery.SwitchCommandTopic);
        Assert.Equal("solax/solax_controller/charge_control/state", Discovery.SwitchStateTopic);
    }

    [Fact]
    public void DiscoveryMessages_IncludeSwitchAndControlStateOnHaDiscoveryTopics()
    {
        var messages = Discovery.DiscoveryMessages().ToList();

        var switchMsg = messages.Single(m => m.Topic == "homeassistant/switch/solax_controller/charge_control/config");
        using var switchJson = JsonDocument.Parse(switchMsg.Payload);
        var s = switchJson.RootElement;
        Assert.Equal("solax_controller_charge_control", s.GetProperty("unique_id").GetString());
        Assert.Equal(Discovery.SwitchCommandTopic, s.GetProperty("command_topic").GetString());
        Assert.Equal(Discovery.AvailabilityTopic, s.GetProperty("availability_topic").GetString());
        Assert.Equal("solax_controller", s.GetProperty("device").GetProperty("identifiers")[0].GetString());

        Assert.Contains(messages, m => m.Topic == "homeassistant/sensor/solax_controller/control_state/config");
        Assert.Contains(messages, m => m.Topic == "homeassistant/binary_sensor/solax_controller/holding_control/config");
    }

    [Fact]
    public void SensorConfigs_ReadFromTheJsonStateTopicViaValueTemplate()
    {
        var surplus = Discovery.DiscoveryMessages().Single(m => m.Topic.EndsWith("/surplus/config"));
        using var json = JsonDocument.Parse(surplus.Payload);
        var s = json.RootElement;

        Assert.Equal(Discovery.StateTopic, s.GetProperty("state_topic").GetString());
        Assert.Equal("{{ value_json.surplus_w }}", s.GetProperty("value_template").GetString());
        Assert.Equal("W", s.GetProperty("unit_of_measurement").GetString());
        Assert.Equal("power", s.GetProperty("device_class").GetString());
    }

    [Fact]
    public void StateJson_SerialisesEveryFieldTheSensorsReference()
    {
        var status = new ChargeControlStatus(
            Enabled: true, DryRun: true, HoldingControl: true, State: ChargeControlState.Charging,
            SurplusWatts: 4180.7, TargetCurrentAmps: 6, ActiveCurrentAmps: 16,
            BatterySocPercent: 98.6, Timestamp: DateTimeOffset.UtcNow);

        using var json = JsonDocument.Parse(Discovery.StateJson(status));
        var s = json.RootElement;

        Assert.Equal("Charging", s.GetProperty("state").GetString());
        Assert.Equal(4181, s.GetProperty("surplus_w").GetDouble());
        Assert.Equal(6, s.GetProperty("target_a").GetInt32());
        Assert.Equal(16, s.GetProperty("active_a").GetInt32());
        Assert.Equal(99, s.GetProperty("soc").GetDouble());
        Assert.True(s.GetProperty("holding").GetBoolean());
    }

    [Fact]
    public void StateJson_OmitsNullMetricsWhenIdle()
    {
        var status = new ChargeControlStatus(
            Enabled: true, DryRun: false, HoldingControl: false, State: ChargeControlState.Idle,
            SurplusWatts: null, TargetCurrentAmps: null, ActiveCurrentAmps: null,
            BatterySocPercent: 50, Timestamp: DateTimeOffset.UtcNow);

        using var json = JsonDocument.Parse(Discovery.StateJson(status));

        Assert.False(json.RootElement.TryGetProperty("target_a", out _));
        Assert.Equal("Idle", json.RootElement.GetProperty("state").GetString());
    }

    [Theory]
    [InlineData(true, "ON")]
    [InlineData(false, "OFF")]
    public void SwitchState_MapsToHaSwitchPayloads(bool enabled, string expected) =>
        Assert.Equal(expected, Discovery.SwitchState(enabled));
}
