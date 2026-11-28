using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting;
using Gleanvolt.Web.Auth;
using Gleanvolt.Web.Components;
using Gleanvolt.Web.Components.Pages;
using Gleanvolt.Web.Tests;

namespace Gleanvolt.Web.Screenshots;

/// <summary>
/// The manual's "Setting up" pictures (#329): the password page and each step of the setup wizard, Inverter, Site
/// and Finish (#442), on a fresh install that a search has just found a SolaX inverter for, and the charger beside it. The site
/// shows what an owner would have typed, so the reader sees a filled form rather than an empty one.
/// </summary>
public sealed class SetupScreenshots : ScreenshotTest
{
    private readonly FakePvSystemEditor _pv = new()
    {
        InverterDrivers = InverterDrivers.Known,
    };

    private readonly FoundDevices _discovery = new();
    private readonly WizardSetup _setup = new();

    public SetupScreenshots()
    {
        // A fresh install: none of the device addresses, the site as an owner would type it.
        _pv.Running.Clear();
        _pv.Running[PvSystemSettingKeys.Id] = "home-roof";
        _pv.Running[PvSystemSettingKeys.Name] = "Home Roof";
        _pv.Running[PvSystemSettingKeys.AzimuthDegrees] = "180";
        _pv.Running[PvSystemSettingKeys.TiltDegrees] = "35";
        _pv.Running[PvSystemSettingKeys.CapacityKwp] = "9.2";
        _pv.Running[PvSystemSettingKeys.InverterCapacityKw] = "8";
        _pv.Running[PvSystemSettingKeys.Latitude] = "50.0755";
        _pv.Running[PvSystemSettingKeys.Longitude] = "14.4378";
        _pv.Running[PvSystemSettingKeys.TimeZone] = "Europe/Prague";
        _pv.Running[PvSystemSettingKeys.HomeBatteryCapacityKWh] = "11.6";

        Services.AddSingleton<IPvSystemEditor>(_pv);
        Services.AddSingleton<IDeviceDiscovery>(_discovery);
        Services.AddSingleton<IServiceShutdown>(new FakeServiceShutdown());
        Services.AddSingleton<ISetupState>(_setup);

        // No token yet on a fresh install: Finish offers to make the one a charger plugin connects with (#442).
        var providers = new FakeExternalProviders();
        providers.Set(new ExternalProviderStatus("charger", "Charger", "/providers/charger", false, ExternalProviderState.Waiting));
        Services.AddSingleton<IExternalProviders>(providers);
        Services.AddSingleton<IEvEditor>(new FakeEvEditor());
        Services.AddSingleton<ISolcastAccountEditor>(new FakeSolcastAccountEditor());
        // The weather source (#424): nothing chosen yet on a new installation.
        Services.AddSingleton<IWeatherSourceSelection>(new FakeWeatherSourceSelection());
        Services.AddSingleton<IIntegrationsEditor>(new FakeIntegrationsEditor());
        Services.AddSingleton<ITariffEditor>(new FakeTariffEditor());
        Services.AddSingleton(new TariffPanelRegistration(TariffProviderIds.Table, typeof(TariffTablePanel)));
        Services.AddSingleton<TimeProvider>(TimeProvider.System);
        Services.AddSingleton(Options.Create(new WebOptions { PasswordHash = "hash" }));
        Services.AddSingleton(new WebPasswordState("hash"));
    }

    [Fact]
    public void The_password_page()
    {
        Services.AddSingleton(new WebPasswordState(null));
        SignedOut();

        Capture(RenderPage<Setup>("/setup"), "setup-password", selector: ".login-card");
    }

