using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Serilog;
using Solax.Core.Enums;
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

// Enforces the dry-run guarantee structurally: when a device may not be written to, its client
// physically cannot write, so even a caller that forgot its own guard can never reach the hardware.
static IModbusClient WriteProof(IServiceProvider services, IModbusClient client, bool writable) =>
    writable
        ? client
        : new ReadOnlyModbusClient(client, services.GetRequiredService<ILogger<ReadOnlyModbusClient>>());

builder.Services.AddKeyedSingleton<IModbusClient>(ModbusClientKeys.Inverter, (services, _) =>
{
    var options = services.GetRequiredService<IOptions<SolaxOptions>>().Value;

    // The battery discharge hold is the only thing that ever writes to the inverter, so the client is
    // writable only while that feature is both enabled and out of dry-run. With BatteryHold:Enabled
    // false — the default — an inverter write is structurally impossible, not merely skipped.
    var batteryHold = services.GetRequiredService<IOptions<BatteryHoldOptions>>().Value;
    return WriteProof(services, new ModbusTcpClient(options.Inverter), batteryHold.Enabled && !batteryHold.DryRun);
});

builder.Services.AddKeyedSingleton<IModbusClient>(ModbusClientKeys.EvCharger, (services, _) =>
{
    var options = services.GetRequiredService<IOptions<SolaxOptions>>().Value;

    // Not gated on ChargeControl:Enabled: that is only the boot mode, and Home Assistant can select
    // Solar at runtime on a service that started with it off.
    var chargeControl = services.GetRequiredService<IOptions<ChargeControlOptions>>().Value;
    return WriteProof(services, new ModbusTcpClient(options.EvCharger), !chargeControl.DryRun);
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

builder.Services.AddSingleton(services =>
{
    var options = services.GetRequiredService<IOptions<ChargeControlOptions>>().Value;
    return new ChargePowerConverter(options.NominalVoltage, options.Phases);
});

builder.Services.AddSingleton<IChargingController>(services =>
{
    var options = services.GetRequiredService<IOptions<ChargeControlOptions>>().Value;
    return new LiveSolarChargingController(
        services.GetRequiredService<ChargePowerConverter>(),
        options.MinChargingCurrentAmps,
        options.MaxChargingCurrentAmps,
        options.CurrentStepAmps,
        options.ResumeHysteresisWatts,
        options.BatteryFullSocPercent,
        options.BatteryReleaseSocPercent);
});

builder.Services.AddSingleton(services =>
    new SurplusMovingAverage(services.GetRequiredService<IOptions<ChargeControlOptions>>().Value.SurplusAverageWindow));

builder.Services.AddSingleton(services =>
{
    var options = services.GetRequiredService<IOptions<ChargeControlOptions>>().Value;
    return new ChargingControlCoordinator(
        services.GetRequiredService<IChargingController>(),
        services.GetRequiredService<IEvChargerControl>(),
        services.GetRequiredService<SurplusMovingAverage>(),
        pauseCurrentAmps: options.PauseCurrentAmps,
        services.GetRequiredService<ILogger<ChargingControlCoordinator>>());
});

// Runtime charge-control mode (Off/Solar), seeded from config; changed at runtime (e.g. by HA).
// The config Enabled flag is the boot default: enabled -> Solar, disabled -> Off.
builder.Services.AddSingleton<IChargeControlModeSelector>(services => new ChargeControlModeSelector(
    services.GetRequiredService<IOptions<ChargeControlOptions>>().Value.Enabled ? ChargeControlMode.Solar : ChargeControlMode.Off,
    services.GetRequiredService<ILogger<ChargeControlModeSelector>>()));
builder.Services.AddSingleton<ChargeControlStatusHolder>();

// Battery discharge hold (issue #20) -- the only feature that writes to the INVERTER. Disabled by
// default: the power-control block's addresses and field layout are taken from the upstream
// integration's map, not a SolaX document, and must be verified against your firmware first.
builder.Services.Configure<BatteryHoldOptions>(builder.Configuration.GetSection(BatteryHoldOptions.SectionName));

builder.Services.AddSingleton<IBatteryHoldSelector>(services =>
{
    var options = services.GetRequiredService<IOptions<BatteryHoldOptions>>().Value;
    return new BatteryHoldSelector(
        options.Enabled && options.HoldAtStartup,
        services.GetRequiredService<ILogger<BatteryHoldSelector>>());
});

builder.Services.AddSingleton<IBatteryDischargeControl>(services =>
{
    var options = services.GetRequiredService<IOptions<BatteryHoldOptions>>().Value;
    return new BatteryDischargeControl(
        services.GetRequiredKeyedService<IModbusClient>(ModbusClientKeys.Inverter),
        services.GetRequiredService<ILogger<BatteryDischargeControl>>(),
        dryRun: options.DryRun,
        duration: options.Duration,
        targetChangeThresholdWatts: options.TargetChangeThresholdWatts);
});

builder.Services.AddHostedService<SolaxPollingService>();

// Home Assistant integration over MQTT (issue #17). Disabled by default; broker credentials are
// secrets supplied via .env / env var (HomeAssistant__Username / HomeAssistant__Password).
builder.Services.Configure<HomeAssistantOptions>(builder.Configuration.GetSection(HomeAssistantOptions.SectionName));
builder.Services.AddHostedService<HomeAssistantMqttWorker>();

var host = builder.Build();
host.Run();
