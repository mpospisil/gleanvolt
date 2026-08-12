using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Options;
using Serilog;
using Solax.Core.Enums;
using Solax.Core.Interfaces;
using Solax.Core.Models;
using Solax.Core.Strategies;
using Solax.Infrastructure;
using Solax.Infrastructure.Modbus;
using Solax.Infrastructure.Sessions;
using Solax.Infrastructure.Solcast;
using Solax.Web;
using Solax.Web.Components;
using Solax.Worker;
using Solax.Worker.Configuration;
using Solax.Worker.Forecasting;
using Solax.Worker.HomeAssistant;
using Solax.Worker.Sessions;

// Load secrets (e.g. Solcast__ApiKey) from an untracked .env file into the process environment
// before configuration is built, so they reach the app whether it's started via `dotnet run` or
// the VS Code debugger -- without living in any committed file. Real env vars still take priority.
DotEnv.Load(Directory.GetCurrentDirectory());

// Serilog swallows failures inside its own sinks. That silence is dangerous in the container: if the
// bind-mounted logs directory isn't writable by the image's non-root user, the console keeps logging
// normally and the log files simply never appear -- verified, and invisible without this line.
Serilog.Debugging.SelfLog.Enable(Console.Error);

// A web host, but only sometimes a web server. The self-hosted UI (issue #44) lives in Solax.Web and
// is hosted here, so the process needs ASP.NET's builder; when the UI is switched off the process
// must still be exactly what it was before — a headless worker that listens on nothing. See the
// "Web" section below for how that is enforced rather than merely intended.
var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Services.AddSerilog(config => config
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext());

builder.Services.Configure<SolaxOptions>(builder.Configuration.GetSection(SolaxOptions.SectionName));

// The zone every "local" decision is made in. Registered as the app's TimeProvider rather than read
// at each call site, so the services that already take one need no change and there is exactly one
// answer to "what day is it here". Resolved eagerly: an unknown id must stop the host at startup,
// not surface days later as a day boundary in the wrong place. See ControllerOptions.TimeZone for
// why Windows containers cannot rely on TZ the way the Linux ones do.
builder.Services.Configure<ControllerOptions>(builder.Configuration.GetSection(ControllerOptions.SectionName));
builder.Services.AddSingleton(ZonedTimeProvider.Resolve(
    builder.Configuration.GetSection(ControllerOptions.SectionName)[nameof(ControllerOptions.TimeZone)]));

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
        services.GetRequiredService<ILogger<ChargingControlCoordinator>>(),
        services.GetRequiredService<TimeProvider>());
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

// Charging session store (issue #32). Observes only -- it subscribes to the same status snapshots the
// Home Assistant worker consumes and writes them to a local SQLite file, so it touches no register and
// no device. On by default for that reason; a failure to open the store disables recording for the run
// and leaves everything else running.
builder.Services.Configure<SessionStoreOptions>(builder.Configuration.GetSection(SessionStoreOptions.SectionName));

builder.Services.AddSingleton<IChargingSessionStore>(services =>
{
    var options = services.GetRequiredService<IOptions<SessionStoreOptions>>().Value;
    var environment = services.GetRequiredService<IHostEnvironment>();

    // Resolved against the content root so a relative path means the same thing however the service
    // was started -- `dotnet run`, the debugger, or the container's working directory.
    var path = Path.IsPathRooted(options.Path)
        ? options.Path
        : Path.Combine(environment.ContentRootPath, options.Path);

    return new SqliteChargingSessionStore(path, services.GetRequiredService<ILogger<SqliteChargingSessionStore>>());
});

builder.Services.AddHostedService<SessionRecordingWorker>();

// Home Assistant integration over MQTT (issue #17). Disabled by default; broker credentials are
// secrets supplied via .env / env var (HomeAssistant__Username / HomeAssistant__Password).
builder.Services.Configure<HomeAssistantOptions>(builder.Configuration.GetSection(HomeAssistantOptions.SectionName));
builder.Services.AddHostedService<HomeAssistantMqttWorker>();

