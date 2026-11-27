using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Components;
using Gleanvolt.Web.Components.Pages;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// Settings, the installation (issue #111). What it has to get right is what a configuration page is
/// for: showing what the controller talks through -- MQTT and the API -- and saying plainly when
/// something is not configured. The edit form at its top (issue #204) has its own tests in
/// PvSystemEditorFormTests.
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
        double? peakPowerWatts = 9_200,
        double? inverterCapacityWatts = 8_000,
        double? lossFactor = 0.9,
        DateOnly? installDate = null) =>
        new(
            id,
            name,
            address,
            latitude,
            longitude,
            azimuth,
            tilt,
            peakPowerWatts,
            inverterCapacityWatts,
            lossFactor,
            installDate ?? new DateOnly(2024, 5, 1),
            Device("Inverter", string.Empty, "SolaX X3-HYB-G4 PRO", "192.168.2.10"));

    /// <summary>An ID.4 Pro: 77 kWh usable, three phases, 6–16 A.</summary>
    private static EvInfo Car(int? phases = 3, int? minAmps = 6, int? maxAmps = 16) => new(
        "id4", "The ID.4", "Volkswagen", "ID.4 Pro", 77_000, 0.9, phases, minAmps, maxAmps, "gleanvolt/vehicle/id4/state");

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
            new("Availability", $"{topicPrefix}/availability", false, Phrase.Of("Topic.Availability")),
            new("Status, as one JSON payload", $"{topicPrefix}/state", false, Phrase.Of("Topic.State")),
        ];

        if (batteryHold)
        {
            topics.Add(new("Battery hold", $"{topicPrefix}/battery_hold/state", false, Phrase.Of("Topic.BatteryHold")));
            topics.Add(new("Battery hold, set", $"{topicPrefix}/battery_hold/set", true, Phrase.Of("Topic.BatteryHoldSet")));
        }

        topics.Add(new("Start a targeted charge", $"{topicPrefix}/activate_target/set", true, Phrase.Of("Topic.ActivateTarget")));
        topics.Add(new("Stop the controller", $"{topicPrefix}/stop_service/set", true, Phrase.Of("Topic.StopService")));

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

    /// <summary>The API as the host composes it once it is switched on, with the keys it was given.</summary>
    private static ApiDisplayOptions Api(params ApiKeyDisplay[] keys) =>
        new(Enabled: true, "/api/v1", "/api/v1/openapi.json", 8090, keys);

    /// <summary>
    /// Renders the page into a given context. Static and context-taking because bUnit registers
    /// services once per context, so a test that compares two configurations needs two of them.
    /// </summary>
    private static IRenderedComponent<TPage> RenderIn<TPage>(
        BunitContext context,
        PvSystemInfo? site = null,
        EvInfo? car = null,
        MqttDisplayOptions? mqtt = null,
        ApiDisplayOptions? api = null,
        IExternalProviders? providers = null,
        FakeIntegrationsEditor? integrations = null,
        bool signedIn = false)
        where TPage : Microsoft.AspNetCore.Components.IComponent
    {
        context.Services.AddSingleton(site ?? Site());

        var ev = car ?? EvInfo.Unknown;
        context.Services.AddSingleton(ev);

        // Composed exactly as the host composes it: the reference install's 6-16 A on three phases,
        // narrowed by whatever the car says.
        context.Services.AddSingleton(ChargingLimits.Intersect(6, 16, 3, ev));

        // Off is the default for every one of the three control surfaces.
        context.Services.AddSingleton(mqtt ?? MqttDisplayOptions.None);
        context.Services.AddSingleton(api ?? ApiDisplayOptions.Off);

        // The edit form's dependencies, in the UI's default state: no login, so the form is read-only.
        context.Services.AddSingleton<IPvSystemEditor>(new FakePvSystemEditor());
        context.Services.AddSingleton<IDeviceSnapshot>(new FakeDeviceSnapshot());
        context.Services.AddSingleton(Options.Create(signedIn ? new WebOptions { PasswordHash = "hash" } : new WebOptions()));
        context.Services.AddSingleton(new Gleanvolt.Web.Auth.WebPasswordState(signedIn ? "hash" : null));
        context.Services.AddSingleton<IServiceShutdown>(new FakeServiceShutdown());
        context.Services.AddSingleton<ISetupState>(new NoSetupState());
        context.Services.AddSingleton(provider => new Gleanvolt.Web.Auth.WebPasswordService(
            provider.GetRequiredService<Gleanvolt.Web.Auth.WebPasswordState>(),
            new FakePasswordStore(),
            new Gleanvolt.Web.Auth.PasswordResetOptions(Path.Combine(Path.GetTempPath(), "gleanvolt-test-reset-code.txt")),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Gleanvolt.Web.Auth.WebPasswordService>.Instance));

        // The Solcast account's, with none configured.
        context.Services.AddSingleton<ISolcastAccountEditor>(new FakeSolcastAccountEditor());
        context.Services.AddSingleton(providers ?? new FakeExternalProviders());
        context.Services.AddSingleton<IIntegrationsEditor>(integrations ?? new FakeIntegrationsEditor());

        // The tariff form's, with no tariff set up.
        context.Services.AddSingleton<ITariffEditor>(new FakeTariffEditor());
        context.Services.AddSingleton(new TariffPanelRegistration(TariffProviderIds.Table, typeof(TariffTablePanel)));
        context.Services.AddSingleton<TimeProvider>(
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero), TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague")));

        return context.Render<TPage>();
    }

    private IRenderedComponent<PvSystem> Render(
        PvSystemInfo? site = null,
        EvInfo? car = null,
        MqttDisplayOptions? mqtt = null,
        ApiDisplayOptions? api = null) =>
        RenderIn<PvSystem>(this, site, car, mqtt, api);

    // The MQTT and API sections live on Settings → Integrations since the 2026-11-27 reorganization.
    private IRenderedComponent<Integrations> RenderIntegrations(
        MqttDisplayOptions? mqtt = null,
        ApiDisplayOptions? api = null) =>
        RenderIn<Integrations>(this, mqtt: mqtt, api: api);

    [Fact]
    public void The_installation_tab_holds_only_the_installation()
    {
        var page = Render();

        // The charger section shows with a charger kind registered: see the test below.
        Assert.NotEmpty(page.FindAll("#pv-editor"));
        foreach (var moved in new[] { "#solcast-editor", "#weather-editor", "#tariff-editor", "#mqtt-editor", "#api-editor" })
        {
            Assert.Empty(page.FindAll(moved));
        }
    }

    [Fact]
    public void Forecast_and_prices_holds_the_forecast_the_weather_and_the_tariff()
    {
        var page = RenderIn<ForecastPrices>(this);

        Assert.NotEmpty(page.FindAll("#solcast-editor"));
        Assert.NotEmpty(page.FindAll("#weather-editor"));
        Assert.NotEmpty(page.FindAll("#tariff-editor"));
        Assert.Empty(page.FindAll("#pv-editor"));
    }

    [Fact]
    public void Integrations_has_one_restart_banner_for_both_forms()
    {
        var editor = new FakeIntegrationsEditor();
        editor.Stored[IntegrationSettingKeys.MqttBrokerHost] = "192.168.1.5";
        editor.Keys.Add(new ApiKeyEntry("claude", "abc", SettingSource.SecretStore, IsPending: true));

        var page = RenderIn<Integrations>(this, integrations: editor);

        Assert.Contains("2", page.Find("#integrations-pending").TextContent);
        Assert.Empty(page.FindAll("#mqtt-pending"));
        Assert.Empty(page.FindAll("#api-pending"));
    }

    [Fact]
    public void A_change_saved_in_a_form_shows_in_the_banner_at_once()
    {
        var editor = new FakeIntegrationsEditor();
        editor.Keys.Add(new ApiKeyEntry("claude", "abc", SettingSource.SecretStore, IsPending: true));
        var page = RenderIn<Integrations>(this, integrations: editor, signedIn: true);
        Assert.Contains("1", page.Find("#integrations-pending").TextContent);

        editor.Stored[IntegrationSettingKeys.MqttBrokerHost] = "192.168.1.5";
        page.Find("#api-enabled").Change(true);

        Assert.Contains("3", page.Find("#integrations-pending").TextContent);
    }

    [Fact]
    public void Integrations_holds_home_assistant_and_the_api()
    {
        var page = RenderIntegrations();

        Assert.NotEmpty(page.FindAll("#mqtt-editor"));
        Assert.NotEmpty(page.FindAll("#api-editor"));
        Assert.Empty(page.FindAll("#pv-editor"));
    }

    [Fact]
    public void External_providers_are_a_tab_of_their_own_and_the_charger_has_its_own_section()
    {
        // The plugins moved from the top of the page to their own tab; the charger, a plugin's since
        // #445, keeps its section here.
        var providers = new FakeExternalProviders();
        providers.Set(new ExternalProviderStatus("charger", "Charger", "/providers/charger", HasToken: true, ExternalProviderState.Connected,
            ProviderId: "shelly-topac", ProviderName: "Shelly TopAC", ProviderVersion: "1.0",
            Since: new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero), PageUrl: new Uri("http://localhost:8097/")));

        var page = RenderIn<PvSystem>(this, providers: providers);

        Assert.Empty(page.FindAll("#external-providers"));

        var charger = page.Find("#charger-editor");
        Assert.Contains("Shelly TopAC", charger.QuerySelector("#external-charger-state")!.TextContent);
        Assert.Equal("/external/charger/page", charger.QuerySelector("#external-charger-frame")!.GetAttribute("src"));
        Assert.Equal("/external/charger/page", charger.QuerySelector("#external-charger-page-link")!.GetAttribute("href"));
        Assert.Empty(page.FindAll("#charger-source-choice"));
    }

    [Fact]
    public void Points_to_the_car_page_for_the_cars_settings_and_limits()
    {
        // The running facts the page used to repeat -- the site, the devices, the car and its limits --
        // are in the form above or on the Car page (#255).
        var page = Render(car: Car());

        Assert.Contains("/car", page.Find(".car-link a").GetAttribute("href"));
        Assert.Empty(page.FindAll("table.grid"));
    }

    [Fact]
    public void Without_a_login_offers_nothing_that_edits()
    {
        // Device addresses decide where Modbus writes go, so an open UI shows the form and lets nobody
        // use it (#204); the running view below was never editable at all.
        var page = Render();

        Assert.All(page.FindAll("input"), input => Assert.True(input.HasAttribute("disabled")));
        Assert.Empty(page.FindAll("button"));
        Assert.All(page.FindAll("select"), select => Assert.True(select.HasAttribute("disabled")));
        Assert.Contains("no login", page.Find("#pv-readonly").TextContent);
    }

    // -- MQTT (#143).

    [Fact]
    public void Says_plainly_when_neither_link_is_configured()
    {
        // Off is the default for both. Home Assistant's own form says it with its checkbox; car telemetry,
        // turned on on the Car page, says where.
        var page = RenderIntegrations();

        Assert.Empty(page.FindAll("#ha-mqtt-off"));
        Assert.Equal("/car", page.Find("#vehicle-mqtt-off a").GetAttribute("href"));
        Assert.Empty(page.FindAll("table.topics"));
    }

    [Fact]
    public void No_tab_tells_the_owner_to_edit_env_for_what_the_forms_do()
    {
        var off = RenderIntegrations().Markup;

        using var on = new BunitContext();
        var noTopic = RenderIn<Integrations>(on, mqtt: Mqtt(vehicle: Link(), vehicleTopic: "")).Markup;

        foreach (var markup in new[] { off, noTopic })
        {
            Assert.DoesNotContain("HomeAssistant__Enabled", markup);
            Assert.DoesNotContain("Vehicle__Enabled", markup);
            Assert.DoesNotContain("Ev__Vehicles__0__Telemetry__Topic", markup);
        }
    }

    [Fact]
    public void The_broker_is_shown_once()
    {
        // The form edits the broker; the running details no longer repeat it.
        var page = RenderIntegrations(mqtt: Mqtt(homeAssistant: Link()));

        Assert.DoesNotContain("mosquitto:1883", page.Find("#mqtt-editor").TextContent);
    }

    [Fact]
    public void Topics_are_folded_until_asked_for()
    {
        var page = RenderIntegrations(mqtt: Mqtt(homeAssistant: Link()));

        var topics = page.Find("#ha-topics");
        Assert.Equal("details", topics.TagName, ignoreCase: true);
        Assert.False(topics.HasAttribute("open"));
        Assert.NotNull(topics.QuerySelector("table.topics"));
        Assert.NotNull(topics.Closest("#mqtt-editor"));
    }

    [Fact]
    public void The_retiring_notice_stays_visible()
    {
        var page = RenderIntegrations(mqtt: Mqtt(homeAssistant: Link(), retiredDeviceIds: ["old_controller"]));

        Assert.Null(page.Find("#ha-mqtt-retiring").Closest("details"));
    }

    [Fact]
    public void Car_telemetry_is_a_section_of_its_own()
    {
        var page = RenderIntegrations(mqtt: Mqtt(vehicle: Link(clientId: "gleanvolt-vehicle-raspberrypi")));

        var section = page.Find("#vehicle-telemetry");
        Assert.Contains("gleanvolt/vehicle/id4/state", section.TextContent);
        Assert.Contains("gleanvolt-vehicle-raspberrypi", section.TextContent);
    }

    [Fact]
    public void Shows_the_prefix_every_topic_hangs_off()
    {
        // The one string nothing else prints: it is composed at startup from BaseTopic and Pv:Id, so it
        // is not guessable from either setting on its own.
        var page = RenderIntegrations(mqtt: Mqtt(homeAssistant: Link()));

        Assert.Contains("gleanvolt/home-roof", page.Markup);
        Assert.Contains("gleanvolt/home-roof/availability", page.Markup);
        Assert.Contains("gleanvolt/home-roof/activate_target/set", page.Markup);

        // And the pattern for everything it does not list, or the table would read as the whole surface.
        Assert.Contains("{object_id}", page.Markup);
    }

    [Fact]
    public void Marks_the_topics_that_can_change_what_the_charger_is_doing()
    {
        var page = RenderIntegrations(mqtt: Mqtt(homeAssistant: Link()));

        var rows = page.FindAll("table.topics tbody tr");
        var state = rows.Single(row => row.TextContent.Contains("gleanvolt/home-roof/state"));
        var activate = rows.Single(row => row.TextContent.Contains("activate_target/set"));

        Assert.Contains("out", state.TextContent);
        Assert.Contains("in", activate.TextContent);
    }

    [Fact]
    public void Lists_the_battery_hold_topics_only_when_the_feature_is_on()
    {
        Assert.DoesNotContain("battery_hold", RenderIntegrations(mqtt: Mqtt(homeAssistant: Link())).Markup);

        // A fresh context per render: bUnit registers services once.
        using var withHold = new BunitContext();
        var withHoldPage = RenderIn<Integrations>(withHold, mqtt: Mqtt(homeAssistant: Link(), batteryHold: true));

        Assert.Contains("gleanvolt/home-roof/battery_hold/set", withHoldPage.Markup);
    }

    [Fact]
    public void Warns_what_changing_the_device_id_costs()
    {
        var page = RenderIntegrations(mqtt: Mqtt(homeAssistant: Link()));

        Assert.Contains("solax_controller", page.Markup);
        Assert.Contains("every graph starts again", page.Markup);
    }

    [Fact]
    public void Names_the_client_id_the_broker_knows_this_controller_by()
    {
        // What the broker's connection log and its ACL file call us -- the string to search for when
        // the broker is the suspect.
        var page = RenderIntegrations(mqtt: Mqtt(
            homeAssistant: Link(),
            vehicle: Link(clientId: "gleanvolt-vehicle-raspberrypi")));

        Assert.Contains("gleanvolt-controller-home-roof", page.Markup);
        Assert.Contains("gleanvolt-vehicle-raspberrypi", page.Markup);
    }

    [Fact]
    public void Shows_the_retirement_lists_only_when_an_installation_carries_one()
    {
        Assert.Empty(RenderIntegrations(mqtt: Mqtt(homeAssistant: Link())).FindAll("#ha-mqtt-retiring"));

        using var retiring = new BunitContext();
        var retiringPage = RenderIn<Integrations>(retiring, mqtt: Mqtt(homeAssistant: Link(), retiredDeviceIds: ["old_controller"]));

        Assert.Contains("old_controller", retiringPage.Find("#ha-mqtt-retiring").TextContent);
    }

    [Fact]
    public void Shows_the_topic_the_vehicle_feed_actually_subscribed_to()
    {
        var page = RenderIntegrations(mqtt: Mqtt(vehicle: Link(clientId: "gleanvolt-vehicle-raspberrypi")));

        Assert.Contains("gleanvolt/vehicle/id4/state", page.Markup);
        Assert.Contains("12 h", page.Markup);
        Assert.Contains("30 s", page.Markup);
    }

    [Fact]
    public void Says_when_the_vehicle_feed_is_on_with_nothing_to_subscribe_to()
    {
        // The exact case the worker warns about once and then does nothing -- which from the dashboard
        // is indistinguishable from a car that never reports.
        var page = RenderIntegrations(mqtt: Mqtt(vehicle: Link(), vehicleTopic: string.Empty));

        Assert.Equal("/car", page.Find("#vehicle-mqtt-no-topic a").GetAttribute("href"));
    }

    [Fact]
    public void Says_when_the_two_links_share_one_broker_and_stays_quiet_when_they_do_not()
    {
        var page = RenderIntegrations(mqtt: Mqtt(homeAssistant: Link(), vehicle: Link()));

        Assert.Contains("mosquitto:1883", page.Find("#mqtt-one-broker").TextContent);

        using var separate = new BunitContext();
        var separatePage = RenderIn<Integrations>(separate, mqtt: Mqtt(homeAssistant: Link(), vehicle: Link(host: "192.168.2.4")));

        Assert.Empty(separatePage.FindAll("#mqtt-one-broker"));
        Assert.Contains("192.168.2.4:1883", separatePage.Markup);
    }

    [Fact]
    public void No_broker_password_reaches_an_unauthenticated_page()
    {
        // The structural half of the guarantee is the host's: with no login enforced it hands over a
        // null. This is the other half -- that a null renders as an explanation rather than as blank,
        // and that nothing secret is in the markup to be revealed by a view-source.
        var page = RenderIntegrations(mqtt: Mqtt(homeAssistant: Link(password: null), vehicle: Link(password: null)));

        Assert.DoesNotContain("broker-secret", page.Markup);
        Assert.Contains("Web__PasswordHash", page.Markup);
        Assert.Empty(page.FindAll("button"));
    }

    [Fact]
    public void A_broker_password_renders_masked_until_it_is_asked_for()
    {
        // The car telemetry link's: Home Assistant's password is the form's, and the form never shows it.
        var page = RenderIntegrations(mqtt: Mqtt(vehicle: Link(password: "broker-secret")));

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
        var page = RenderIntegrations(mqtt: Mqtt(
            vehicle: Link(username: string.Empty, password: null, hasPassword: false)));

        // The car telemetry section only: the edit forms around it name Web__PasswordHash for their own reason.
        var telemetry = page.Find("#vehicle-telemetry").InnerHtml;
        Assert.DoesNotContain("Web__PasswordHash", telemetry);
        Assert.Contains("anonymous", telemetry);
    }

    [Fact]
    public void Does_not_claim_that_a_configured_link_is_a_working_one()
    {
        // The folded details were read at startup; the form's Test connection is what checks the broker.
        var page = RenderIntegrations(mqtt: Mqtt(homeAssistant: Link()));

        Assert.Contains("As the running controller uses them", page.Find("#ha-topics").TextContent);
    }

    // -- The API (#142).

    /// <summary>The form's editor with the given keys, as saved here.</summary>
    private static FakeIntegrationsEditor WithKeys(params (string Name, string Key)[] keys)
    {
        var editor = new FakeIntegrationsEditor();
        editor.Keys.AddRange(keys.Select(key => new ApiKeyEntry(key.Name, key.Key, SettingSource.SecretStore, IsPending: false)));
        return editor;
    }

    [Fact]
    public void With_the_api_off_shows_no_address_and_no_example()
    {
        // The form's checkbox says it is off; nothing describes a surface that is not there, and nothing
        // tells the owner to edit .env for what the checkbox does.
        var page = RenderIntegrations();

        Assert.Empty(page.FindAll("#api-example"));
        Assert.Empty(page.FindAll("#api-off"));
        Assert.DoesNotContain("openapi.json", page.Markup);
        Assert.DoesNotContain("Api__Enabled", page.Markup);
    }

    [Fact]
    public void Shows_where_the_api_is_and_links_to_what_answers_without_a_key()
    {
        var page = RenderIntegrations(api: Api());

        var form = page.Find("#api-editor");

        // Absolute, built from the address the browser used -- bUnit's is http://localhost/.
        Assert.Contains("http://localhost/api/v1", form.TextContent);

        var links = form.QuerySelectorAll("a").Select(link => link.GetAttribute("href")).ToList();
        Assert.Contains("http://localhost/api/v1", links);
        Assert.Contains("http://localhost/api/v1/openapi.json", links);
    }

    [Fact]
    public void States_the_configured_port_when_the_address_it_arrived_on_carries_none()
    {
        // A proxy answering on 80 leaves no port in the URL, and the direct LAN address is still
        // host:8090. bUnit's base URI has no port, which is exactly that case.
        var page = RenderIntegrations(api: Api());

        Assert.Contains("host:8090", page.Find("#api-editor").TextContent);
    }

    [Fact]
    public void One_keys_table()
    {
        var page = RenderIn<Integrations>(this, api: Api(), integrations: WithKeys(("claude-mcp", "k1"), ("dashboard", "k2")));

        var table = Assert.Single(page.FindAll("table"), table => table.QuerySelector("th")?.TextContent == "Name"
            && table.TextContent.Contains("claude-mcp"));
        Assert.Contains("dashboard", table.TextContent);
        Assert.NotNull(table.Closest("#api-editor"));
    }

    [Fact]
    public void No_api_key_reaches_an_unauthenticated_page()
    {
        // Signed out, nothing secret may be in the markup at all -- not masked, not in an attribute, not
        // in the example call.
        var page = RenderIn<Integrations>(this, api: Api(), integrations: WithKeys(("client", "a-real-key")));

        Assert.DoesNotContain("a-real-key", page.Markup);
        Assert.Contains("Web__PasswordHash", page.Find("#api-readonly").TextContent);

        // The example call still has to be useful, so the key is a placeholder rather than absent.
        Assert.Contains("Bearer &lt;key&gt;", page.Find("#api-example").InnerHtml);
    }

    [Fact]
    public void An_api_key_renders_masked_until_it_is_asked_for()
    {
        var page = RenderIn<Integrations>(this, api: Api(), integrations: WithKeys(("client", "a-real-key")), signedIn: true);

        Assert.DoesNotContain("a-real-key", page.Markup);

        page.Find("#api-keys").QuerySelectorAll("button").First(button => button.TextContent == "Reveal").Click();

        Assert.Contains("a-real-key", page.Find("#api-keys").TextContent);
    }

    [Fact]
    public void The_example_call_carries_this_installations_own_address()
    {
        // A line that can be pasted into a terminal on another machine and work, folded until wanted.
        var page = RenderIn<Integrations>(this, api: Api(), integrations: WithKeys(("client", "a-real-key")), signedIn: true);

        var example = page.Find("#api-example");
        Assert.Equal("details", example.TagName, ignoreCase: true);
        Assert.False(example.HasAttribute("open"));

        var call = example.QuerySelector("p.code-line")!.TextContent;
        Assert.Contains("curl -H \"Authorization: Bearer", call);
        Assert.Contains("http://localhost/api/v1/status", call);

        // Masked in the line, whole when copied -- so the command stays a command.
        Assert.DoesNotContain("a-real-key", call);
    }

    [Fact]
    public void Every_string_on_the_installation_page_comes_from_a_resource()
    {
        var page = PseudoLocale.Render(() => Render());

        PseudoLocale.AssertTranslated(
            page.Markup,
            "SolaX Acme", "SolaX Wallco"); // the brands in the inverter and charger drop-downs: names, never translated
    }

    [Fact]
    public void Every_string_on_the_forecast_and_prices_page_comes_from_a_resource()
    {
        var page = PseudoLocale.Render(() => RenderIn<ForecastPrices>(this));

        PseudoLocale.AssertTranslated(page.Markup);
    }

    [Fact]
    public void Every_string_on_the_integrations_page_comes_from_a_resource()
    {
        var page = PseudoLocale.Render(() => RenderIntegrations(
            mqtt: Mqtt(homeAssistant: Link(), vehicle: Link(), batteryHold: true, retiredDeviceIds: ["solax_old"]),
            api: Api()));

        // Only the configuration's own values: host names, ids, topic paths and the example command.
        PseudoLocale.AssertTranslated(
            page.Markup,
            "mosquitto solax gleanvolt controller home roof homeassistant solax_controller solax_old vehicle id4 state",
            "curl Authorization Bearer http localhost status api openapi json home assistant availability set");
    }
}
