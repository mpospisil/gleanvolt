using System.Text.Json;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.HomeAssistant;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// Which name goes where, once a PV system has one (issue #111).
///
/// <para>The distinction these tests exist to hold: <b>the system id namespaces the topics</b>, so two
/// installations can share a broker, while <b>the unique-id root does not follow it</b>, because Home
/// Assistant keys an entity's identity — and therefore its history — to <c>unique_id</c> alone. Getting
/// the first wrong costs two systems overwriting each other; getting the second wrong costs every graph
/// on the dashboard starting again, silently, on the next restart.</para>
/// </summary>
public class HaSystemTopicTests
{
    // What every existing deployment looks like after this change: topics namespaced by the system,
    // and the unique-id root pinned to the value Home Assistant already knows the entities by.
    private static HomeAssistantOptions Pinned(
        string deviceId = "solax_controller",
        string[]? retireDeviceIds = null,
        string[]? retireTopicPrefixes = null) =>
        new()
        {
            BaseTopic = "gleanvolt",
            DeviceId = deviceId,
            DiscoveryPrefix = "homeassistant",
            RetireDeviceIds = retireDeviceIds ?? [],
            RetireTopicPrefixes = retireTopicPrefixes ?? [],
        };

    private static JsonElement ConfigFor(HaDiscovery discovery, string topicEnding) =>
        JsonSerializer.Deserialize<JsonElement>(
            discovery.DiscoveryMessages().Single(message => message.Topic.EndsWith(topicEnding, StringComparison.Ordinal)).Payload);

    [Fact]
    public void EveryTopicIsNamespacedByTheSystemId()
    {
        var discovery = new HaDiscovery(Pinned(), Sites.Home);

        Assert.Equal("gleanvolt/home-roof", discovery.TopicPrefix);
        Assert.Equal("gleanvolt/home-roof/availability", discovery.AvailabilityTopic);
        Assert.Equal("gleanvolt/home-roof/state", discovery.StateTopic);
        Assert.Equal("gleanvolt/home-roof/battery_hold/set", discovery.BatteryHoldCommandTopic);
        Assert.Equal("gleanvolt/home-roof/stop_service/set", discovery.StopServiceCommandTopic);
        Assert.Equal("gleanvolt/home-roof/start_solar/set", discovery.ButtonCommandTopic("start_solar"));
        Assert.Equal("gleanvolt/home-roof/daily_ev_target/state", discovery.NumberStateTopic("daily_ev_target"));
    }

    [Fact]
    public void TwoSystemsOnOneBrokerShareNoTopic()
    {
        // The whole point of the exercise. Before this, both published to solax/solax_controller/state
        // and the second one to connect simply overwrote the first, entity for entity.
        var first = new HaDiscovery(Pinned(), Sites.At("home-roof", "Home Roof"));
        var second = new HaDiscovery(Pinned(), Sites.At("barn-roof", "Barn Roof"));

        Assert.NotEqual(first.StateTopic, second.StateTopic);
        Assert.NotEqual(first.AvailabilityTopic, second.AvailabilityTopic);
        Assert.Empty(first.DiscoveryMessages()
            .Select(message => Topics(message.Payload))
            .SelectMany(topics => topics)
            .Intersect(second.DiscoveryMessages().SelectMany(message => Topics(message.Payload))));
    }

    [Fact]
    public void TheUniqueIdDoesNotFollowTheSystemId()
    {
        // Home Assistant treats a changed unique_id as a different entity: a new sensor.…_2, and the
        // history left behind on the old id. It is the one value here that a rename must not touch,
        // which is why it has its own setting rather than deriving from the system.
        var discovery = new HaDiscovery(Pinned(), Sites.Home);

        var config = ConfigFor(discovery, "/battery_soc/config");

        Assert.Equal("solax_controller_battery_soc", config.GetProperty("unique_id").GetString());
    }

