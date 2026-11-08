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
/// search found without assuming roles, and ends by starting the controller.
/// </summary>
public class WizardTests : PageTest
{
    private readonly FakePvSystemEditor _pv = new();
    private readonly FakeDiscovery _discovery = new();
    private readonly FakeServiceShutdown _shutdown = new();
    private readonly SwitchableSetup _setup = new();

    public WizardTests()
    {
        Services.AddSingleton<IPvSystemEditor>(_pv);
        Services.AddSingleton<IDeviceDiscovery>(_discovery);
        Services.AddSingleton<IServiceShutdown>(_shutdown);
        Services.AddSingleton<ISetupState>(_setup);
        Services.AddSingleton<IEvEditor>(new FakeEvEditor());
        Services.AddSingleton<ISolcastAccountEditor>(new FakeSolcastAccountEditor());
        Services.AddSingleton<IWeatherKeyEditor>(new FakeWeatherKeyEditor());
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
    public void The_devices_step_shows_only_the_device_fields()
    {
        var page = Step("devices");

        Assert.NotNull(page.Find(Field(PvSystemSettingKeys.InverterHost)));
        Assert.NotNull(page.Find(Field(PvSystemSettingKeys.ChargerHost)));
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
    public void An_unknown_step_starts_at_the_devices()
    {
        Assert.NotNull(Step("nonsense").Find("#discovery"));
    }

    [Fact]
    public void The_extras_step_sets_the_weather_key_home_assistant_and_the_api()
    {
        var extras = Step("extras");

        Assert.NotNull(extras.Find("#weather-api-key"));
        Assert.NotNull(extras.Find("#mqtt-brokerhost"));
        Assert.NotNull(extras.Find("#api-enabled"));
    }

    [Fact]
    public void A_search_suggests_this_machines_subnet_and_starts_on_the_button()
    {
        var page = Step("devices");

        Assert.Equal("192.168.1.0/24", page.Find("#discovery-subnet").GetAttribute("placeholder"));

        page.Find("#discovery-scan").Click();

        Assert.Equal([""], _discovery.Starts);
        Assert.Contains("Searching 192.168.1.0/24", page.Find("#discovery-progress").TextContent);
        Assert.NotNull(page.Find("#discovery-cancel"));
    }

    [Fact]
    public void Progress_and_devices_arrive_live()
    {
        var page = Step("devices");
        page.Find("#discovery-scan").Click();

        _discovery.Finish(Inverter, Charger);

        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("#discovery-results tbody tr").Count));
        Assert.Contains("2 answered", page.Find("#discovery-progress").TextContent);
    }

    [Fact]
    public void Choosing_a_found_device_fills_the_address_and_saves_nothing()
    {
        _discovery.Finish(Inverter, Charger);
        var page = Step("devices");

        page.FindAll("#discovery-results tbody tr")[0].QuerySelector("button")!.Click();   // inverter row, as inverter
        page.FindAll("#discovery-results tbody tr")[1].QuerySelectorAll("button")[1].Click(); // charger row, as charger

        Assert.Equal("192.168.1.10", page.Find(Field(PvSystemSettingKeys.InverterHost)).GetAttribute("value"));
        Assert.Equal("192.168.1.11", page.Find(Field(PvSystemSettingKeys.ChargerHost)).GetAttribute("value"));
        Assert.Equal("3", page.Find(Field(PvSystemSettingKeys.ChargerUnitId)).GetAttribute("value"));
        Assert.Empty(_pv.Saves);
        Assert.Empty(page.FindAll("#discovery-role-warning"));

        page.Find("#pv-save").Click();

        var saved = Assert.Single(_pv.Saves);
        Assert.Equal("192.168.1.10", saved[PvSystemSettingKeys.InverterHost]);
        Assert.Equal("192.168.1.11", saved[PvSystemSettingKeys.ChargerHost]);
    }

    [Fact]
    public void A_role_the_device_did_not_claim_is_allowed_but_said()
    {
        _discovery.Finish(Inverter, Charger);
        var page = Step("devices");

        // The inverter's address, chosen as the charger.
        page.FindAll("#discovery-results tbody tr")[0].QuerySelectorAll("button")[1].Click();

        Assert.Contains("identified itself as an inverter", page.Find("#discovery-role-warning").TextContent);
        Assert.Equal("192.168.1.10", page.Find(Field(PvSystemSettingKeys.ChargerHost)).GetAttribute("value"));
    }

    [Fact]
    public void A_refused_start_says_why()
    {
        _discovery.Refusal = new DiscoveryStart(false, "'8.8.8.0/24' is not a private network.");
        var page = Step("devices");

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
        Assert.NotNull(finish.QuerySelector("a[href='/wizard/devices']"));
        Assert.Contains("Solar forecast", finish.TextContent);
        Assert.Null(finish.QuerySelector("#restart"));
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
    public void Optional_steps_can_be_skipped_and_required_ones_say_next()
    {
        Assert.Contains("skip", Step("solcast").Find("#wizard-next").TextContent);
        Assert.Equal("/wizard/car", Step("solcast").Find("#wizard-next").GetAttribute("href"));
        Assert.DoesNotContain("skip", Step("devices").Find("#wizard-next").TextContent);
        Assert.Empty(Step("finish").FindAll("#wizard-next"));
    }

    [Theory]
    [InlineData("devices")]
    [InlineData("site")]
    [InlineData("extras")]
    [InlineData("finish")]
    public void Every_string_comes_from_a_resource(string step)
    {
        _discovery.Finish(Inverter, Charger);
        _setup.Status = new SetupStatus([], [OptionalSetting.Mqtt]);

        var page = PseudoLocale.Render(() => Step(step));

        // The fixture's own data: addresses, models, file names.
        PseudoLocale.AssertTranslated(
            page.Markup, "192.168.1.10", "192.168.1.11", "192.168.1.0/24", "X3-HYB-G4 PRO", "X3-HAC", ".env", "README",
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
