using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Auth;
using Gleanvolt.Web.Components;
using Gleanvolt.Web.Components.Pages;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// #302: the setup wizard is Settings in a guided order. It reuses the forms, offers what a device
/// search found, and ends by starting the controller. Since #442 it sets up only Gleanvolt itself: Inverter, Site,
/// Finish, and Finish says how to add a charger plugin.
/// </summary>
public class WizardTests : PageTest
{
    private readonly FakePvSystemEditor _pv = new();
    private readonly FakeDiscovery _discovery = new();
    private readonly FakeServiceShutdown _shutdown = new();
    private readonly SwitchableSetup _setup = new();
    private readonly FakeExternalProviders _providers = new();

    public WizardTests()
    {
        Services.AddSingleton<IPvSystemEditor>(_pv);
        Services.AddSingleton<IDeviceDiscovery>(_discovery);
        Services.AddSingleton<IServiceShutdown>(_shutdown);
        Services.AddSingleton<ISetupState>(_setup);
        Services.AddSingleton<IExternalProviders>(_providers);
        _providers.Set(new ExternalProviderStatus("charger", "Charger", "/providers/charger", false, ExternalProviderState.Waiting));
        Services.AddSingleton<IEvEditor>(new FakeEvEditor());
        Services.AddSingleton<ISolcastAccountEditor>(new FakeSolcastAccountEditor());
        Services.AddSingleton<IWeatherSourceSelection>(new FakeWeatherSourceSelection());
        Services.AddSingleton<IIntegrationsEditor>(new FakeIntegrationsEditor());
        Services.AddSingleton<ITariffEditor>(new FakeTariffEditor());
        Services.AddSingleton(new TariffPanelRegistration(TariffProviderIds.Table, typeof(TariffTablePanel)));
        Services.AddSingleton<IVehicleAccountSignIn>(new FakeVehicleAccountSignIn(configured: false));
        Services.AddSingleton<TimeProvider>(TimeProvider.System);
        Services.AddSingleton(Options.Create(new WebOptions { PasswordHash = "hash" }));
        Services.AddSingleton(new WebPasswordState("hash"));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<Wizard> Step(string step) =>
        Render<Wizard>(parameters => parameters.Add(page => page.Step, step));

    private static string Field(string key) => "#pv-" + key.Replace(':', '-');

    [Fact]
    public void The_wizard_is_inverter_site_and_finish()
    {
        var steps = Step("inverter").FindAll(".wizard-steps a").Select(link => link.GetAttribute("href"));

        Assert.Equal(["/wizard/inverter", "/wizard/site", "/wizard/finish"], steps);
    }

    [Fact]
    public void The_inverter_step_shows_only_the_inverters_fields()
    {
        var page = Step("inverter");

        Assert.NotNull(page.Find(Field(PvSystemSettingKeys.InverterHost)));
        Assert.Empty(page.FindAll(Field(PvSystemSettingKeys.ChargerHost)));
        Assert.Empty(page.FindAll(Field(PvSystemSettingKeys.Latitude)));
        Assert.Empty(page.FindAll(Field(PvSystemSettingKeys.HomeBatteryCapacityKWh)));
    }

    [Fact]
    public void The_site_step_has_the_location_the_time_zone_and_the_home_battery()
    {
        var page = Step("site");

        Assert.NotNull(page.Find(Field(PvSystemSettingKeys.Latitude)));
        Assert.NotNull(page.Find(Field(PvSystemSettingKeys.TimeZone)));
        Assert.NotNull(page.Find(Field(PvSystemSettingKeys.HomeBatteryCapacityKWh)));
        Assert.Empty(page.FindAll(Field(PvSystemSettingKeys.InverterHost)));
    }

    [Fact]
    public void An_unknown_step_starts_at_the_inverter()
    {
        Assert.NotNull(Step("nonsense").Find("#discovery"));
    }

    [Theory]
    [InlineData("devices", "#discovery")]
    [InlineData("solcast", "#wizard-finish")]
    [InlineData("car", "#wizard-finish")]
    [InlineData("extras", "#wizard-finish")]
    public void An_address_from_before_442_lands_on_the_step_that_took_it_over(string step, string expected)
    {
        Assert.NotNull(Step(step).Find(expected));
    }

    [Fact]
    public void A_search_suggests_this_machines_subnet_and_starts_on_the_button()
    {
        var page = Step("inverter");

        Assert.Equal("192.168.1.0/24", page.Find("#discovery-subnet").GetAttribute("placeholder"));

        page.Find("#discovery-scan").Click();

        Assert.Equal([""], _discovery.Starts);
        Assert.Contains("Searching 192.168.1.0/24", page.Find("#discovery-progress").TextContent);
        Assert.NotNull(page.Find("#discovery-cancel"));
    }

    [Fact]
    public void Found_devices_are_listed_but_only_offered_as_the_inverter()
    {
        var page = Step("inverter");
        page.Find("#discovery-scan").Click();

        _discovery.Finish(Inverter, Charger, Inverter with { Host = "192.168.1.30", Port = 80, UnitId = null, Role = PvDeviceRole.Charger, Driver = "wallco" });

        page.WaitForAssertion(() => Assert.Equal(3, page.FindAll("#discovery-results tbody tr").Count));
        var rows = page.FindAll("#discovery-results tbody tr");
        Assert.Single(rows[0].QuerySelectorAll("button"));
        Assert.Single(rows[1].QuerySelectorAll("button"));
        Assert.Empty(rows[2].QuerySelectorAll("button"));
        Assert.DoesNotContain(page.FindAll("button"), button => button.TextContent.Contains("charger", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Choosing_a_found_inverter_of_another_family_brings_its_driver_and_topology()
    {
        // A Huawei behind an EMMA, as the scan reports it (#324); the fake editor's second family stands in.
        var behindEmma = new FoundDevice("192.168.1.20", 502, 1, PvDeviceRole.Inverter, "SUN2000-10K-MAP0", null, true, "note",
            Driver: "acme", Topology: "emma");
        _discovery.Finish(behindEmma);
        var page = Step("inverter");

        page.Find("#discovery-results tbody tr button").Click();

        Assert.Equal("acme", page.Find(Field(PvSystemSettingKeys.InverterDriver)).GetAttribute("value"));
        Assert.Equal("emma", page.Find(Field(PvSystemSettingKeys.InverterTopology)).GetAttribute("value"));
        Assert.NotNull(page.Find("#pv-emma-one-client"));

        page.Find("#pv-save").Click();

        var saved = Assert.Single(_pv.Saves);
        Assert.Equal("acme", saved[PvSystemSettingKeys.InverterDriver]);
        Assert.Equal("emma", saved[PvSystemSettingKeys.InverterTopology]);
    }

    [Fact]
    public void Choosing_a_found_inverter_of_the_configured_family_is_no_driver_edit()
    {
        var solax = Inverter with { Driver = "solax" };
        _discovery.Finish(solax);
        var page = Step("inverter");

        page.Find("#discovery-results tbody tr button").Click();
        page.Find("#pv-save").Click();

        var saved = Assert.Single(_pv.Saves);
        Assert.False(saved.ContainsKey(PvSystemSettingKeys.InverterDriver));
        Assert.False(saved.ContainsKey(PvSystemSettingKeys.InverterTopology));
    }

    [Fact]
    public void Choosing_a_found_inverter_fills_its_address_and_saves_nothing_until_saved()
    {
        _discovery.Finish(Inverter, Charger);
        var page = Step("inverter");

        page.FindAll("#discovery-results tbody tr")[0].QuerySelector("button")!.Click();

        Assert.Equal("192.168.1.10", page.Find(Field(PvSystemSettingKeys.InverterHost)).GetAttribute("value"));
        Assert.Empty(_pv.Saves);
        Assert.Empty(page.FindAll("#discovery-role-warning"));

        page.Find("#pv-save").Click();

        var saved = Assert.Single(_pv.Saves);
        Assert.Equal("192.168.1.10", saved[PvSystemSettingKeys.InverterHost]);
        Assert.False(saved.ContainsKey(PvSystemSettingKeys.ChargerHost));
    }

    [Fact]
    public void A_charger_chosen_as_the_inverter_is_allowed_but_said()
    {
        _discovery.Finish(Inverter, Charger);
        var page = Step("inverter");

        page.FindAll("#discovery-results tbody tr")[1].QuerySelector("button")!.Click();

        Assert.Contains("identified itself as a charger", page.Find("#discovery-role-warning").TextContent);
        Assert.Equal("192.168.1.11", page.Find(Field(PvSystemSettingKeys.InverterHost)).GetAttribute("value"));
    }

    [Fact]
    public void A_refused_start_says_why()
    {
        _discovery.Refusal = new DiscoveryStart(false, "'8.8.8.0/24' is not a private network.");
        var page = Step("inverter");

        page.Find("#discovery-subnet").Change("8.8.8.0/24");
        page.Find("#discovery-scan").Click();

        Assert.Contains("not a private network", page.Find("[role=alert]").TextContent);
    }

    [Fact]
    public void The_finish_step_lists_what_is_missing_with_the_step_that_sets_it()
    {
        _setup.Status = new SetupStatus(
            [new SetupProblem(SetupArea.Inverter, "No inverter is configured. Set Pv:Inverter:Host.", Phrase.Of("Config.PvNoInverter"))],
            [OptionalSetting.Solcast]);

        var finish = Step("finish").Find("#wizard-finish");

        Assert.Contains("No inverter is configured", finish.TextContent);
        Assert.NotNull(finish.QuerySelector("a[href='/wizard/inverter']"));
        Assert.Contains("Solar forecast", finish.TextContent);
        Assert.Null(finish.QuerySelector("#restart"));
    }

    [Fact]
    public void What_left_the_wizard_is_listed_on_finish_with_links_to_where_it_lives()
    {
        _setup.Status = new SetupStatus([], [OptionalSetting.Solcast, OptionalSetting.Ev, OptionalSetting.Tariff, OptionalSetting.Mqtt, OptionalSetting.Weather]);

        var links = Step("finish").FindAll(".optional-checklist a").Select(link => link.GetAttribute("href")).ToList();

        Assert.Equal(
            ["/settings#solcast-editor", "/car#car-editor", "/settings#tariff-editor", "/settings#mqtt-editor", "/settings#weather-editor"],
            links);
    }

    [Fact]
    public void Finish_says_how_to_add_a_charger_and_makes_the_token_it_connects_with()
    {
        var page = Step("finish");
        var charger = page.Find("#wizard-charger");

        Assert.Contains("through a plugin", charger.TextContent);
        Assert.Contains("setting-up/charger", charger.QuerySelector("a")!.GetAttribute("href"));
        Assert.Empty(page.FindAll("#wizard-charger-token"));

        page.Find("#wizard-charger-make-token").Click();

        Assert.Equal(1, _providers.TokensMade);
        page.WaitForAssertion(() => Assert.NotNull(page.Find("#wizard-charger-token")));
        Assert.Empty(page.FindAll("#wizard-charger-make-token"));
    }

    [Fact]
    public void Finish_shows_a_token_that_exists()
    {
        _providers.Token = "gvp_" + new string('x', 43);

        var page = Step("finish");

        Assert.NotNull(page.Find("#wizard-charger-token"));
        Assert.Empty(page.FindAll("#wizard-charger-make-token"));
    }

    [Fact]
    public void Once_everything_required_is_set_the_finish_starts_gleanvolt()
    {
        var page = Step("finish");

        page.Find("#restart").Click();
        page.Find("#restart-confirm").Click();

        Assert.Single(_shutdown.Restarts);
    }

    [Fact]
    public void Starting_from_the_finish_lands_on_the_overview()
    {
        var page = Step("finish");

        page.Find("#restart").Click();
        page.Find("#restart-confirm").Click();

        var reload = Assert.Single(JSInterop.Invocations, call => call.Identifier == "gleanvolt.reloadAfterRestart");
        Assert.Equal("/", Assert.Single(reload.Arguments));
    }

    [Fact]
    public void A_missing_location_or_time_zone_is_recommended_but_does_not_hold_the_start_back()
    {
        _setup.Status = new SetupStatus(
            [],
            [],
            [
                new SetupProblem(SetupArea.Location, "The site's location is not set. Set Pv:Latitude and Pv:Longitude.", Phrase.Of("Config.SetupNoLocation")),
                new SetupProblem(SetupArea.TimeZone, "The site's time zone is not set. Set Controller:TimeZone (e.g. Europe/Prague).", Phrase.Of("Config.SetupNoTimeZone")),
            ]);

        var finish = Step("finish").Find("#wizard-finish");

        var recommended = finish.QuerySelector("#wizard-recommended");
        Assert.NotNull(recommended);
        Assert.Contains("location is not set", recommended.TextContent);
        Assert.Contains("time zone is not set", recommended.TextContent);
        Assert.NotNull(recommended.QuerySelector("a[href='/wizard/site']"));
        Assert.NotNull(finish.QuerySelector("#restart"));
    }

    [Fact]
    public void Every_step_but_the_last_says_next()
    {
        Assert.Equal("/wizard/site", Step("inverter").Find("#wizard-next").GetAttribute("href"));
        Assert.Equal("/wizard/finish", Step("site").Find("#wizard-next").GetAttribute("href"));
        Assert.Empty(Step("finish").FindAll("#wizard-next"));
    }

    [Theory]
    [InlineData("inverter")]
    [InlineData("site")]
    [InlineData("finish")]
    public void Every_string_comes_from_a_resource(string step)
    {
        _discovery.Finish(Inverter, Charger);
        _setup.Status = new SetupStatus([], [OptionalSetting.Mqtt]);

        var page = PseudoLocale.Render(() => Step(step));

        // The fixture's own data: addresses, models, file names.
        PseudoLocale.AssertTranslated(
            page.Markup, "192.168.1.10", "192.168.1.11", "192.168.1.0/24", "X3-HYB-G4 PRO", "X3-HAC", ".env", "README", "SolaX Acme", "SolaX Wallco",
            "502", "Home Roof", "home-roof", "SolaX X3-HYB-G4 PRO", "wallbox", "Home Assistant");
    }

    private static readonly FoundDevice Inverter =
        new("192.168.1.10", 502, 1, PvDeviceRole.Inverter, "X3-HYB-G4 PRO", "H34A10", true, "");

    private static readonly FoundDevice Charger =
        new("192.168.1.11", 502, 3, PvDeviceRole.Charger, "X3-HAC", "C31", true, "");

    private sealed class SwitchableSetup : ISetupState
    {
        public SetupStatus Status { get; set; } = new([], []);

        public bool SetupMode => true;

        public SetupStatus AtStartup => Status;

        public SetupStatus Current() => Status;
    }

    private sealed class FakeDiscovery : IDeviceDiscovery
    {
        public List<string> Starts { get; } = [];

        public DiscoveryStart? Refusal { get; set; }

        public DiscoveryScan? Current { get; private set; }

        public event Action? Changed;

        public DiscoverySuggestion Suggest() => new(["192.168.1.0/24"], []);

        public DiscoveryStart Start(string? subnet)
        {
            if (Refusal is { } refused)
            {
                return refused;
            }

            Starts.Add(subnet ?? "");
            Current = new DiscoveryScan(["192.168.1.0/24"], 762, 100, true, false, [], [], DateTimeOffset.UnixEpoch, null);
            return new DiscoveryStart(true);
        }

        public void Cancel()
        {
        }

        public void Finish(params FoundDevice[] devices)
        {
            Current = new DiscoveryScan(["192.168.1.0/24"], 762, 762, false, false, devices, [], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            Changed?.Invoke();
        }
    }
}
