using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Vehicles;
using Gleanvolt.Infrastructure.Vehicles.Skoda;
using Gleanvolt.Infrastructure.Vehicles.VwGroup;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// Which vehicle feed the composition root turns on (issues #140, #212): <b>one car, one feed</b> —
/// volkswagen.de for a VW, MyŠkoda for a Škoda, and the Data Act portal only for a car with neither.
///
/// <para>Asserted on the registrations rather than by resolving hosted services, because resolving
/// them all would build a Modbus stack this test has no use for.</para>
/// </summary>
public class VehicleFeedRegistrationTests
{
    private static readonly (string Key, string? Value)[] MinimalDevices =
    [
        ("Pv:Inverter:Host", "127.0.0.1"),
        ("Pv:Chargers:0:Host", "127.0.0.1"),
    ];

    /// <summary>Enough for the portal client to be considered configured.</summary>
    private static readonly (string Key, string? Value)[] Credentials =
    [
        ("Vehicle:DataAct:Brand", "vw"),
        ("Vehicle:DataAct:Username", "owner@example.com"),
        ("Vehicle:DataAct:Password", "hunter2"),
    ];

    private static readonly (string Key, string? Value)[] TheCar =
    [
        ("Ev:Vehicles:0:Id", "id4"),
        ("Ev:Vehicles:0:BatteryCapacityKWh", "77"),
    ];

