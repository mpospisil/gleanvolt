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
    };

    private readonly FakeSolcastAccountEditor _solcast = new();
    // With the External strategy too, as the controller registers it (#401).
    private readonly FakeTariffEditor _tariff = new(new Gleanvolt.Core.Strategies.ExternalTariffProvider());
    private readonly FakeExternalProviders _providers = new();
    // Solcast, with External on offer, as the controller registers them (#388).
    private readonly FakeSolarForecastSourceSelection _forecastSources = new();
    // No weather until a case chooses it (#425).
    private readonly FakeWeatherSourceSelection _weatherSources = new();
    private readonly FakeEvEditor _car = new();
    private readonly FakeIntegrationsEditor _integrations = new();

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
        _pv.Running[PvSystemSettingKeys.TimeZone] = "Europe/Prague";
        _pv.Running[PvSystemSettingKeys.HomeBatteryCapacityKWh] = "11.6";

        _solcast.Settings = new SolcastAccountSettings(
            SolcastAccountSource.Page, "sk-7f3a9c2e41d8b605", "a1b2-c3d4-e5f6-0789", false, "an owner-only file",
            new SolcastFetchStatus(Now.AddHours(-2), null, SolcastFetchFailure.None, null));

        // A Czech night tariff (HDO): 22:00 to 06:00 every night, and two more hours on weekend afternoons.
        _tariff.Stored = TestTariffs.Table(new TariffTable(
            [new TariffPeriod("00:00", "06:00"), new TariffPeriod("22:00", "24:00")],
            [new TariffPeriod("00:00", "08:00"), new TariffPeriod("13:00", "15:00"), new TariffPeriod("22:00", "24:00")]));

        Services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now, Prague));
        Services.AddSingleton<IPvSystemEditor>(_pv);
        Services.AddSingleton<ISolcastAccountEditor>(_solcast);
        Services.AddSingleton<ITariffEditor>(_tariff);
        Services.AddSingleton<ITariffSchedule>(new FakeTariffSchedule());
        Services.AddSingleton(new TariffPanelRegistration(TariffProviderIds.Table, typeof(TariffTablePanel)));
        Services.AddSingleton(new TariffPanelRegistration(TariffProviderIds.External, typeof(ExternalTariffPanel)));
        Services.AddSingleton<IExternalProviders>(_providers);
        _providers.Set(new ExternalProviderStatus("tariff", "Tariff", "/providers/tariff", HasToken: false, ExternalProviderState.Off));
        _providers.Set(new ExternalProviderStatus("solar-forecast", "Solar forecast", "/providers/solar-forecast", HasToken: false, ExternalProviderState.Off));
        _providers.Set(new ExternalProviderStatus("vehicle", "Car", "/providers/vehicle", HasToken: false, ExternalProviderState.Off));
        Services.AddSingleton<ISolarForecastSourceSelection>(_forecastSources);
        Services.AddSingleton<IWeatherSourceSelection>(_weatherSources);
        _providers.Set(new ExternalProviderStatus("weather", "Weather", "/providers/weather", HasToken: false, ExternalProviderState.Off));
        Services.AddSingleton<IIntegrationsEditor>(_integrations);
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

    /// <summary>The External solar forecast (#388): the sample connected, its forecast received a minute ago.</summary>
    [Fact]
    public void The_solar_forecast_external()
    {
        _forecastSources.Selected = SolarForecastSourceIds.External;
        _providers.Set(new ExternalProviderStatus(
            "solar-forecast", "Solar forecast", "/providers/solar-forecast", HasToken: true, ExternalProviderState.Connected,
            ProviderId: "solar-forecast-sample", ProviderName: "Solar forecast sample", ProviderVersion: "1.0.0",
            Since: Now.AddHours(-3), LastDataAt: Now.AddMinutes(-1)));
        _providers.Token = "gvp_8nR3yW6kM1qT9vB4xC7zD2fG5hJ0sL3eP6iU9oA1wE4";

        Capture(RenderPage<PvSystem>("/settings"), "settings-solar-forecast-external", selector: "#solcast-editor");
    }

    /// <summary>
    /// The weather right after the upgrade that removed the built-in OpenWeather (#425): External chosen
    /// for the owner, the notice saying what to do, and no provider connected yet.
    /// </summary>
    [Fact]
    public void The_weather()
    {
        _weatherSources.Selected = WeatherSource.External;
        _weatherSources.BuiltInRetired = true;
        _providers.Set(new ExternalProviderStatus("weather", "Weather", "/providers/weather", HasToken: true, ExternalProviderState.Waiting));
        _providers.Token = "gvp_8nR3yW6kM1qT9vB4xC7zD2fG5hJ0sL3eP6iU9oA1wE4";

        Capture(RenderPage<PvSystem>("/settings"), "settings-weather", selector: "#weather-editor");
    }

    /// <summary>
    /// The weather from an external provider (#424): the OpenWeather plugin connected, with its own page
    /// in the frame, where the owner enters the OpenWeatherMap key.
    /// </summary>
    [Fact]
    public void The_weather_external()
    {
        _weatherSources.Selected = WeatherSource.External;
        _providers.Set(new ExternalProviderStatus(
            "weather", "Weather", "/providers/weather", HasToken: true, ExternalProviderState.Connected,
            ProviderId: "openweather", ProviderName: "OpenWeather", ProviderVersion: "1.0.0",
            Since: Now.AddHours(-3), LastDataAt: Now.AddMinutes(-4), Healthy: true, PageUrl: new Uri("http://192.168.2.7:8096/")));
        _providers.Token = "gvp_8nR3yW6kM1qT9vB4xC7zD2fG5hJ0sL3eP6iU9oA1wE4";

        Capture(RenderPage<PvSystem>("/settings"), "settings-weather-external", selector: "#weather-editor",
            frames: new Dictionary<string, string> { ["/external/weather/page"] = ProviderPageScreenshot.OpenWeather(Now) });
    }

    [Fact]
    public void The_tariff_table() =>
        Capture(RenderPage<PvSystem>("/settings"), "settings-tariff", selector: "#tariff-editor");

    /// <summary>The External tariff (#401): a provider connected, its windows received a minute ago.</summary>
    [Fact]
    public void The_tariff_external()
    {
        _tariff.Stored = TariffSettings.None with { Provider = TariffProviderIds.External };
        _providers.Set(new ExternalProviderStatus(
            "tariff", "Tariff", "/providers/tariff", HasToken: true, ExternalProviderState.Connected,
            ProviderId: "spot-prices", ProviderName: "Spot prices", ProviderVersion: "1.0.0",
            Since: Now.AddHours(-3), LastDataAt: Now.AddMinutes(-1)));
        _providers.Token = "gvp_5mQ2xV9tL4pR8wK1nB7yC3dF6hJ0sA2eG5iU8oZ4qW1";

        Capture(RenderPage<PvSystem>("/settings"), "settings-tariff-external", selector: "#tariff-editor");
    }

    /// <summary>External providers (#406): the one token, the tariff's provider connected, the forecast's awaited.</summary>
    [Fact]
    public void The_external_providers()
    {
        _providers.Set(new ExternalProviderStatus(
            "tariff", "Tariff", "/providers/tariff", HasToken: true, ExternalProviderState.Connected,
            ProviderId: "spot-prices", ProviderName: "Spot prices", ProviderVersion: "1.0.0",
            Since: Now.AddHours(-3), LastDataAt: Now.AddMinutes(-1)));
        _providers.Set(new ExternalProviderStatus(
            "solar-forecast", "Solar forecast", "/providers/solar-forecast", HasToken: true, ExternalProviderState.Waiting));
        _providers.Set(new ExternalProviderStatus("vehicle", "Car", "/providers/vehicle", HasToken: true, ExternalProviderState.Off));
        _providers.Token = "gvp_5mQ2xV9tL4pR8wK1nB7yC3dF6hJ0sA2eG5iU8oZ4qW1";

        Capture(RenderPage<PvSystem>("/settings"), "settings-external-providers", selector: "#external-providers");
    }

    /// <summary>The API switched on, with the key the Home Assistant integration uses (#393).</summary>
    [Fact]
    public void The_api_access()
    {
        _integrations.Stored[IntegrationSettingKeys.ApiEnabled] = "true";
        _integrations.Running[IntegrationSettingKeys.ApiEnabled] = "true";
        _integrations.Keys.Add(new ApiKeyEntry("home-assistant", "k3Q9xV7mT2pL8wR4nB6yC1dF", SettingSource.SecretStore, false));
        Capture(RenderPage<PvSystem>("/settings"), "settings-api", selector: "#api-editor");
    }

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

    /// <summary>The car read by an external provider (#408): the feed chosen, and its session on the Car page.</summary>
    [Fact]
    public void The_car_page_with_an_external_provider()
    {
        ExternalCar();

        Capture(RenderPage<Car>("/car"), "car-external", selector: "#car-editor");
    }

    [Fact]
    public void The_external_car_providers_session()
    {
        ExternalCar();

        Capture(RenderPage<Car>("/car"), "car-external-provider", selector: "#external-vehicle");
    }

    /// <summary>
    /// The Volkswagen plugin connected, with its own page in the Car page's frame (#410): where the owner
    /// signs in to the car's account.
    /// </summary>
    [Fact]
    public void The_external_car_providers_page()
    {
        ExternalCar();
        _providers.Set(new ExternalProviderStatus(
            "vehicle", "Car", "/providers/vehicle", HasToken: true, ExternalProviderState.Connected,
            ProviderId: "volkswagen", ProviderName: "Volkswagen", ProviderVersion: "1.0.0",
            Since: Now.AddHours(-2), LastDataAt: Now.AddMinutes(-2), PageUrl: new Uri("http://192.168.2.7:8095/")));

        Capture(RenderPage<Car>("/car"), "car-external-page", selector: "#external-vehicle-page",
            frames: new Dictionary<string, string> { ["/external/vehicle/page"] = ProviderPageScreenshot.Volkswagen(Now) });
    }

    private const string Main = "main.content";

    private void ExternalCar()
    {
        _car.RunningFeed = VehicleFeed.External;
        _car.Running[EvSettingKeys.ExternalEnabled] = "true";
        _car.Running[EvSettingKeys.Id] = "model3";
        _car.Running[EvSettingKeys.Name] = "The Model 3";
        _car.Running[EvSettingKeys.Make] = "Tesla";
        _car.Running[EvSettingKeys.Model] = "Model 3 RWD";
        _car.Running[EvSettingKeys.BatteryCapacityKWh] = "57.5";
        _providers.Token = "gvp_5mQ2xV9tL4pR8wK1nB7yC3dF6hJ0sA2eG5iU8oZ4qW1";
        _providers.Set(new ExternalProviderStatus(
            "vehicle", "Car", "/providers/vehicle", HasToken: true, ExternalProviderState.Connected,
            ProviderId: "vehicle-sample", ProviderName: "Vehicle sample", ProviderVersion: "1.0.0",
            Since: Now.AddHours(-2), LastDataAt: Now.AddMinutes(-3)));

        var car = new EvInfo("model3", "The Model 3", "Tesla", "Model 3 RWD", 57_500, 0.9, 3, 6, 16, string.Empty);
        Services.AddSingleton(car);
        Services.AddSingleton(ChargingLimits.Intersect(6, 16, 3, car));
        Services.AddSingleton<IVehicleAccountSignIn>(new NoVehicleAccountSignIn());
        Services.AddSingleton<IVehiclePortalReader>(new NoPortal());
        Services.AddSingleton<IVehicleStateRefresh>(new FakeVehicleStateRefresh(canRefresh: true));
        Services.AddSingleton<IVehicleTelemetry>(new VehicleStateHolder());
        Services.AddSingleton<IVehicleUpdateService>(new SampleProviderFeed());
    }

    /// <summary>An external provider's feed, as the Car page sees it.</summary>
    private sealed class SampleProviderFeed : IVehicleUpdateService
    {
        public string VehicleId => "model3";

        public string Manufacturer => "external";

        public string DisplayName => "Vehicle sample";

        public bool IsPushed => true;

        public VehicleSourceHealth Health => VehicleSourceHealth.Ok("Vehicle sample is connected");

        public TimeSpan NextDelay => Timeout.InfiniteTimeSpan;

        public Task<VehicleState?> FetchAsync(CancellationToken cancellationToken) => Task.FromResult<VehicleState?>(null);
    }

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
