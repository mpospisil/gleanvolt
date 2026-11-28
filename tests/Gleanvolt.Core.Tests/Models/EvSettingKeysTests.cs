using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Tests.Models;

/// <summary>
/// The allowlist <c>/car</c> is bounded by (issue #214), and the feed table the page's one
/// &lt;select&gt; is built from. What these guard is the boundary itself: a key that is not on the
/// list cannot be written from a browser, and every choice the page offers knows which key switches
/// it on and what it then needs.
/// </summary>
public class EvSettingKeysTests
{
    [Fact]
    public void Every_listed_key_is_editable_and_nothing_else_is()
    {
        Assert.All(EvSettingKeys.All, key => Assert.True(EvSettingKeys.IsEditable(key)));

        // The neighbours worth naming: a second car, the broker that is the house's rather than the
        // car's, and the staleness guard. Each is a plausible next key and none of them is this page's.
        Assert.False(EvSettingKeys.IsEditable("Ev:Vehicles:1:Id"));
        Assert.False(EvSettingKeys.IsEditable("Vehicle:BrokerHost"));
        Assert.False(EvSettingKeys.IsEditable("Vehicle:Password"));
        Assert.False(EvSettingKeys.IsEditable("Vehicle:MaxAge"));
        Assert.False(EvSettingKeys.IsEditable("ChargeControl:MaxChargingCurrentAmps"));
    }

    [Fact]
    public void Keys_are_matched_the_way_configuration_matches_them()
    {
        Assert.True(EvSettingKeys.IsEditable("ev:vehicles:0:phases"));
        Assert.Equal(EvSettingKeys.Phases, EvSettingKeys.Canonical("EV:VEHICLES:0:PHASES"));
        Assert.Null(EvSettingKeys.Canonical("Ev:Vehicles:1:Phases"));
    }

    [Fact]
    public void The_built_in_feeds_keys_are_not_editable()
    {
        // Removed in #477: a car plugin keeps the account on its own page.
        Assert.False(EvSettingKeys.IsEditable("Vehicle:Website:Enabled"));
        Assert.False(EvSettingKeys.IsEditable("Vehicle:Website:Password"));
        Assert.False(EvSettingKeys.IsEditable("Vehicle:Skoda:Vin"));
        Assert.False(EvSettingKeys.IsEditable("Vehicle:DataAct:Brand"));
    }

    [Fact]
    public void The_cars_own_fields_are_all_the_car_and_the_plugin_switch()
    {
        Assert.All(EvSettingKeys.Car, key => Assert.StartsWith("Ev:Vehicles:0:", key, StringComparison.Ordinal));
        Assert.Equal([.. EvSettingKeys.Car, EvSettingKeys.ExternalEnabled], EvSettingKeys.All);

        // #486: the car's own MQTT topic is gone.
        Assert.False(EvSettingKeys.IsEditable("Vehicle:Enabled"));
        Assert.False(EvSettingKeys.IsEditable("Ev:Vehicles:0:Telemetry:Topic"));
    }

    [Fact]
    public void The_environment_variable_is_the_one_an_operator_will_grep_for() =>
        Assert.Equal("Vehicle__External__Enabled", EvSettingKeys.EnvironmentVariable(EvSettingKeys.ExternalEnabled));
}

/// <summary>The feed table (issue #214): a car plugin or nothing (#486), and which a configuration selects.</summary>
public class VehicleFeedsTests
{
    [Fact]
    public void The_choices_are_a_car_plugin_or_nothing() =>
        Assert.Equal([VehicleFeed.External, VehicleFeed.ChargeOnly], VehicleFeeds.All);

    [Fact]
    public void Only_charge_only_switches_nothing_on()
    {
        Assert.Null(VehicleFeeds.EnabledKey(VehicleFeed.ChargeOnly));
        Assert.Equal(EvSettingKeys.ExternalEnabled, VehicleFeeds.EnabledKey(VehicleFeed.External));
    }

    [Fact]
    public void Nothing_enabled_is_charge_only() =>
        Assert.Equal(VehicleFeed.ChargeOnly, VehicleFeeds.Selected(_ => false));

    [Fact]
    public void The_plugin_switch_selects_the_plugin() =>
        Assert.Equal(VehicleFeed.External, VehicleFeeds.Selected(key => key == EvSettingKeys.ExternalEnabled));

    [Fact]
    public void The_old_mqtt_switch_selects_nothing() =>
        Assert.Equal(VehicleFeed.ChargeOnly, VehicleFeeds.Selected(key => key == "Vehicle:Enabled"));
}
