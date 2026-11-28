using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Tests.Models;

/// <summary>
/// The built-in car feeds' replacements (#476): each names its plugin in gleanvolt-cars and the settings that carry
/// over to it, and none of those is a secret.
/// </summary>
public class VehicleFeedPluginTests
{
    [Theory]
    [InlineData(VehicleFeed.Volkswagen, "volkswagen")]
    [InlineData(VehicleFeed.Skoda, "skoda")]
    [InlineData(VehicleFeed.DataAct, "vwgroup")]
    public void EveryBuiltInNamesItsPluginAndNoPasswordIsCarried(VehicleFeed feed, string id)
    {
        var plugin = VehicleFeeds.Plugin(feed);

        Assert.NotNull(plugin);
        Assert.Equal(id, plugin.Id);
        Assert.NotEmpty(plugin.CarriedKeys);
        Assert.All(plugin.CarriedKeys, key =>
        {
            Assert.False(EvSettingKeys.IsSecret(key), key);
            Assert.Contains(key, VehicleFeeds.Fields(feed));
        });
    }

    [Theory]
    [InlineData(VehicleFeed.ChargeOnly)]
    [InlineData(VehicleFeed.OwnTopic)]
    [InlineData(VehicleFeed.External)]
    public void What_is_not_a_built_in_manufacturer_feed_has_no_plugin(VehicleFeed feed) =>
        Assert.Null(VehicleFeeds.Plugin(feed));
}
