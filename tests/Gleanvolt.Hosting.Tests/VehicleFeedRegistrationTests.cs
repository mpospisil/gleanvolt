using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Vehicles;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// Which vehicle feed the composition root turns on (issues #140, #212): <b>one car, one feed</b> — a car plugin
/// (<c>Vehicle:External</c>), or none, with the owner's own MQTT topic beside either. The built-in manufacturer feeds
/// are gone (#477).
///
/// <para>Asserted on the registrations rather than by resolving hosted services, because resolving
/// them all would build a Modbus stack this test has no use for.</para>
/// </summary>
public class VehicleFeedRegistrationTests
{
    private static readonly (string Key, string? Value)[] MinimalDevices =
    [.. UsableSite.Settings];

    private static readonly (string Key, string? Value)[] TheCar =
    [
        ("Ev:Vehicles:0:Id", "id4"),
        ("Ev:Vehicles:0:BatteryCapacityKWh", "77"),
    ];

    private static IServiceCollection Services(params (string Key, string? Value)[] settings)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(
            MinimalDevices.Concat(settings)
                .GroupBy(s => s.Key)
                .Select(group => new KeyValuePair<string, string?>(group.Key, group.Last().Value)));
        builder.Services.AddGleanvolt(builder.Configuration);

        return builder.Services;
    }

    private static bool Registers<T>(IServiceCollection services) =>
        services.Any(descriptor => descriptor.ImplementationType == typeof(T));

    [Fact]
    public void Off_by_default_the_MQTT_feed_is_the_one_that_runs()
    {
        // Nothing that leaves the LAN starts itself.
        var services = Services();

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IVehicleUpdateService));
        Assert.True(Registers<VehicleMqttWorker>(services));
        Assert.True(Registers<VehicleUpdateWorker>(services));
    }

    [Fact]
    public void A_built_in_feeds_old_settings_alone_start_nothing()
    {
        // #477: a VIN or a username left in .env from a built-in, without its flag, is not a feed.
        var services = Services(TheCar.Concat([("Vehicle:Website:Vin", "WVWZZZE2ZMP000001"), ("Vehicle:DataAct:Username", "owner@example.com")]).ToArray());

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IVehicleUpdateService));
    }

    [Fact]
    public void Make_alone_chooses_no_feed()
    {
        var services = Services(TheCar.Concat([("Ev:Vehicles:0:Make", "Škoda")]).ToArray());

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IVehicleUpdateService));
    }

    [Theory]
    [InlineData("Vehicle:Website:Enabled")]
    [InlineData("Vehicle:Skoda:Enabled")]
    public void A_built_in_beside_a_plugin_is_no_longer_refused(string builtIn)
    {
        // It used to be "one car, two feeds"; the built-in is gone, so the plugin is the one feed.
        var services = Services(TheCar.Concat(TheExternalProvider).Concat([(builtIn, "true")]).ToArray());

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IVehicleUpdateService));
    }

    private static readonly (string Key, string? Value)[] TheExternalProvider = [("Vehicle:External:Enabled", "true")];

    [Fact]
    public async Task An_external_provider_is_the_feed()
    {
        var services = Services(TheCar.Concat(TheExternalProvider).ToArray());

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IVehicleUpdateService));

        await using var provider = services.BuildServiceProvider();
        var configured = provider.GetRequiredService<ConfiguredVehicleFeed>();

        var feed = Assert.IsType<Gleanvolt.Hosting.Vehicles.External.ExternalVehicleFeed>(configured.Service);
        Assert.Equal("external", feed.Manufacturer);
        Assert.True(feed.IsPushed);

        // The kind is on, and listed beside the tariff and the forecast.
        var providers = provider.GetRequiredService<IExternalProviders>();
        Assert.Equal(ExternalProviderState.Waiting, providers.Find("vehicle")!.State);
    }

    [Fact]
    public async Task Without_the_external_provider_its_kind_is_listed_off()
    {
        await using var provider = Services(TheCar).BuildServiceProvider();

        Assert.Equal(ExternalProviderState.Off, provider.GetRequiredService<IExternalProviders>().Find("vehicle")!.State);
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
}
