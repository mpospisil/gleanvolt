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
    public void The_passwords_are_the_only_secrets()
    {
        Assert.Equal(
            [EvSettingKeys.WebsitePassword, EvSettingKeys.DataActPassword],
            EvSettingKeys.Secrets);

        Assert.True(EvSettingKeys.IsSecret(EvSettingKeys.WebsitePassword));
        Assert.False(EvSettingKeys.IsSecret(EvSettingKeys.WebsiteUsername));
        Assert.All(EvSettingKeys.Secrets, key => Assert.Contains(key, EvSettingKeys.All));
    }

    [Fact]
    public void The_cars_own_fields_are_independent_of_any_feed()
    {
        // They are what makes charging correct, and they are editable whichever manufacturer is
        // picked -- including "charge only", which configures no feed at all.
        Assert.All(EvSettingKeys.Car, key => Assert.StartsWith("Ev:Vehicles:0:", key, StringComparison.Ordinal));
        Assert.All(
            VehicleFeeds.All.SelectMany(VehicleFeeds.Fields),
            key => Assert.DoesNotContain(key, EvSettingKeys.Car));
    }

    [Fact]
    public void The_environment_variable_is_the_one_an_operator_will_grep_for() =>
        Assert.Equal("Vehicle__Skoda__Vin", EvSettingKeys.EnvironmentVariable(EvSettingKeys.SkodaVin));
}

/// <summary>
/// The feed table (issue #214): what each manufacturer choice switches on, what it then needs, and
/// which choice a configuration already selects.
/// </summary>
public class VehicleFeedsTests
{
    [Fact]
    public void Only_charge_only_switches_nothing_on()
    {
        Assert.Null(VehicleFeeds.EnabledKey(VehicleFeed.ChargeOnly));
        Assert.All(
            VehicleFeeds.All.Where(feed => feed != VehicleFeed.ChargeOnly),
            feed => Assert.True(EvSettingKeys.IsEditable(VehicleFeeds.EnabledKey(feed)!)));
    }

    [Fact]
    public void Every_field_a_choice_offers_is_one_the_editor_will_accept() =>
        Assert.All(
            VehicleFeeds.All.SelectMany(VehicleFeeds.Fields),
            key => Assert.True(EvSettingKeys.IsEditable(key)));

    [Fact]
    public void What_a_feed_requires_is_a_subset_of_what_it_offers() =>
        Assert.All(
            VehicleFeeds.All,
            feed => Assert.All(VehicleFeeds.Required(feed), key => Assert.Contains(key, VehicleFeeds.Fields(feed))));

    [Fact]
    public void The_client_id_is_offered_but_never_required()
    {
        // The escape hatch for a brand whose sign-in id has changed: documentation for a failure, not
        // a field to fill, so requiring it would refuse every ordinary Data Act installation.
        Assert.Contains(EvSettingKeys.DataActClientId, VehicleFeeds.Fields(VehicleFeed.DataAct));
        Assert.DoesNotContain(EvSettingKeys.DataActClientId, VehicleFeeds.Required(VehicleFeed.DataAct));
    }

    [Fact]
    public void Nothing_enabled_is_charge_only() =>
        Assert.Equal(VehicleFeed.ChargeOnly, VehicleFeeds.Selected(_ => false));

    [Theory]
    [InlineData(VehicleFeed.Volkswagen)]
    [InlineData(VehicleFeed.Skoda)]
    [InlineData(VehicleFeed.DataAct)]
    [InlineData(VehicleFeed.OwnTopic)]
    public void One_enabled_key_selects_its_feed(VehicleFeed feed) =>
        Assert.Equal(feed, VehicleFeeds.Selected(key => key == VehicleFeeds.EnabledKey(feed)));

    [Fact]
    public void A_live_feed_beside_the_portal_is_the_one_named()
    {
        // The order the composition root resolves in (#212): the portal is set aside, not run beside
        // a live feed, so a file carrying both since before this page existed reads as the feed that
        // is actually running.
        Assert.Equal(
            VehicleFeed.Volkswagen,
            VehicleFeeds.Selected(key => key is EvSettingKeys.WebsiteEnabled or EvSettingKeys.DataActEnabled));

        Assert.Equal(
            VehicleFeed.Skoda,
            VehicleFeeds.Selected(key => key is EvSettingKeys.SkodaEnabled or EvSettingKeys.DataActEnabled));
    }
}
