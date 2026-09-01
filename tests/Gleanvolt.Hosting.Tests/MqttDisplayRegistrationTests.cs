using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.HomeAssistant;
using Gleanvolt.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// What the composition root hands the MQTT section on <c>/pv-system</c> (issue #143).
///
/// <para>The assertion that matters is the last group: the broker password reaches the UI only when a
/// login is enforced. It is a structural guarantee rather than a rule the markup has to remember —
/// <c>MQTT_PASSWORD</c> is the account that publishes to the <c>…/set</c> topics, so on the open LAN
/// dashboard the UI is by default, printing it would hand out the stop button on the wallbox by
/// another route. The page cannot disclose what it was never given.</para>
/// </summary>
public class MqttDisplayRegistrationTests
{
    private static readonly (string Key, string? Value)[] MinimalDevices =
    [
        ("Pv:Inverter:Host", "127.0.0.1"),
        ("Pv:Chargers:0:Host", "127.0.0.1"),
    ];

    // Everything the Home Assistant link needs to be genuinely on, including the id it publishes under.
    private static readonly (string Key, string? Value)[] HomeAssistantOn =
    [
        ("Pv:Id", "home-roof"),
        ("HomeAssistant:Enabled", "true"),
        ("HomeAssistant:BrokerHost", "mosquitto"),
        ("HomeAssistant:Username", "solax"),
        ("HomeAssistant:Password", "broker-secret"),
    ];

    private static ServiceProvider Build(params (string Key, string? Value)[] settings)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(
            MinimalDevices.Concat(settings).Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)));
        builder.Services.AddGleanvolt(builder.Configuration);

        return builder.Services.BuildServiceProvider();
    }

    [Fact]
    public async Task TheSectionIsRegisteredEvenWhenNoLinkIsConfigured()
    {
        // "MQTT is off" is what the section most often has to say, so it cannot be registered behind
        // either link being on -- both are off by default.
        await using var provider = Build();

        var mqtt = provider.GetRequiredService<MqttDisplayOptions>();

        Assert.False(mqtt.AnyEnabled);
        Assert.False(mqtt.HomeAssistant.Connection.Enabled);
        Assert.False(mqtt.Vehicle.Connection.Enabled);
    }

    [Fact]
    public async Task NothingIsRegisteredWhenTheUiIsOff()
    {
        await using var provider = Build(("Web:Enabled", "false"));

        Assert.Null(provider.GetService<MqttDisplayOptions>());
    }

    [Fact]
    public async Task TheTopicsAreTheOnesTheWorkerPublishesOn()
    {
        // Not "the topics look right" but "they are the same object's": one owner of the layout is the
        // whole reason HaDiscovery exists, and a second copy of the format strings in the host would
        // pass any test written against literals.
        await using var provider = Build(HomeAssistantOn);

        var mqtt = provider.GetRequiredService<MqttDisplayOptions>();
        var discovery = provider.GetRequiredService<HaDiscovery>();

        Assert.Equal(discovery.TopicPrefix, mqtt.HomeAssistant.TopicPrefix);
        Assert.Equal(discovery.ClientId, mqtt.HomeAssistant.Connection.ClientId);
        Assert.Equal(
            discovery.WellKnownTopics().Select(topic => topic.Topic),
            mqtt.HomeAssistant.Topics.Select(topic => topic.Topic));
    }

    [Fact]
    public async Task TheDeviceIdShownIsTheOneInForce()
    {
        // Left empty, the unique-id root is the system's id. The page shows what the entities are
        // actually keyed by, not the blank that produced it.
        await using var provider = Build(HomeAssistantOn);

        Assert.Equal("home-roof", provider.GetRequiredService<MqttDisplayOptions>().HomeAssistant.DeviceId);

        await using var pinned = Build([.. HomeAssistantOn, ("HomeAssistant:DeviceId", "solax_controller")]);

        Assert.Equal("solax_controller", pinned.GetRequiredService<MqttDisplayOptions>().HomeAssistant.DeviceId);
    }

    [Fact]
    public async Task TheVehicleTopicIsTheOneTheWorkerSubscribedTo()
    {
        // The worker takes the topic from the car (#124), not from VehicleOptions.Topic -- which is a
        // retired key the host refuses outright -- so the page must take it from the same place.
        await using var provider = Build(
            ("Vehicle:Enabled", "true"),
            ("Ev:Vehicles:0:Id", "id4"),
            ("Ev:Vehicles:0:Telemetry:Topic", "gleanvolt/vehicle/id4/state"));

        var vehicle = provider.GetRequiredService<MqttDisplayOptions>().Vehicle;

        Assert.Equal(provider.GetRequiredService<EvInfo>().TelemetryTopic, vehicle.Topic);
        Assert.Equal("gleanvolt/vehicle/id4/state", vehicle.Topic);
    }

    [Fact]
    public async Task AVehicleFeedWithNoTopicIsShownAsTheMisconfigurationItIs()
    {
        await using var provider = Build(("Vehicle:Enabled", "true"));

        Assert.True(provider.GetRequiredService<MqttDisplayOptions>().Vehicle.IsEnabledWithoutTopic);
    }

    [Fact]
    public async Task TheBrokerPasswordIsWithheldFromAnOpenUi()
    {
        await using var provider = Build(HomeAssistantOn);

        var connection = provider.GetRequiredService<MqttDisplayOptions>().HomeAssistant.Connection;

        Assert.Null(connection.Password);
        Assert.True(connection.PasswordWithheld);

        // The name is not a credential and stays, so the section can still answer "as whom?".
        Assert.Equal("solax", connection.Username);
    }

    [Fact]
    public async Task TheBrokerPasswordIsHandedOverOnceALoginIsEnforced()
    {
        await using var provider = Build(
            [.. HomeAssistantOn, ("Web:PasswordHash", "a-hash"), ("Vehicle:Enabled", "true"), ("Vehicle:Password", "car-secret")]);

        var mqtt = provider.GetRequiredService<MqttDisplayOptions>();

        Assert.Equal("broker-secret", mqtt.HomeAssistant.Connection.Password);
        Assert.Equal("car-secret", mqtt.Vehicle.Connection.Password);
        Assert.False(mqtt.HomeAssistant.Connection.PasswordWithheld);
    }

    [Fact]
    public async Task AnAnonymousBrokerIsNotAWithheldPassword()
    {
        // Nothing configured must not read as "there is a password you may not see".
        await using var provider = Build(("Pv:Id", "home-roof"), ("HomeAssistant:Enabled", "true"));

        var connection = provider.GetRequiredService<MqttDisplayOptions>().HomeAssistant.Connection;

        Assert.False(connection.PasswordWithheld);
        Assert.True(connection.IsAnonymous);
    }

    [Fact]
    public async Task TwoLinksOnOneBrokerAreSaidToBeOne()
    {
        await using var shared = Build(
            [.. HomeAssistantOn, ("Vehicle:Enabled", "true"), ("Vehicle:BrokerHost", "mosquitto")]);

        Assert.True(shared.GetRequiredService<MqttDisplayOptions>().SharesOneBroker);

        await using var separate = Build(
            [.. HomeAssistantOn, ("Vehicle:Enabled", "true"), ("Vehicle:BrokerHost", "192.168.2.4")]);

        Assert.False(separate.GetRequiredService<MqttDisplayOptions>().SharesOneBroker);
    }
}
