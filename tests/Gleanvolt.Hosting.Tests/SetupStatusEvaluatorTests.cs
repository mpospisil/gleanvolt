using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Setup;
using Microsoft.Extensions.Configuration;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// #301: whether an installation is usable is computed from its configuration, every time, and says
/// exactly what is missing.
/// </summary>
public class SetupStatusEvaluatorTests
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private static readonly SetupHost Linux = new(Prague, IsWindows: false);

    private static SetupStatus Evaluate(SetupHost host, params (string Key, string? Value)[] settings) =>
        SetupStatusEvaluator.Evaluate(
            new ConfigurationBuilder()
                .AddInMemoryCollection(settings
                    .GroupBy(setting => setting.Key)
                    .Select(group => new KeyValuePair<string, string?>(group.Key, group.Last().Value)))
                .Build(),
            host);

    private static SetupStatus Usable(params (string Key, string? Value)[] overrides) =>
        Evaluate(Linux, [.. UsableSite.Settings, .. overrides]);

    private static IEnumerable<SetupArea> Areas(SetupStatus status) => status.MissingRequired.Select(problem => problem.Area);

    [Fact]
    public void A_complete_configuration_is_usable()
    {
        var status = Usable();

        Assert.True(status.Usable);
        Assert.Empty(status.MissingRequired);
    }

    [Fact]
    public void Nothing_at_all_lists_every_required_item()
    {
        var status = Evaluate(Linux);

        Assert.False(status.Usable);
        Assert.Equal([SetupArea.Password, SetupArea.Inverter, SetupArea.Charger], Areas(status));
        Assert.All(status.MissingRequired, problem => Assert.NotNull(problem.Phrase));
        Assert.Equal([SetupArea.Location], status.Recommended.Select(problem => problem.Area));
    }

    [Theory]
    [InlineData("Web:PasswordHash", SetupArea.Password)]
    [InlineData("Pv:Inverter:Host", SetupArea.Inverter)]
    [InlineData("Pv:Chargers:0:Host", SetupArea.Charger)]
    public void Each_missing_item_is_named_on_its_own(string key, SetupArea area)
    {
        var status = Usable((key, ""));

        Assert.Equal([area], Areas(status));
    }

    [Fact]
    public void No_location_at_all_is_recommended_but_does_not_block()
    {
        var status = Usable(("Pv:Latitude", null), ("Pv:Longitude", null));

        Assert.True(status.Usable);
        var problem = Assert.Single(status.Recommended);
        Assert.Equal(SetupArea.Location, problem.Area);
        Assert.Equal(Phrase.Of("Config.SetupNoLocation"), problem.Phrase);
    }

    [Fact]
    public void Half_a_location_is_a_problem_too()
    {
        var status = Usable(("Pv:Longitude", null));

        Assert.Equal([SetupArea.Location], Areas(status));
        Assert.Contains("Pv:Longitude", status.MissingRequired[0].English);
    }

    [Fact]
    public void A_moved_key_keeps_the_controller_stopped()
    {
        var status = Usable(("Vehicle:Topic", "old/topic"));

        var problem = Assert.Single(status.MissingRequired);
        Assert.Equal(SetupArea.Configuration, problem.Area);
        Assert.Contains("Ev:Vehicles:0:Telemetry:Topic", problem.English);
    }

    [Fact]
    public void An_explicitly_open_ui_or_no_ui_needs_no_password()
    {
        Assert.True(Usable(("Web:PasswordHash", ""), ("Web:RequireAuthentication", "false")).Usable);
        Assert.True(Usable(("Web:PasswordHash", ""), ("Web:Enabled", "false")).Usable);
    }

    [Fact]
    public void An_unknown_time_zone_is_a_problem()
    {
        var status = Usable(("Controller:TimeZone", "Europe/Atlantis"));

        Assert.Equal([SetupArea.TimeZone], Areas(status));
        Assert.Contains("Europe/Atlantis", status.MissingRequired[0].English);
    }

    [Fact]
    public void An_unset_zone_is_the_hosts_own_on_linux()
    {
        Assert.True(Evaluate(Linux, [.. UsableSite.Settings, ("Controller:TimeZone", "")]).Usable);
    }

    [Fact]
    public void An_unset_zone_is_recommended_on_windows_and_on_a_bare_utc_host_but_does_not_block()
    {
        var windows = Evaluate(new SetupHost(Prague, IsWindows: true), [.. UsableSite.Settings, ("Controller:TimeZone", "")]);
        var utc = Evaluate(new SetupHost(TimeZoneInfo.Utc, IsWindows: false), [.. UsableSite.Settings, ("Controller:TimeZone", "")]);

        Assert.True(windows.Usable);
        Assert.True(utc.Usable);
        Assert.Equal([SetupArea.TimeZone], windows.Recommended.Select(problem => problem.Area));
        Assert.Equal([SetupArea.TimeZone], utc.Recommended.Select(problem => problem.Area));
    }

    [Fact]
    public void Optional_settings_never_block_and_are_listed_when_missing()
    {
        var bare = Usable();

        Assert.True(bare.Usable);
        Assert.Equal(
            [OptionalSetting.Solcast, OptionalSetting.Ev, OptionalSetting.Tariff, OptionalSetting.Mqtt, OptionalSetting.Weather],
            bare.MissingOptional);

        var configured = Usable(
            ("Solcast:ApiKey", "key"),
            ("Solcast:ResourceId", "abcd-1234"),
            ("Ev:Vehicles:0:Id", "car"),
            ("HomeAssistant:Enabled", "true"),
            ("Weather:ApiKey", "weather"));

        Assert.Equal([OptionalSetting.Tariff], configured.MissingOptional);
    }

    [Fact]
    public void The_shipped_placeholder_is_not_a_solcast_site()
    {
        var status = Usable(("Solcast:ApiKey", "key"), ("Solcast:ResourceId", "your-solcast-resource-id"));

        Assert.Contains(OptionalSetting.Solcast, status.MissingOptional);
    }
}