    [Fact]
    public void The_recovery_key()
    {
        // The setup page shows the key only in answer to the form's POST, which bUnit does not make;
        // this is that branch of Setup.razor, with the same component and the same card around it.
        SignedOut();
        RenderFragment card = builder =>
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "login-card");
            builder.OpenElement(2, "h1");
            builder.AddContent(3, Resources.SharedText.PasswordSet);
            builder.CloseElement();
            builder.OpenComponent<RecoveryKeyPanel>(4);
            builder.AddAttribute(5, nameof(RecoveryKeyPanel.Key), "K7QD2-M9XRT-4WBHN-P3ZLF-8VCJE");
            builder.AddAttribute(6, nameof(RecoveryKeyPanel.Intro), Resources.SharedText.RecoveryKeyIntro);
            builder.AddAttribute(7, nameof(RecoveryKeyPanel.ContinueHref), "/login");
            builder.AddAttribute(8, nameof(RecoveryKeyPanel.ContinueText), Resources.SharedText.RecoveryKeySaved);
            builder.CloseComponent();
            builder.CloseElement();
        };

        Capture(RenderPage<Fragment>("/setup", new Dictionary<string, object?> { [nameof(Fragment.ChildContent)] = card }), "setup-recovery-key", selector: ".login-card");
    }

    [Fact]
    public void The_search_for_devices()
    {
        _discovery.Finish(Inverter, Charger, Router);
        SignedIn();

        Capture(Step("inverter"), "wizard-search", selector: "#discovery");
    }

    [Fact]
    public void The_inverter_step_with_it_chosen()
    {
        _discovery.Finish(Inverter, Charger, Router);
        SignedIn();
        var page = Step("inverter");

        page.FindAll("#discovery-results tbody tr")[0].QuerySelector("button")!.Click();

        Capture(page, "wizard-inverter", selector: Main);
    }

    [Fact]
    public void The_site_step()
    {
        SignedIn();

        Capture(Step("site"), "wizard-site", selector: Main);
    }

    [Fact]
    public void The_finish()
    {
        _setup.Status = new SetupStatus([], [OptionalSetting.Tariff, OptionalSetting.Mqtt, OptionalSetting.Weather]);
        SignedIn();

        Capture(Step("finish"), "wizard-finish", selector: Main);
    }

    // The page without the menu bar: about as wide as the manual's column, so the text stays readable.
    private const string Main = "main.content";

    private IRenderedComponent<LayoutView> Step(string step) =>
        RenderPage<Wizard>("/wizard/" + step, new Dictionary<string, object?> { [nameof(Wizard.Step)] = step });

    private static readonly FoundDevice Inverter =
        new("192.168.1.10", 502, 1, PvDeviceRole.Inverter, "X3-HYB-G4 PRO", "H34A10J8123456", true, "", Driver: "solax");

    // The charger answers Modbus too; the search no longer says what it is (#445).
    private static readonly FoundDevice Charger =
        new("192.168.1.11", 502, 3, null, null, null, false, "Answers Modbus, but has no SolaX serial number and no Huawei model name: not an inverter Gleanvolt knows. A charger? Its plugin's page has a search of its own.");

    // Something else on the network that answered on a port the search tries, as a real search always finds.
    private static readonly FoundDevice Router =
        new("192.168.1.1", 80, null, null, null, null, false, "web page, not a device Gleanvolt knows");

    /// <summary>Renders its child content, so a fragment can be passed where a page is expected.</summary>
    private sealed class Fragment : ComponentBase
    {
        [Parameter] public RenderFragment? ChildContent { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) =>
            builder.AddContent(0, ChildContent);
    }

    private sealed class WizardSetup : ISetupState
    {
        public SetupStatus Status { get; set; } = new([], []);

        public bool SetupMode => true;

        public SetupStatus AtStartup => Status;

        public SetupStatus Current() => Status;
    }

    private sealed class FoundDevices : IDeviceDiscovery
    {
        public DiscoveryScan? Current { get; private set; }

        public event Action? Changed;

        public DiscoverySuggestion Suggest() => new(["192.168.1.0/24"], []);

        public DiscoveryStart Start(string? subnet) => new(false, "not in a picture");

        public void Cancel()
        {
        }

        public void Finish(params FoundDevice[] devices)
        {
            var at = new DateTimeOffset(2026, 6, 14, 10, 0, 0, TimeSpan.FromHours(2));
            Current = new DiscoveryScan(["192.168.1.0/24"], 762, 762, false, false, devices, [], at, at.AddSeconds(74));
            Changed?.Invoke();
        }
    }
}
