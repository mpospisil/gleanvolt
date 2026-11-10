using Bunit;
using Microsoft.AspNetCore.Components.Web;
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
/// The manual's pictures of Settings and the Car page (#329): an installation that has been running for
/// a while, with the forecast, the weather and a night tariff set up, and a Volkswagen to sign in to.
/// The clock is fixed, so "fetched 2 hours ago" reads the same on every run.
/// </summary>
public sealed class SettingsScreenshots : ScreenshotTest
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private static readonly DateTimeOffset Now = new(2026, 6, 14, 14, 30, 0, TimeSpan.FromHours(2));

    private readonly FakePvSystemEditor _pv = new()
    {
        InverterDrivers = InverterDrivers.Known,
        ChargerDrivers = ChargerDrivers.Known,
    };

    private readonly FakeSolcastAccountEditor _solcast = new();
    private readonly FakeWeatherKeyEditor _weather = new();
    private readonly FakeTariffEditor _tariff = new();
    private readonly FakeEvEditor _car = new();

    public SettingsScreenshots()
    {
        _pv.Running[PvSystemSettingKeys.Name] = "Home Roof";
        _pv.Running[PvSystemSettingKeys.Address] = "Krásného 12, Praha";
        _pv.Running[PvSystemSettingKeys.AzimuthDegrees] = "180";
        _pv.Running[PvSystemSettingKeys.CapacityKwp] = "9.2";
        _pv.Running[PvSystemSettingKeys.InverterCapacityKw] = "8";
        _pv.Running[PvSystemSettingKeys.InverterDriver] = "solax";
        _pv.Running[PvSystemSettingKeys.InverterModel] = "X3-HYB-G4 PRO";
        _pv.Running[PvSystemSettingKeys.InverterHost] = "192.168.1.10";
        _pv.Running[PvSystemSettingKeys.InverterUnitId] = "1";
        _pv.Running[PvSystemSettingKeys.ChargerDriver] = "solax";
        _pv.Running[PvSystemSettingKeys.ChargerModel] = "X3-HAC";
        _pv.Running[PvSystemSettingKeys.ChargerHost] = "192.168.1.11";
        _pv.Running[PvSystemSettingKeys.ChargerPort] = "502";
        _pv.Running[PvSystemSettingKeys.ChargerUnitId] = "3";
        _pv.Running[PvSystemSettingKeys.TimeZone] = "Europe/Prague";
        _pv.Running[PvSystemSettingKeys.HomeBatteryCapacityKWh] = "11.6";

        _solcast.Settings = new SolcastAccountSettings(
            SolcastAccountSource.Page, "sk-7f3a9c2e41d8b605", "a1b2-c3d4-e5f6-0789", false, "an owner-only file",
            new SolcastFetchStatus(Now.AddHours(-2), null, SolcastFetchFailure.None, null));
        _weather.Settings = new WeatherKeySettings(WeatherKeySource.Page, "3f9c2e41d8b6057a", false, "an owner-only file");

        // A Czech night tariff (HDO): 22:00 to 06:00 every night, and two more hours on weekend afternoons.
        _tariff.Stored = TestTariffs.Table(new TariffTable(
            [new TariffPeriod("00:00", "06:00"), new TariffPeriod("22:00", "24:00")],
            [new TariffPeriod("00:00", "08:00"), new TariffPeriod("13:00", "15:00"), new TariffPeriod("22:00", "24:00")]));

        Services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now, Prague));
        Services.AddSingleton<IPvSystemEditor>(_pv);
        Services.AddSingleton<ISolcastAccountEditor>(_solcast);
        Services.AddSingleton<IWeatherKeyEditor>(_weather);
        Services.AddSingleton<ITariffEditor>(_tariff);
        Services.AddSingleton<ITariffSchedule>(new FakeTariffSchedule());
        Services.AddSingleton(new TariffPanelRegistration(TariffProviderIds.Table, typeof(TariffTablePanel)));
        Services.AddSingleton<IIntegrationsEditor>(new FakeIntegrationsEditor());
        Services.AddSingleton<IDeviceSnapshot>(new FakeDeviceSnapshot());
        Services.AddSingleton<IDeviceDiscovery>(new NoDiscovery());
        Services.AddSingleton<IServiceShutdown>(new FakeServiceShutdown());
        // As a Linux install describes it: the manual's main platform.
        Services.AddSingleton<ISecretStore>(new FakeSecretStore { Protection = "owner-only files (0600) in the data directory" });
        Services.AddSingleton<IEvEditor>(_car);
        Services.AddSingleton(MqttDisplayOptions.None);
        Services.AddSingleton(ApiDisplayOptions.Off);
        Services.AddSingleton(new WebBuildInfo("1.2.0 (9a48f4e)"));
        Services.AddSingleton(Options.Create(new WebOptions { PasswordHash = "hash" }));
        Services.AddSingleton(new WebPasswordState("hash"));
        SignedIn();
    }

    [Fact]
    public void The_installation_tab() =>
        Capture(RenderPage<PvSystem>("/settings"), "settings-installation", selector: "#pv-editor");

    [Fact]
    public void The_solar_forecast() =>
        Capture(RenderPage<PvSystem>("/settings"), "settings-solcast", selector: "#solcast-editor");

    [Fact]
    public void The_weather() =>
        Capture(RenderPage<PvSystem>("/settings"), "settings-weather", selector: "#weather-editor");

    [Fact]
    public void The_tariff_table() =>
        Capture(RenderPage<PvSystem>("/settings"), "settings-tariff", selector: "#tariff-editor");

    [Fact]
    public void The_health_tab()
    {
        // A controller that polled a few seconds ago, in mode Off.
        Services.GetRequiredService<ChargeControlStatusHolder>().Set(Statuses.Sample(Now.AddSeconds(-4)));

        Capture(RenderPage<Health>("/settings/health"), "settings-health", selector: Main);
    }

    [Fact]
    public void The_car_page_for_a_volkswagen()
    {
        VolkswagenCar(new FakeVehicleAccountSignIn(configured: true));

        Capture(RenderPage<Car>("/car"), "car-volkswagen", selector: "#car-editor");
    }

    [Fact]
    public void Signing_in_to_volkswagen_asks_for_the_emailed_code()
    {
        VolkswagenCar(new FakeVehicleAccountSignIn(
            configured: true, first: VehicleSignInState.CodeRequired("Volkswagen has emailed you a code.")));
        var page = RenderPage<Car>("/car");

        page.Find("#account button").Click();

        Capture(page, "car-sign-in-code", selector: "#account");
    }

    private const string Main = "main.content";

    private void VolkswagenCar(IVehicleAccountSignIn account)
    {
        _car.RunningFeed = VehicleFeed.Volkswagen;
        _car.Running[EvSettingKeys.WebsiteEnabled] = "true";
        _car.Running[EvSettingKeys.WebsiteUsername] = "owner@example.com";
        _car.Running[EvSettingKeys.WebsiteVin] = "WVGZZZE2ZPP012345";
        _car.RunningSecrets[EvSettingKeys.WebsitePassword] = true;
        _car.Secrets[EvSettingKeys.WebsitePassword] = true;

        var id4 = new EvInfo("id4", "The ID.4", "Volkswagen", "ID.4 Pro", 77_000, 0.9, 3, 6, 16, "gleanvolt/vehicle/id4/state");
        Services.AddSingleton(id4);
        Services.AddSingleton(ChargingLimits.Intersect(6, 16, 3, id4));
        Services.AddSingleton(account);
        Services.AddSingleton<IVehiclePortalReader>(new NoPortal());
        Services.AddSingleton<IVehicleStateRefresh>(new FakeVehicleStateRefresh(canRefresh: true));
        Services.AddSingleton<IVehicleTelemetry>(new VehicleStateHolder());
    }

    private sealed class NoDiscovery : IDeviceDiscovery
    {
        public DiscoveryScan? Current => null;

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public DiscoverySuggestion Suggest() => new(["192.168.1.0/24"], []);

        public DiscoveryStart Start(string? subnet) => new(false, "not in a picture");

        public void Cancel()
        {
        }
    }

    private sealed class NoPortal : IVehiclePortalReader
    {
        public string PortalName => "VW Group EU Data Act portal";

        public bool IsConfigured => false;

        public string DescribeWhatIsMissing() => string.Empty;

        public Task<VehiclePortalReading> ReadAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A picture never reads the portal.");
    }
}
