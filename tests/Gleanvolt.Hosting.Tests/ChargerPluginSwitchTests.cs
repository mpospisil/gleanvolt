using Gleanvolt.Core.Configuration;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Setup;
using Microsoft.Extensions.Configuration;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The move from the built-in charger drivers to the plugins (#444): which plugin replaces which, and the switch the
/// deploy scripts set, which makes the charger the plugin's whatever the configuration names, so the two never both
/// open it.
/// </summary>
public sealed class ChargerPluginSwitchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gleanvolt-switch-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("solax", "solax")]
    [InlineData("SolaX ", "solax")]
    [InlineData("huawei-7ks", "huawei")]
    [InlineData("huawei-22kt", "huawei")]
    [InlineData("goe", "goe")]
    [InlineData("external", null)]
    [InlineData("nonsense", null)]
    public void Each_built_in_driver_names_the_plugin_that_replaces_it(string driver, string? profile)
    {
        Assert.Equal(profile, ChargerPlugins.Replacing(driver)?.Profile);
    }

    [Fact]
    public void Every_built_in_driver_has_a_replacement()
    {
        Assert.All(
            ChargerDrivers.Known.Where(driver => driver.Id != Chargers.ExternalChargerDriverFactory.DriverId),
            driver => Assert.NotNull(ChargerPlugins.Replacing(driver.Id)));
    }

    [Theory]
    [InlineData("true", "external")]
    [InlineData("false", "solax")]
    public void The_switch_makes_the_charger_the_plugins_whatever_the_environment_names(string usePlugin, string expected)
    {
        var site = PvSystemResolver.Resolve(Configuration(
            ("Pv:Chargers:0:Driver", "solax"), ("Pv:Chargers:0:Host", "192.168.2.6"), ("ExternalCharger:UsePlugin", usePlugin)));

        Assert.Equal(expected, Assert.Single(site.Chargers).Driver);
    }

    [Fact]
    public void The_switch_wins_over_a_brand_saved_on_Settings()
    {
        // The overrides file wins over the environment, so the switch has to win over the file too.
        var path = Path.Combine(_root, PvSystemOverrides.DefaultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PvSystemOverrides.Write(path, new Dictionary<string, string> { ["Pv:Chargers:0:Driver"] = "goe" });
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection([.. UsableSite.Settings.Select(Pair), new("ExternalCharger:UsePlugin", "true")]);
        configuration.AddPvSystemOverrides(_root);

        Assert.Equal("external", Assert.Single(PvSystemResolver.Resolve(configuration).Chargers).Driver);
    }

    [Fact]
    public void Setup_sees_the_switch_too()
    {
        var status = SetupStatusEvaluator.Evaluate(
            Configuration(("Pv:Chargers:0:Driver", "solax"), ("Pv:Chargers:0:Host", ""), ("ExternalCharger:UsePlugin", "true")),
            new SetupHost(TimeZoneInfo.Utc, IsWindows: false));

        // A built-in SolaX with no host would be a problem; behind the plugin it is none.
        Assert.True(status.Usable, string.Join("; ", status.MissingRequired.Select(problem => problem.English)));
    }

    private static IConfiguration Configuration(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(UsableSite.Pairs)
            .AddInMemoryCollection(settings.Select(Pair))
            .Build();

    private static KeyValuePair<string, string?> Pair((string Key, string? Value) setting) => new(setting.Key, setting.Value);
}
