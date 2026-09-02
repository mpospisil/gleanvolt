using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Vehicles;
using Gleanvolt.Infrastructure.Vehicles.VwGroup;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// Which vehicle feed the composition root turns on (issue #140), and the one decision that cannot
/// live in either worker: <b>two sources must not write to one holder</b>, so when the manufacturer's
/// service is configured the MQTT feed is not subscribed at all.
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
