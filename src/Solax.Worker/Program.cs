using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Serilog;
using Solax.Core.Interfaces;
using Solax.Core.Strategies;
using Solax.Infrastructure;
using Solax.Infrastructure.Modbus;
using Solax.Infrastructure.Solcast;
using Solax.Worker;
using Solax.Worker.Configuration;
using Solax.Worker.HomeAssistant;

// Load secrets (e.g. Solcast__ApiKey) from an untracked .env file into the process environment
// before configuration is built, so they reach the app whether it's started via `dotnet run` or
// the VS Code debugger -- without living in any committed file. Real env vars still take priority.
DotEnv.Load(Directory.GetCurrentDirectory());

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Services.AddSerilog(config => config
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext());

builder.Services.Configure<SolaxOptions>(builder.Configuration.GetSection(SolaxOptions.SectionName));

// Enforces the dry-run guarantee structurally: in dry-run the device clients physically cannot
// write, so even a caller that forgot its own guard can never reach the hardware.
static IModbusClient WriteProofInDryRun(IServiceProvider services, IModbusClient client) =>
    services.GetRequiredService<IOptions<ChargeControlOptions>>().Value.DryRun
        ? new ReadOnlyModbusClient(client, services.GetRequiredService<ILogger<ReadOnlyModbusClient>>())
        : client;

builder.Services.AddKeyedSingleton<IModbusClient>(ModbusClientKeys.Inverter, (services, _) =>
{
    var options = services.GetRequiredService<IOptions<SolaxOptions>>().Value;
    return WriteProofInDryRun(services, new ModbusTcpClient(options.Inverter));
});

builder.Services.AddKeyedSingleton<IModbusClient>(ModbusClientKeys.EvCharger, (services, _) =>
{
    var options = services.GetRequiredService<IOptions<SolaxOptions>>().Value;
    return WriteProofInDryRun(services, new ModbusTcpClient(options.EvCharger));
});

builder.Services.AddSingleton<IEnergyStateReader, EnergyStateReader>();

// Solcast solar-forecast integration. The API key is a secret and is not stored in
// appsettings.json -- supply it via user-secrets (development) or an environment variable
// (deployment): Solcast:ApiKey / Solcast__ApiKey.
builder.Services.Configure<SolcastOptions>(builder.Configuration.GetSection(SolcastOptions.SectionName));

builder.Services.AddHttpClient(SolcastForecastService.HttpClientName, (services, client) =>
{
    var options = services.GetRequiredService<IOptions<SolcastOptions>>().Value;
    if (!string.IsNullOrWhiteSpace(options.BaseUrl))
    {
        client.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);
    }

    if (!string.IsNullOrWhiteSpace(options.ApiKey))
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
    }
});

// Single instance shared as both the injectable query interface and (via the refresh worker) a
// service warmed at startup.
builder.Services.AddSingleton<SolcastForecastService>();
builder.Services.AddSingleton<ISolarForecastService>(services => services.GetRequiredService<SolcastForecastService>());
builder.Services.AddHostedService<SolarForecastRefreshWorker>();

// Forecast-driven EV charge control (issue #10). Disabled by default -- it writes to the charger
// and the control register addresses must be verified first (see EvChargerRegister).
builder.Services.Configure<ChargeControlOptions>(builder.Configuration.GetSection(ChargeControlOptions.SectionName));

builder.Services.AddSingleton<IEvChargerControl>(services =>
{
    var client = services.GetRequiredKeyedService<IModbusClient>(ModbusClientKeys.EvCharger);
    var logger = services.GetRequiredService<ILogger<EvChargerControl>>();
    var options = services.GetRequiredService<IOptions<ChargeControlOptions>>().Value;
    return new EvChargerControl(
        client,
        logger,
        dryRun: options.DryRun,
        currentChangeThresholdAmps: options.CurrentChangeThresholdAmps);
});

builder.Services.AddSingleton<IChargingController>(services =>
{
    var options = services.GetRequiredService<IOptions<ChargeControlOptions>>().Value;
    return new LiveSolarChargingController(
        new ChargePowerConverter(options.NominalVoltage, options.Phases),
        options.MinChargingCurrentAmps,
        options.MaxChargingCurrentAmps,
        options.CurrentStepAmps,
        options.ResumeHysteresisWatts,
        options.BatteryFullSocPercent,
        options.BatteryReleaseSocPercent);
});

builder.Services.AddSingleton(services =>
    new SurplusMovingAverage(services.GetRequiredService<IOptions<ChargeControlOptions>>().Value.SurplusAverageWindow));

builder.Services.AddSingleton<ChargingControlCoordinator>();

// Runtime on/off switch for charge control, seeded from config; toggled at runtime (e.g. by HA).
builder.Services.AddSingleton<IChargeControlSwitch>(services => new ChargeControlSwitch(
    services.GetRequiredService<IOptions<ChargeControlOptions>>().Value.Enabled,
    services.GetRequiredService<ILogger<ChargeControlSwitch>>()));
builder.Services.AddSingleton<ChargeControlStatusHolder>();

builder.Services.AddHostedService<SolaxPollingService>();

// Home Assistant integration over MQTT (issue #17). Disabled by default; broker credentials are
// secrets supplied via .env / env var (HomeAssistant__Username / HomeAssistant__Password).
builder.Services.Configure<HomeAssistantOptions>(builder.Configuration.GetSection(HomeAssistantOptions.SectionName));
builder.Services.AddHostedService<HomeAssistantMqttWorker>();

var host = builder.Build();
host.Run();