    private static IServiceCollection Services(params (string Key, string? Value)[] settings)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(
            MinimalDevices.Concat(settings).Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)));
        builder.Services.AddGleanvolt(builder.Configuration);

        return builder.Services;
    }

    private static bool Registers<T>(IServiceCollection services) =>
        services.Any(descriptor => descriptor.ImplementationType == typeof(T));

    [Fact]
    public void Off_by_default_the_MQTT_feed_is_the_one_that_runs()
    {
        // Nothing that leaves the LAN starts itself, and an installation that has never heard of the
        // portal must behave exactly as it did before #140.
        var services = Services();

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IVehicleUpdateService));
        Assert.True(Registers<VehicleMqttWorker>(services));
        Assert.True(Registers<VehicleUpdateWorker>(services));
    }

    [Fact]
    public void Credentials_alone_do_not_start_a_feed()
    {
        // A .env that carries a VW ID because somebody pressed the Vehicle portal button once must not
        // become an unattended feed at the next restart. Signing in on a clock is a separate decision.
        var services = Services(Credentials.Concat(TheCar).ToArray());

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IVehicleUpdateService));
        Assert.True(Registers<VehicleMqttWorker>(services));
    }

    [Fact]
    public async Task Switched_on_it_serves_the_configured_car_and_the_MQTT_feed_keeps_running()
    {
        // Both feeds run, and VehicleStateHolder keeps whichever reading is newest. This used to
        // exclude the MQTT worker outright, on the issue's "the manufacturer service wins" -- until
        // the reference install showed the portal's state of charge to be coarser and later than the
        // same manufacturer's app API arriving over MQTT. Which source is better is a fact about a car
        // and a moment, not something a registration can be right about once.
        var services = Services(
            Credentials.Concat(TheCar).Concat([("Vehicle:DataAct:Enabled", "true")]).ToArray());

        Assert.True(Registers<VehicleMqttWorker>(services));

        await using var provider = services.BuildServiceProvider();
        var feed = provider.GetRequiredService<IVehicleUpdateService>();

        Assert.Equal("vw-group", feed.Manufacturer);
        Assert.Equal("id4", feed.VehicleId);

        // The portal button is untouched by any of this: it is what proves the credentials before the
        // feed is switched on, so it stays available on exactly the same terms.
        Assert.True(provider.GetRequiredService<IVehiclePortalReader>().IsConfigured);
    }

    /// <summary>A Škoda installation (issue #193): its own section, its own sign-in, its own feed.</summary>
    private static readonly (string Key, string? Value)[] TheSkoda =
    [
        ("Ev:Vehicles:0:Id", "enyaq"),
        ("Ev:Vehicles:0:Make", "Škoda"),
        ("Ev:Vehicles:0:BatteryCapacityKWh", "77"),
        ("Vehicle:Skoda:Vin", "TMBJB9NY5RF999999"),
        ("Vehicle:Skoda:KeyPath", Path.Combine(Path.GetTempPath(), "gleanvolt-no-such-dir", "skoda-api-key.json")),
    ];

    [Fact]
    public async Task A_skoda_installation_registers_its_feed_and_its_key_sign_in_without_a_key()
    {
        // Enabled and a VIN are enough: the key is pasted on the page afterwards, and until then the
        // feed is present, sends nothing, and says so.
        var services = Services(TheSkoda.Concat([("Vehicle:Skoda:Enabled", "true")]).ToArray());

        await using var provider = services.BuildServiceProvider();
        var feed = provider.GetRequiredService<IVehicleUpdateService>();

        Assert.Equal("skoda", feed.Manufacturer);
        Assert.Equal("enyaq", feed.VehicleId);
        Assert.False(feed.DeliversOnlyWhileCharging);
        Assert.True(feed.Health.IsBlocked);

        var signIn = Assert.IsType<SkodaApiSignIn>(provider.GetRequiredService<IVehicleAccountSignIn>());
        Assert.True(signIn.State.WantsKey);
    }

    [Fact]
    public void Make_alone_chooses_no_feed()
    {
        // "Škoda" in Ev:Vehicles is reported, never acted on. The section is the switch.
        var services = Services(TheSkoda);

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IVehicleUpdateService));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(SkodaApiKeyStore));
    }

    [Fact]
    public void Website_and_Skoda_both_enabled_is_refused_naming_both()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Services(
            TheSkoda.Concat(
            [
                ("Vehicle:Skoda:Enabled", "true"),
                ("Vehicle:Website:Enabled", "true"),
            ]).ToArray()));

        Assert.Contains("Vehicle:Website", error.Message);
        Assert.Contains("Vehicle:Skoda", error.Message);
    }

    /// <summary>A Volkswagen read from volkswagen.de (issue #170).</summary>
    private static readonly (string Key, string? Value)[] TheWebsite =
    [
        ("Vehicle:Website:Enabled", "true"),
        ("Vehicle:Website:Username", "owner@example.com"),
        ("Vehicle:Website:Password", "hunter2"),
        ("Vehicle:Website:Vin", "WVGZZZE2ZPE999999"),
        ("Vehicle:Website:SessionPath", Path.Combine(Path.GetTempPath(), "gleanvolt-no-such-dir", "vw-website-session.json")),
    ];

    private static readonly (string Key, string? Value)[] ThePortalSwitchedOn =
        Credentials.Concat([("Vehicle:DataAct:Enabled", "true")]).ToArray();

    [Fact]
    public async Task A_VW_with_volkswagen_de_runs_that_feed_alone_and_says_why_the_portal_is_not_used()
    {
        // One car, one feed (#212). An ID.4 .env that has carried both since #170 keeps booting: the
        // portal is set aside, not refused, and the reason is on the startup log.
        var services = Services(TheCar.Concat(TheWebsite).Concat(ThePortalSwitchedOn).ToArray());

        var feed = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IVehicleUpdateService));

        await using var provider = services.BuildServiceProvider();
        var configured = provider.GetRequiredService<ConfiguredVehicleFeed>();

        Assert.Equal("vw-website", configured.Service!.Manufacturer);
        Assert.Same(provider.GetRequiredService<IVehicleUpdateService>(), configured.Service);
        Assert.Equal(
            "Vehicle:Website is configured, so the Data Act portal is not used: volkswagen.de reads this car "
            + "live. Vehicle:DataAct:Enabled (VW_ENABLED) can be switched off.",
            configured.SetAside);

        // The portal button stays: it is on the Vehicle portal page whatever runs on the clock.
        Assert.True(provider.GetRequiredService<IVehiclePortalReader>().IsConfigured);
    }

    [Fact]
    public async Task A_Skoda_with_MySkoda_runs_that_feed_alone()
    {
        var services = Services(
            TheSkoda.Concat([("Vehicle:Skoda:Enabled", "true")]).Concat(ThePortalSwitchedOn).ToArray());

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IVehicleUpdateService));

        await using var provider = services.BuildServiceProvider();
        var configured = provider.GetRequiredService<ConfiguredVehicleFeed>();

        Assert.Equal("skoda", configured.Service!.Manufacturer);
        Assert.StartsWith("Vehicle:Skoda is configured, so the Data Act portal is not used", configured.SetAside);
    }

    [Fact]
    public async Task A_car_with_no_live_feed_reads_the_portal_as_before()
    {
        // A Cupra, or another Group brand: the portal is the feed, and nothing is set aside.
        var services = Services(TheCar.Concat(ThePortalSwitchedOn).ToArray());

        await using var provider = services.BuildServiceProvider();
        var configured = provider.GetRequiredService<ConfiguredVehicleFeed>();

        Assert.Equal("vw-group", configured.Service!.Manufacturer);
        Assert.Null(configured.SetAside);
    }

    [Fact]
    public async Task A_live_feed_without_the_portal_switched_on_sets_nothing_aside()
    {
        var services = Services(TheCar.Concat(TheWebsite).ToArray());

        await using var provider = services.BuildServiceProvider();
        var configured = provider.GetRequiredService<ConfiguredVehicleFeed>();

        Assert.Equal("vw-website", configured.Service!.Manufacturer);
        Assert.Null(configured.SetAside);
    }

    [Fact]
    public async Task With_no_feed_the_configured_feed_is_none()
    {
        await using var provider = Services(TheCar).BuildServiceProvider();

        Assert.Null(provider.GetRequiredService<ConfiguredVehicleFeed>().Service);
    }

    [Fact]
    public void An_unnamed_MQTT_reading_is_given_this_feed_s_name()
    {
        // The payload's "source" is optional (#73). Left unnamed, a reading would be filed under
        // "unnamed" in the week's tally and the dashboard's "via ..." would stay blank for the feed
        // that has been working since then.
        var reading = VehicleMqttWorker.Label(new VehicleState(DateTimeOffset.UnixEpoch, SocPercent: 60));

        Assert.Equal(VehicleMqttWorker.DefaultSourceId, reading.SourceId);
    }

    [Fact]
    public void A_publisher_that_named_itself_keeps_its_name()
    {
        var reading = VehicleMqttWorker.Label(
            new VehicleState(DateTimeOffset.UnixEpoch, SocPercent: 60, SourceId: "id4"));

        Assert.Equal("id4", reading.SourceId);
    }

    [Fact]
    public void The_short_env_name_switches_it_on_too()
    {
        // A hand-edited .env is where these get typed, and VW_* is what docs/VW_PORTAL_SETUP.md has
        // documented since #139.
        Environment.SetEnvironmentVariable("VW_ENABLED", "true");

        try
        {
            var services = Services(Credentials.Concat(TheCar).ToArray());

            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IVehicleUpdateService));
        }
        finally
        {
            Environment.SetEnvironmentVariable("VW_ENABLED", null);
        }
    }

    [Fact]
    public void The_section_wins_over_the_env_name()
    {
        // The rule the resolver already applies to the credentials: a deployment's own configuration
        // is not quietly overridden by a developer's leftover .env.
        Environment.SetEnvironmentVariable("VW_ENABLED", "true");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection([new KeyValuePair<string, string?>("Vehicle:DataAct:Enabled", "false")])
                .Build();

            Assert.False(VwGroupPortalOptionsResolver.IsFeedEnabled(configuration));
        }
        finally
        {
            Environment.SetEnvironmentVariable("VW_ENABLED", null);
        }
    }
    [Fact]
    public void The_merge_budget_is_bound_from_configuration()
    {
        // The page and the log both tell an owner to raise this when a reading is short of something.
        // That advice did nothing at all while the number was a constant the configuration could not
        // reach -- the worst kind of wrong, because following it looks like the answer failing.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                [new KeyValuePair<string, string?>("Vehicle:DataAct:MaxDatasetsPerRead", "12")])
            .Build();

        Assert.Equal(12, VwGroupPortalOptionsResolver.Resolve(configuration).MaxDatasetsPerRead);
    }

    [Fact]
    public void An_unstated_budget_keeps_the_default()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Equal(
            new VwGroupPortalOptions().MaxDatasetsPerRead,
            VwGroupPortalOptionsResolver.Resolve(configuration).MaxDatasetsPerRead);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("0")]
    [InlineData("-2")]
    public void A_budget_that_is_not_a_count_is_refused_rather_than_ignored(string stated)
    {
        // Clamping a typo back to the default is how somebody spends an afternoon wondering why
        // raising it changed nothing.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                [new KeyValuePair<string, string?>("Vehicle:DataAct:MaxDatasetsPerRead", stated)])
            .Build();

        var error = Assert.Throws<InvalidOperationException>(
            () => VwGroupPortalOptionsResolver.Resolve(configuration));

        Assert.Contains("MaxDatasetsPerRead", error.Message);
    }


}
