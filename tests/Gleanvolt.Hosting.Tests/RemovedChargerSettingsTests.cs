using Microsoft.Extensions.Configuration;
using Gleanvolt.Hosting.Configuration;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// <c>Pv:Chargers</c> after the built-in charger drivers left (#445): ignored, with a warning that names the plugin
/// that replaced the driver it chose.
/// </summary>
public sealed class RemovedChargerSettingsTests
{
    private static IConfiguration Configuration(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();

    [Theory]
    [InlineData("", "192.168.2.6", "solax")]
    [InlineData("solax", "", "solax")]
    [InlineData("HUAWEI-22KT", "192.168.2.6", "huawei")]
    [InlineData("huawei-7ks", "", "huawei")]
    [InlineData("goe", "192.168.2.6", "goe")]
    public void A_built_in_driver_is_ignored_and_the_warning_names_its_plugin(string driver, string host, string plugin)
    {
        var warning = RemovedChargerSettings.Warning(
            Configuration(("Pv:Chargers:0:Driver", driver), ("Pv:Chargers:0:Host", host)));

        Assert.NotNull(warning);
        Assert.Contains("Pv:Chargers is ignored", warning);
        Assert.Contains($"add {plugin} to GLEANVOLT_PLUGINS", warning);
        Assert.Contains($"gleanvolt-chargers/tree/main/{plugin}", warning);
    }

    [Fact]
    public void A_driver_no_plugin_replaced_is_still_said_to_be_ignored()
    {
        var warning = RemovedChargerSettings.Warning(Configuration(("Pv:Chargers:0:Driver", "external")));

        Assert.NotNull(warning);
        Assert.Contains("Pv:Chargers is ignored", warning);
        Assert.DoesNotContain("GLEANVOLT_PLUGINS", warning);
    }

    [Fact]
    public void Labels_alone_say_nothing()
    {
        // What the shipped appsettings.json and compose file had until 1.4: an id and a model, no driver, no host.
        Assert.Null(RemovedChargerSettings.Warning(
            Configuration(("Pv:Chargers:0:Id", "charger"), ("Pv:Chargers:0:Model", "SolaX X1/X3-HAC"), ("Pv:Chargers:0:Host", ""))));
        Assert.Null(RemovedChargerSettings.Warning(Configuration()));
    }

    [Theory]
    [InlineData("Pv:Chargers:0:Host", true)]
    [InlineData("pv:chargers:0:driver", true)]
    [InlineData("Pv:Inverter:Host", false)]
    [InlineData("Pv:ChargersX", false)]
    public void A_charger_key_is_one_under_the_section(string key, bool charger)
    {
        Assert.Equal(charger, RemovedChargerSettings.IsChargerKey(key));
    }
}
