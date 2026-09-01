using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Components.Pages;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The read-only view of the installation (issue #111). What it has to get right is what a
/// configuration page is for: showing what is actually configured, saying plainly when something is
/// not, and never implying that anything on it can be edited here.
/// </summary>
public class PvSystemPageTests : BunitContext
{
    private static PvDeviceInfo Device(string id, string name, string model, string host, int port = 502) =>
        new(id, name, model, new DeviceConfig { Host = host, Port = port });

    private static PvSystemInfo Site(
        string id = "home-roof",
        string name = "Home Roof",
        string address = "Krásného 12, Praha",
        double? latitude = 50.0755,
        double? longitude = 14.4378,
        double? azimuth = 172,
        double? tilt = 35,
        double? capacityKwp = 9.2,
        double? inverterCapacityKw = 8,
        double? lossFactor = 0.9,
        DateOnly? installDate = null,
        IReadOnlyList<PvDeviceInfo>? chargers = null) =>
        new(
            id,
            name,
            address,
            latitude,
            longitude,
            azimuth,
            tilt,
            capacityKwp,
            inverterCapacityKw,
            lossFactor,
            installDate ?? new DateOnly(2024, 5, 1),
            Device("Inverter", string.Empty, "SolaX X3-HYB-G4 PRO", "192.168.2.10"),
            chargers ?? [Device("wallbox", "Garage wallbox", "SolaX X3-HAC", "192.168.2.6")]);

    /// <summary>An ID.4 Pro: 77 kWh usable, three phases, 6–16 A.</summary>
    private static EvInfo Car(int? phases = 3, int? minAmps = 6, int? maxAmps = 16) => new(
        "id4", "The ID.4", "Volkswagen", "ID.4 Pro", 77, 0.9, phases, minAmps, maxAmps, "gleanvolt/vehicle/id4/state");

    /// <summary>
    /// A configured MQTT link, as the host composes one. <paramref name="password"/> is what the host
    /// decides: null stands for "a password is set and this page may not print it", which is what an
    /// unauthenticated UI is handed.
    /// </summary>
    private static MqttConnectionDisplay Link(
        string host = "mosquitto",
        int port = 1883,
        string username = "solax",
        string? password = null,
        bool hasPassword = true,
        string clientId = "gleanvolt-controller-home-roof") =>
        new(Enabled: true, host, port, username, password, hasPassword, clientId);

    private static MqttDisplayOptions Mqtt(
        MqttConnectionDisplay? homeAssistant = null,
        MqttConnectionDisplay? vehicle = null,
        string topicPrefix = "gleanvolt/home-roof",
        bool batteryHold = false,
        string vehicleTopic = "gleanvolt/vehicle/id4/state",
        IReadOnlyList<string>? retiredDeviceIds = null)
    {
        // The same shape HaDiscovery.WellKnownTopics() produces, battery hold included only when the
        // feature is on -- which is the one thing about the list that varies.
        List<MqttTopicDisplay> topics =
        [
            new("Availability", $"{topicPrefix}/availability", false),
            new("Status, as one JSON payload", $"{topicPrefix}/state", false),
        ];

        if (batteryHold)
        {
            topics.Add(new("Battery hold", $"{topicPrefix}/battery_hold/state", false));
            topics.Add(new("Battery hold, set", $"{topicPrefix}/battery_hold/set", true));
        }

        topics.Add(new("Start a targeted charge", $"{topicPrefix}/activate_target/set", true));
        topics.Add(new("Stop the controller", $"{topicPrefix}/stop_service/set", true));

        return new MqttDisplayOptions(
            new HomeAssistantMqttDisplay(
                homeAssistant ?? MqttConnectionDisplay.Off,
                DiscoveryPrefix: "homeassistant",
                DeviceId: "solax_controller",
                TopicPrefix: topicPrefix,
                Topics: homeAssistant is null ? [] : topics,
                StatusInterval: TimeSpan.FromSeconds(15),
                RetiredDeviceIds: retiredDeviceIds),
            new VehicleMqttDisplay(
                vehicle ?? MqttConnectionDisplay.Off,
                Topic: vehicleTopic,
                MaxAge: TimeSpan.FromHours(12),
                ReconnectInterval: TimeSpan.FromSeconds(30)));
    }

    private IRenderedComponent<PvSystem> Render(
        PvSystemInfo? site = null, EvInfo? car = null, MqttDisplayOptions? mqtt = null)
    {
        Services.AddSingleton(site ?? Site());

        var ev = car ?? EvInfo.Unknown;
        Services.AddSingleton(ev);

        // Composed exactly as the host composes it: the reference install's 6-16 A on three phases,
        // narrowed by whatever the car says.
        Services.AddSingleton(ChargingLimits.Intersect(6, 16, 3, ev));

        // Both links off is the default an installation that speaks no MQTT is handed.
        Services.AddSingleton(mqtt ?? MqttDisplayOptions.None);

        return Render<PvSystem>();
    }

    [Fact]
    public void Shows_what_the_system_is_called_and_where_it_is()
    {
        var page = Render();

        Assert.Contains("Home Roof", page.Markup);
        Assert.Contains("home-roof", page.Markup);
        Assert.Contains("Krásného 12, Praha", page.Markup);
        Assert.Contains("50.0755, 14.4378", page.Markup);
    }

    [Fact]
    public void Spells_out_which_way_the_array_faces()
    {
        // 172° is only checkable by someone who already thinks in degrees; "south" is checkable by
        // anyone who has stood in the garden, and this is the field that goes silently wrong.
        var page = Render();

        Assert.Contains("172° (south)", page.Markup);
    }

    [Theory]
    [InlineData(0, "north")]
    [InlineData(90, "east")]
    [InlineData(225, "south-west")]
    [InlineData(359, "north")]
    public void Names_every_bearing_by_its_nearest_point(double bearing, string expected)
    {
        var page = Render(Site(azimuth: bearing));

        Assert.Contains($"({expected})", page.Markup);
    }

    [Fact]
    public void Shows_the_array_figures_with_their_units()
    {
        var page = Render();

        Assert.Contains("9.2 kWp", page.Markup);
        Assert.Contains("8 kW", page.Markup);
        Assert.Contains("0.90", page.Markup);
        Assert.Contains("2024-05-01", page.Markup);
        Assert.Contains("35°", page.Markup);
    }

    [Fact]
    public void Lists_the_devices_with_their_models_and_addresses()
    {
        var page = Render();

        Assert.Contains("SolaX X3-HYB-G4 PRO", page.Markup);
        Assert.Contains("192.168.2.10:502", page.Markup);
        Assert.Contains("Garage wallbox", page.Markup);
        Assert.Contains("SolaX X3-HAC", page.Markup);
        Assert.Contains("192.168.2.6:502", page.Markup);
        Assert.Contains("wallbox", page.Markup);
    }

    [Fact]
    public void An_unset_value_reads_as_unset_rather_than_as_zero()
    {
        // The installation that has not been described yet -- which is every installation the moment
        // before someone describes it. Zeroes here would be a site in the Atlantic facing north.
        var page = Render(Site(
            id: string.Empty,
            name: string.Empty,
            address: string.Empty,
            latitude: null,
            longitude: null,
            azimuth: null,
            tilt: null,
            capacityKwp: null,
            inverterCapacityKw: null,
            lossFactor: null));

        Assert.DoesNotContain("0.0000", page.Markup);
        Assert.DoesNotContain("(north)", page.Markup);
        Assert.Contains("—", page.Markup);
    }

    [Fact]
    public void Says_how_to_claim_an_id_when_there_is_none()
    {
        var page = Render(Site(id: string.Empty));

        Assert.Contains("Pv__Id", page.Markup);
    }

    [Fact]
    public void Says_what_no_coordinates_costs()
    {
        var page = Render(Site(latitude: null, longitude: null));

        Assert.Contains("no weather is recorded", page.Markup);
        Assert.Contains("Pv__Latitude", page.Markup);
    }

    [Fact]
    public void Offers_nothing_to_click()
    {
        // Read-only is the point: everything here was resolved once at startup, and the Modbus clients
        // were built from it. A control that edited it would be editing a copy of a settled decision.
        var page = Render();

        Assert.Empty(page.FindAll("button"));
        Assert.Empty(page.FindAll("input"));
        Assert.Empty(page.FindAll("select"));
        Assert.Contains("restarting the controller", page.Markup);
    }

    // -- The car (#124).

    [Fact]
    public void Says_plainly_when_no_car_has_been_described()
    {
        var page = Render();

        Assert.Contains("No car is described", page.Find("#no-vehicle").TextContent);
        Assert.Contains("Ev:Vehicles:0", page.Find("#no-vehicle").TextContent);
    }

    [Fact]
    public void Shows_what_the_car_is_and_what_its_pack_holds()
    {
        var page = Render(car: Car());

        Assert.Contains("The ID.4", page.Markup);
        Assert.Contains("Volkswagen ID.4 Pro", page.Markup);
        Assert.Contains("77 kWh", page.Markup);
    }

    [Fact]
    public void Shows_the_car_the_installation_and_which_of_them_is_in_effect()
    {
        // The third column is the interesting one: a page showing only the car's figures would leave
        // somebody wondering why a 32 A car charges at 16.
        var page = Render(car: Car(phases: 1, minAmps: 8, maxAmps: 32));
        var markup = page.Markup;

        Assert.Contains("In effect", markup);

        // The car refuses below 8; the installation gives out at 16; the car can only use one phase.
        var row = page.FindAll("table.grid tbody tr").Last();
        Assert.Contains("1", row.TextContent);
    }

    [Fact]
    public void Explains_that_a_car_can_only_ever_lower_a_limit()
    {
        var page = Render(car: Car());

        Assert.Contains("narrower of the two", page.Markup);
        Assert.Contains("only ever", page.Markup);
    }

    // -- MQTT (#143).

    [Fact]
    public void Says_plainly_when_neither_link_is_configured()
    {
        // Off is the default for both, so it has to read as a deliberate default rather than a fault --
        // and the page has to say what turns each one on.
        var page = Render();

        Assert.Contains("HomeAssistant__Enabled", page.Find("#ha-mqtt-off").TextContent);
        Assert.Contains("Vehicle__Enabled", page.Find("#vehicle-mqtt-off").TextContent);
        Assert.Empty(page.FindAll("table.topics"));
    }

    [Fact]
    public void Shows_the_prefix_every_topic_hangs_off()
    {
        // The one string nothing else prints: it is composed at startup from BaseTopic and Pv:Id, so it
        // is not guessable from either setting on its own.
        var page = Render(mqtt: Mqtt(homeAssistant: Link()));

        Assert.Contains("gleanvolt/home-roof", page.Markup);
        Assert.Contains("gleanvolt/home-roof/availability", page.Markup);
        Assert.Contains("gleanvolt/home-roof/activate_target/set", page.Markup);

        // And the pattern for everything it does not list, or the table would read as the whole surface.
        Assert.Contains("{object_id}", page.Markup);
    }

    [Fact]
    public void Marks_the_topics_that_can_change_what_the_charger_is_doing()
    {
        var page = Render(mqtt: Mqtt(homeAssistant: Link()));

        var rows = page.FindAll("table.topics tbody tr");
        var state = rows.Single(row => row.TextContent.Contains("gleanvolt/home-roof/state"));
        var activate = rows.Single(row => row.TextContent.Contains("activate_target/set"));

        Assert.Contains("out", state.TextContent);
        Assert.Contains("in", activate.TextContent);
    }

    [Fact]
    public void Lists_the_battery_hold_topics_only_when_the_feature_is_on()
    {
        Assert.DoesNotContain("battery_hold", Render(mqtt: Mqtt(homeAssistant: Link())).Markup);

        // A fresh context per render: bUnit registers services once.
        using var withHold = new BunitContext();
        withHold.Services.AddSingleton(Site());
        withHold.Services.AddSingleton(EvInfo.Unknown);
        withHold.Services.AddSingleton(ChargingLimits.Intersect(6, 16, 3, EvInfo.Unknown));
        withHold.Services.AddSingleton(Mqtt(homeAssistant: Link(), batteryHold: true));

        Assert.Contains("gleanvolt/home-roof/battery_hold/set", withHold.Render<PvSystem>().Markup);
    }

    [Fact]
    public void Warns_what_changing_the_device_id_costs()
    {
        var page = Render(mqtt: Mqtt(homeAssistant: Link()));

        Assert.Contains("solax_controller", page.Markup);
        Assert.Contains("every graph starts again", page.Markup);
    }

    [Fact]
    public void Names_the_client_id_the_broker_knows_this_controller_by()
    {
        // What the broker's connection log and its ACL file call us -- the string to search for when
        // the broker is the suspect.
        var page = Render(mqtt: Mqtt(
            homeAssistant: Link(),
            vehicle: Link(clientId: "gleanvolt-vehicle-raspberrypi")));

        Assert.Contains("gleanvolt-controller-home-roof", page.Markup);
        Assert.Contains("gleanvolt-vehicle-raspberrypi", page.Markup);
    }

    [Fact]
    public void Shows_the_retirement_lists_only_when_an_installation_carries_one()
    {
        Assert.Empty(Render(mqtt: Mqtt(homeAssistant: Link())).FindAll("#ha-mqtt-retiring"));

        using var retiring = new BunitContext();
        retiring.Services.AddSingleton(Site());
        retiring.Services.AddSingleton(EvInfo.Unknown);
        retiring.Services.AddSingleton(ChargingLimits.Intersect(6, 16, 3, EvInfo.Unknown));
        retiring.Services.AddSingleton(Mqtt(homeAssistant: Link(), retiredDeviceIds: ["old_controller"]));

        Assert.Contains("old_controller", retiring.Render<PvSystem>().Find("#ha-mqtt-retiring").TextContent);
    }

    [Fact]
    public void Shows_the_topic_the_vehicle_feed_actually_subscribed_to()
    {
        var page = Render(car: Car(), mqtt: Mqtt(vehicle: Link(clientId: "gleanvolt-vehicle-raspberrypi")));

        Assert.Contains("gleanvolt/vehicle/id4/state", page.Markup);
        Assert.Contains("12 h", page.Markup);
        Assert.Contains("30 s", page.Markup);
    }

    [Fact]
    public void Says_when_the_vehicle_feed_is_on_with_nothing_to_subscribe_to()
    {
        // The exact case the worker warns about once and then does nothing -- which from the dashboard
        // is indistinguishable from a car that never reports.
        var page = Render(mqtt: Mqtt(vehicle: Link(), vehicleTopic: string.Empty));

        Assert.Contains(
            "Ev__Vehicles__0__Telemetry__Topic", page.Find("#vehicle-mqtt-no-topic").TextContent);
    }

    [Fact]
    public void Says_when_the_two_links_share_one_broker_and_stays_quiet_when_they_do_not()
    {
        var page = Render(mqtt: Mqtt(homeAssistant: Link(), vehicle: Link()));

        Assert.Contains("mosquitto:1883", page.Find("#mqtt-one-broker").TextContent);

        using var separate = new BunitContext();
        separate.Services.AddSingleton(Site());
        separate.Services.AddSingleton(EvInfo.Unknown);
        separate.Services.AddSingleton(ChargingLimits.Intersect(6, 16, 3, EvInfo.Unknown));
        separate.Services.AddSingleton(Mqtt(homeAssistant: Link(), vehicle: Link(host: "192.168.2.4")));

        var rendered = separate.Render<PvSystem>();
        Assert.Empty(rendered.FindAll("#mqtt-one-broker"));
        Assert.Contains("192.168.2.4:1883", rendered.Markup);
    }

    [Fact]
    public void No_broker_password_reaches_an_unauthenticated_page()
    {
        // The structural half of the guarantee is the host's: with no login enforced it hands over a
        // null. This is the other half -- that a null renders as an explanation rather than as blank,
        // and that nothing secret is in the markup to be revealed by a view-source.
        var page = Render(mqtt: Mqtt(homeAssistant: Link(password: null), vehicle: Link(password: null)));

        Assert.DoesNotContain("broker-secret", page.Markup);
        Assert.Contains("Web__PasswordHash", page.Markup);
        Assert.Empty(page.FindAll("button"));
    }

    [Fact]
    public void A_broker_password_renders_masked_until_it_is_asked_for()
    {
        var page = Render(mqtt: Mqtt(homeAssistant: Link(password: "broker-secret")));

        // Behind a login is not behind a closed door: a shoulder and a screenshot are a different
        // threat from the network, so it starts hidden.
        Assert.DoesNotContain("broker-secret", page.Markup);
        Assert.Contains("•", page.Markup);

        page.FindAll("button").Single(button => button.TextContent == "Reveal").Click();

        Assert.Contains("broker-secret", page.Markup);
        Assert.Contains("Copy", page.Markup);
    }

    [Fact]
    public void An_anonymous_broker_does_not_read_as_a_withheld_password()
    {
        var page = Render(mqtt: Mqtt(
            homeAssistant: Link(username: string.Empty, password: null, hasPassword: false)));

        Assert.DoesNotContain("Web__PasswordHash", page.Markup);
        Assert.Contains("anonymous", page.Markup);
    }

    [Fact]
    public void Does_not_claim_that_a_configured_link_is_a_working_one()
    {
        // Everything here was read at startup; nothing on this page knows whether the broker answered.
        var page = Render(mqtt: Mqtt(homeAssistant: Link()));

        Assert.Contains("whether either link is", page.Markup);
    }
}