    [Fact]
    public void TheDiscoveryTopicDoesNotFollowTheSystemIdEither()
    {
        // Same reasoning, one step out: a config republished on a *different* discovery topic reaches
        // Home Assistant as a second entity claiming an existing unique_id, which it refuses. Keeping
        // the node id put is what lets the topics below it move at all.
        var discovery = new HaDiscovery(Pinned(), Sites.Home);

        Assert.All(
            discovery.DiscoveryMessages().Select(message => message.Topic),
            topic => Assert.Contains("/solax_controller/", topic));
    }

    [Fact]
    public void AnUnsetDeviceIdTakesTheSystemId()
    {
        // What a system being set up today gets: one id, configured once. An installation that already
        // has history in Home Assistant pins the id it was created with instead -- see the compose file.
        var discovery = new HaDiscovery(Pinned(deviceId: ""), Sites.Home);

        var config = ConfigFor(discovery, "/battery_soc/config");

        Assert.Equal("home-roof_battery_soc", config.GetProperty("unique_id").GetString());
        Assert.Contains("/home-roof/", discovery.DiscoveryMessages().First().Topic);
    }

    [Fact]
    public void TheDevicePageIsNamedAfterTheSystem()
    {
        var discovery = new HaDiscovery(Pinned(), Sites.Home);

        var device = ConfigFor(discovery, "/battery_soc/config").GetProperty("device");

        Assert.Equal("Home Roof", device.GetProperty("name").GetString());

        // The identifier stays the unique-id root: an entity may move between devices without losing
        // anything, but only because its unique_id did not move, and keeping the two together is what
        // makes that easy to reason about.
        Assert.Equal("solax_controller", device.GetProperty("identifiers")[0].GetString());
    }

    [Fact]
    public void AFormerDeviceIdHasEveryConfigItLeftBehindBlanked()
    {
        // Retained discovery messages outlive the process that published them. Without this, a renamed
        // installation comes back as two devices after the next restart: the live one, and yesterday's,
        // re-created from the broker and permanently unavailable.
        var discovery = new HaDiscovery(Pinned(retireDeviceIds: ["old_controller"]), Sites.Home);

        var retired = discovery.RetiredDiscoveryTopics().ToList();
        var published = discovery.DiscoveryMessages().Select(message => message.Topic).ToList();

        Assert.All(published, topic =>
            Assert.Contains(topic.Replace("/solax_controller/", "/old_controller/"), retired));

        // And nothing live is retired by mistake.
        Assert.Empty(retired.Intersect(published));
    }

    [Fact]
    public void AFormerTopicPrefixHasItsRetainedStateCleared()
    {
        var discovery = new HaDiscovery(
            Pinned(retireTopicPrefixes: ["solax/solax_controller/"]),
            Sites.Home);

        var retired = discovery.RetiredStateTopics().ToList();

        Assert.Contains("solax/solax_controller/availability", retired);
        Assert.Contains("solax/solax_controller/state", retired);
        Assert.Contains("solax/solax_controller/battery_hold/state", retired);
        Assert.Contains("solax/solax_controller/target_departure/state", retired);
        Assert.Contains("solax/solax_controller/daily_ev_target/state", retired);

        // A command topic is published by Home Assistant, never retained by us; clearing one would be
        // claiming to own something we do not.
        Assert.DoesNotContain(retired, topic => topic.EndsWith("/set", StringComparison.Ordinal));
    }

    [Fact]
    public void NothingIsRetiredWhenNothingWasRenamed()
    {
        var discovery = new HaDiscovery(Pinned(), Sites.Home);

        Assert.Empty(discovery.RetiredStateTopics());
    }

    private static IEnumerable<string> Topics(string payload)
    {
        var config = JsonSerializer.Deserialize<JsonElement>(payload);

        foreach (var name in (string[])["state_topic", "command_topic", "availability_topic"])
        {
            if (config.TryGetProperty(name, out var topic) && topic.GetString() is { } value)
            {
                yield return value;
            }
        }
    }
}
