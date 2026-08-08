using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Serilog;
using Solax.Core.Enums;
using Solax.Core.Interfaces;
using Solax.Core.Models;
using Solax.Core.Strategies;
using Solax.Infrastructure;
using Solax.Infrastructure.Modbus;
using Solax.Infrastructure.Solcast;
using Solax.Worker;
using Solax.Worker.Configuration;
using Solax.Worker.Forecasting;
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

    // Writable unless dry-run: the service always boots in Off, but Home Assistant can select a
    // controlling mode at any time, so the client has to be ready for it.
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

// Forecast-driven charge control (issue #22): the Solcast forecast decides how much of today's sun
// the car may have, so the home battery still reaches 100% by the evening deadline. Nested under the
// ChargeControl section because it refines that feature rather than being a separate one.
builder.Services.Configure<ForecastChargeOptions>(builder.Configuration.GetSection(ForecastChargeOptions.SectionName));

// The day's targets and the SOC floor are settable from Home Assistant without a restart; everything
// else in the forecast section is installation-level and read once.
builder.Services.AddSingleton<IForecastRuntimeSettings, ForecastRuntimeSettings>();
builder.Services.AddSingleton<DayPlanProvider>();

builder.Services.AddSingleton<ForecastedChargingController>(services =>
{
    var chargeControl = services.GetRequiredService<IOptions<ChargeControlOptions>>().Value;
    var forecast = services.GetRequiredService<IOptions<ForecastChargeOptions>>().Value;

    return new ForecastedChargingController(
        services.GetRequiredService<ChargePowerConverter>(),
        // No usable forecast must never read as headroom: the mode degrades to the live-solar
        // controller, i.e. exactly the behaviour that shipped before this one existed.
        services.GetRequiredService<IChargingController>(),
        new ForecastedChargingOptions(
            MinChargingCurrentAmps: chargeControl.MinChargingCurrentAmps,
            MaxChargingCurrentAmps: chargeControl.MaxChargingCurrentAmps,
            CurrentStepAmps: chargeControl.CurrentStepAmps,
            ResumeHysteresisWatts: chargeControl.ResumeHysteresisWatts,
            EnableBatteryLoan: forecast.EnableBatteryLoan,
            MaxLoanPowerWatts: forecast.MaxLoanPowerWatts,
            MinBridgeSurplusWatts: forecast.MinBridgeSurplusWatts,
            MaxDailyLoanWh: forecast.MaxDailyLoanKWh * 1000,
            LoanSocMarginPercent: forecast.LoanSocMarginPercent,
            MinRunTime: forecast.MinRunTime,
            MinPauseTime: forecast.MinPauseTime,
            FinalGuardBefore: forecast.FinalGuardBefore,
            SessionEnergyTargetWh: forecast.SessionEnergyTargetKWh * 1000),
        services.GetRequiredService<IForecastRuntimeSettings>());
});

// Fast charge without the battery (issue #28): the maximum current the site allows, from PV and grid
// together, with the discharge hold armed for as long as the mode is selected. It needs no forecast
// and no surplus -- only the ceiling and how long a silent car counts as a finished one.
builder.Services.AddSingleton(services =>
{
    var options = services.GetRequiredService<IOptions<ChargeControlOptions>>().Value;
    return new FastChargingController(options.MaxChargingCurrentAmps, options.CompletionDwell);
});

builder.Services.AddSingleton(services =>
    new SurplusMovingAverage(services.GetRequiredService<IOptions<ChargeControlOptions>>().Value.SurplusAverageWindow));

builder.Services.AddSingleton(services =>
{
    var options = services.GetRequiredService<IOptions<ChargeControlOptions>>().Value;

    // One controller per mode. Off is absent on purpose: it is handled by the polling loop releasing
    // control, not by a controller that decides to do nothing.
    var controllers = new Dictionary<ChargeControlMode, IChargingController>
    {
        [ChargeControlMode.Solar] = services.GetRequiredService<IChargingController>(),
        [ChargeControlMode.Forecasted] = services.GetRequiredService<ForecastedChargingController>(),
        [ChargeControlMode.FastNoBattery] = services.GetRequiredService<FastChargingController>(),
    };

    return new ChargingControlCoordinator(
        controllers,
        services.GetRequiredService<IEvChargerControl>(),
        services.GetRequiredService<SurplusMovingAverage>(),
        pauseCurrentAmps: options.PauseCurrentAmps,
        idlePowerThresholdWatts: options.CompletionPowerThresholdWatts,
        services.GetRequiredService<ILogger<ChargingControlCoordinator>>());
});

// Runtime charge-control mode, changed at runtime (e.g. by HA). It is deliberately NOT seeded from
// configuration: the service always starts in Off, holding no control over the charger, and only
// takes it when somebody asks. A restart is then never a surprise — after a crash, a power cut or a
// deploy the charger is left exactly as its owner set it, rather than being grabbed by whatever mode
// happened to be in a config file.
builder.Services.AddSingleton<IChargeControlModeSelector>(services => new ChargeControlModeSelector(
    ChargeControlMode.Off,
    services.GetRequiredService<ILogger<ChargeControlModeSelector>>()));
builder.Services.AddSingleton<ChargeControlStatusHolder>();

// Battery discharge hold (issue #20) -- the only feature that writes to the INVERTER. Disabled by
// default: the power-control block's addresses and field layout are taken from the upstream
// integration's map, not a SolaX document, and must be verified against your firmware first.
builder.Services.Configure<BatteryHoldOptions>(builder.Configuration.GetSection(BatteryHoldOptions.SectionName));

// Same contract as the charge mode: the hold always starts OFF, so the battery is free to charge and
// discharge normally until somebody asks otherwise. The hold is a command with a duration rather than
// a stored setting, so an unattended restart that re-armed it would silently keep the pack idle.
builder.Services.AddSingleton<IBatteryHoldSelector>(services => new BatteryHoldSelector(
    initialHold: false,
    services.GetRequiredService<ILogger<BatteryHoldSelector>>()));

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