// The self-hosted web UI (issue #44). It is a second adapter over the same seam the MQTT worker
// uses -- it reads ChargeControlStatusHolder and the Core selector interfaces, and owns no control
// logic of its own -- so the two surfaces are independent: either, both, or neither may run.
// Off by default, like the Home Assistant integration.
builder.Services.Configure<WebOptions>(builder.Configuration.GetSection(WebOptions.SectionName));

var web = builder.Configuration.GetSection(WebOptions.SectionName).Get<WebOptions>() ?? new WebOptions();

if (web.Enabled)
{
    // The port has exactly one source: the Web section. A code-backed endpoint outranks the hosting
    // addresses, so an inherited ASPNETCORE_URLS cannot quietly move the UI somewhere else.
    builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(web.Port));

    // Interactive server rendering: the components run here, beside the services they read, and the
    // browser holds a thin circuit. That is what lets a page update itself as each poll lands
    // without a REST API in between -- WebAssembly would need one, and this project needs it for
    // nothing else.
    builder.Services.AddRazorComponents().AddInteractiveServerComponents();

    // What the UI displays as "this build". The host owns the answer -- the version is stamped on
    // this assembly -- and hands it over, rather than Solax.Web guessing from its own attributes.
    builder.Services.AddSingleton(new WebBuildInfo(BuildInfo.Describe()));
}
else
{
    // "Disabled" has to mean there is no listening socket, not an unmapped one. Kestrel binds its
    // default address when no endpoint is configured, so leaving it in place would expose a port
    // nobody asked for. Replacing IServer is what actually removes the server: Kestrel registers
    // itself with TryAdd during CreateBuilder, and the last registration is the one resolved.
    builder.Services.AddSingleton<IServer, NoListenServer>();
}

var host = builder.Build();

if (web.Enabled)
{
    // Static assets come from Solax.Web's wwwroot, served at /_content/Solax.Web/... Antiforgery is
    // a hard requirement of MapRazorComponents, not an optional hardening step.
    host.MapStaticAssets();
    host.UseAntiforgery();
    host.MapRazorComponents<App>().AddInteractiveServerRenderMode();
}

// First line in the log, before anything can go wrong: a log file or a `docker logs` dump is
// otherwise untraceable to the build that produced it. "0.0.0-dev" with no commit means a local
// build rather than anything CI published.
host.Services.GetRequiredService<ILogger<Program>>().LogInformation(
    "SolaX Local Controller {Version} starting.", BuildInfo.Describe());

// Said explicitly because nothing else says it: Kestrel's own "Now listening on" is logged under
// Microsoft.Hosting.Lifetime, which the Serilog configuration holds at Warning. Without this line a
// log file cannot answer "was the UI up, and on which port" -- and the answer is a listening socket
// on the LAN, which is exactly the sort of thing an operator should be able to audit after the fact.
if (web.Enabled)
{
    host.Services.GetRequiredService<ILogger<Program>>().LogInformation(
        "Web UI enabled; listening on port {Port} (all interfaces, plain HTTP).", web.Port);
}

// An unset zone means "ask the OS", which is right on Linux -- the container's TZ sets it. On
// Windows it is a trap: .NET ignores TZ there, so the container runs in UTC and every session is
// recorded against the wrong day, with nothing in the logs to say so. Say so.
if (OperatingSystem.IsWindows()
    && string.IsNullOrWhiteSpace(host.Services.GetRequiredService<IOptions<ControllerOptions>>().Value.TimeZone))
{
    host.Services.GetRequiredService<ILogger<Program>>().LogWarning(
        "Controller:TimeZone is not set and this is Windows, where .NET ignores the TZ environment "
        + "variable. Local time is {Zone}. Set Controller__TimeZone to a Windows id (e.g. "
        + "\"Central Europe Standard Time\") or the day boundary and recorded sessions will be wrong.",
        TimeZoneInfo.Local.Id);
}

host.Run();
