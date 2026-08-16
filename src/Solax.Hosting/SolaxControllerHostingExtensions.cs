using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Solax.Core.Enums;
using Solax.Core.Interfaces;
using Solax.Core.Models;
using Solax.Core.Strategies;
using Solax.Hosting.Configuration;
using Solax.Hosting.Forecasting;
using Solax.Hosting.HomeAssistant;
using Solax.Hosting.Sessions;
using Solax.Infrastructure;
using Solax.Infrastructure.Modbus;
using Solax.Infrastructure.Sessions;
using Solax.Infrastructure.Solcast;
using Solax.Web;

namespace Solax.Hosting;

/// <summary>
/// The controller's composition root: every service the SolaX Local Controller runs on, registered
/// in one call.
///
/// <para>It lives here rather than in the executable so that "the controller" is a thing a host can
/// reference, rather than a thing only one <c>Program.cs</c> knows how to assemble. Solax.Worker is
/// then a host and nothing else — the .env load, the logging configuration and the exit code — and
/// a second host over the same stack needs no copy of any of this.</para>
/// </summary>
public static class SolaxControllerHostingExtensions
{
    /// <summary>
    /// Registers the whole controller — polling, control strategies, the session store, the Home
    /// Assistant integration and the self-hosted web UI — against <paramref name="configuration"/>.
    ///
    /// <para>Takes a service collection rather than a builder so any host shape can call it,
    /// including one that is not <see cref="WebApplicationBuilder"/>-based. A host that does use
    /// <see cref="WebApplicationBuilder"/> should call the overload that takes it: serving the UI
    /// from an unpublished build needs one thing more than a registration, and that overload is
    /// where it is done.</para>
    /// </summary>
    public static IServiceCollection AddSolaxController(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SolaxOptions>(configuration.GetSection(SolaxOptions.SectionName));

        // The zone every "local" decision is made in. Registered as the app's TimeProvider rather than
        // read at each call site, so the services that already take one need no change and there is
        // exactly one answer to "what day is it here". Resolved eagerly: an unknown id must stop the
        // host at startup, not surface days later as a day boundary in the wrong place. See
        // ControllerOptions.TimeZone for why Windows containers cannot rely on TZ the way Linux does.
        services.Configure<ControllerOptions>(configuration.GetSection(ControllerOptions.SectionName));
        services.AddSingleton(ZonedTimeProvider.Resolve(
            configuration.GetSection(ControllerOptions.SectionName)[nameof(ControllerOptions.TimeZone)]));

        services.AddKeyedSingleton<IModbusClient>(ModbusClientKeys.Inverter, (provider, _) =>
        {
            var options = provider.GetRequiredService<IOptions<SolaxOptions>>().Value;

            // The battery discharge hold is the only thing that ever writes to the inverter, so the
            // client is writable only while that feature is both enabled and out of dry-run. With
            // BatteryHold:Enabled false — the default — an inverter write is structurally impossible,
            // not merely skipped.
            var batteryHold = provider.GetRequiredService<IOptions<BatteryHoldOptions>>().Value;
            return WriteProof(provider, new ModbusTcpClient(options.Inverter), batteryHold.Enabled && !batteryHold.DryRun);
        });

        services.AddKeyedSingleton<IModbusClient>(ModbusClientKeys.EvCharger, (provider, _) =>
        {
            var options = provider.GetRequiredService<IOptions<SolaxOptions>>().Value;

            // Writable unless dry-run: the service always boots in Off, but Home Assistant can select
            // a controlling mode at any time, so the client has to be ready for it.
            var chargeControl = provider.GetRequiredService<IOptions<ChargeControlOptions>>().Value;
            return WriteProof(provider, new ModbusTcpClient(options.EvCharger), !chargeControl.DryRun);
        });

        services.AddSingleton<IEnergyStateReader, EnergyStateReader>();

        // Solcast solar-forecast integration. The API key is a secret and is not stored in
        // appsettings.json -- supply it via user-secrets (development) or an environment variable
        // (deployment): Solcast:ApiKey / Solcast__ApiKey.
        services.Configure<SolcastOptions>(configuration.GetSection(SolcastOptions.SectionName));

        services.AddHttpClient(SolcastForecastService.HttpClientName, (provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<SolcastOptions>>().Value;
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
        services.AddSingleton<SolcastForecastService>();
        services.AddSingleton<ISolarForecastService>(provider => provider.GetRequiredService<SolcastForecastService>());
        services.AddHostedService<SolarForecastRefreshWorker>();

        // Forecast-driven EV charge control (issue #10). Disabled by default -- it writes to the
        // charger and the control register addresses must be verified first (see EvChargerRegister).
        services.Configure<ChargeControlOptions>(configuration.GetSection(ChargeControlOptions.SectionName));

        services.AddSingleton<IEvChargerControl>(provider =>
        {
            var client = provider.GetRequiredKeyedService<IModbusClient>(ModbusClientKeys.EvCharger);
            var logger = provider.GetRequiredService<ILogger<EvChargerControl>>();
            var options = provider.GetRequiredService<IOptions<ChargeControlOptions>>().Value;
            return new EvChargerControl(
                client,
                logger,
                dryRun: options.DryRun,
                currentChangeThresholdAmps: options.CurrentChangeThresholdAmps);
        });

        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<ChargeControlOptions>>().Value;
            return new ChargePowerConverter(options.NominalVoltage, options.Phases);
        });

        services.AddSingleton<IChargingController>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<ChargeControlOptions>>().Value;
            return new LiveSolarChargingController(
                provider.GetRequiredService<ChargePowerConverter>(),
                options.MinChargingCurrentAmps,
                options.MaxChargingCurrentAmps,
                options.CurrentStepAmps,
                options.ResumeHysteresisWatts,
                options.BatteryFullSocPercent,
                options.BatteryReleaseSocPercent);
        });

        // Forecast-driven charge control (issue #22): the Solcast forecast decides how much of today's
        // sun the car may have, so the home battery still reaches 100% by the evening deadline. Nested
        // under the ChargeControl section because it refines that feature rather than being a separate
        // one.
        services.Configure<ForecastChargeOptions>(configuration.GetSection(ForecastChargeOptions.SectionName));

        // The day's targets and the SOC floor are settable from Home Assistant without a restart;
        // everything else in the forecast section is installation-level and read once.
        services.AddSingleton<IForecastRuntimeSettings, ForecastRuntimeSettings>();
        services.AddSingleton<DayPlanProvider>();

        services.AddSingleton<ForecastedChargingController>(provider =>
        {
            var chargeControl = provider.GetRequiredService<IOptions<ChargeControlOptions>>().Value;
            var forecast = provider.GetRequiredService<IOptions<ForecastChargeOptions>>().Value;

            return new ForecastedChargingController(
                provider.GetRequiredService<ChargePowerConverter>(),
                // No usable forecast must never read as headroom: the mode degrades to the live-solar
                // controller, i.e. exactly the behaviour that shipped before this one existed.
                provider.GetRequiredService<IChargingController>(),
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
                provider.GetRequiredService<IForecastRuntimeSettings>());
        });

        // Fast charge without the battery (issue #28): the maximum current the site allows, from PV and
        // grid together, with the discharge hold armed for as long as the mode is selected. It needs no
        // forecast and no surplus -- only the ceiling and how long a silent car counts as a finished one.
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<ChargeControlOptions>>().Value;
            return new FastChargingController(options.MaxChargingCurrentAmps, options.CompletionDwell);
        });

        services.AddSingleton(provider =>
            new SurplusMovingAverage(provider.GetRequiredService<IOptions<ChargeControlOptions>>().Value.SurplusAverageWindow));

        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<ChargeControlOptions>>().Value;

            // One controller per mode. Off is absent on purpose: it is handled by the polling loop
            // releasing control, not by a controller that decides to do nothing.
            var controllers = new Dictionary<ChargeControlMode, IChargingController>
            {
                [ChargeControlMode.Solar] = provider.GetRequiredService<IChargingController>(),
                [ChargeControlMode.Forecasted] = provider.GetRequiredService<ForecastedChargingController>(),
                [ChargeControlMode.FastNoBattery] = provider.GetRequiredService<FastChargingController>(),
            };

            return new ChargingControlCoordinator(
                controllers,
                provider.GetRequiredService<IEvChargerControl>(),
                provider.GetRequiredService<SurplusMovingAverage>(),
                pauseCurrentAmps: options.PauseCurrentAmps,
                idlePowerThresholdWatts: options.CompletionPowerThresholdWatts,
                provider.GetRequiredService<ILogger<ChargingControlCoordinator>>(),
                provider.GetRequiredService<TimeProvider>());
        });

        // Runtime charge-control mode, changed at runtime (e.g. by HA). It is deliberately NOT seeded
        // from configuration: the service always starts in Off, holding no control over the charger,
        // and only takes it when somebody asks. A restart is then never a surprise — after a crash, a
        // power cut or a deploy the charger is left exactly as its owner set it, rather than being
        // grabbed by whatever mode happened to be in a config file.
        services.AddSingleton<IChargeControlModeSelector>(provider => new ChargeControlModeSelector(
            ChargeControlMode.Off,
            provider.GetRequiredService<ILogger<ChargeControlModeSelector>>()));
        services.AddSingleton<ChargeControlStatusHolder>();

        // Battery discharge hold (issue #20) -- the only feature that writes to the INVERTER. Disabled
        // by default: the power-control block's addresses and field layout are taken from the upstream
        // integration's map, not a SolaX document, and must be verified against your firmware first.
        services.Configure<BatteryHoldOptions>(configuration.GetSection(BatteryHoldOptions.SectionName));

        // Same contract as the charge mode: the hold always starts OFF, so the battery is free to
        // charge and discharge normally until somebody asks otherwise. The hold is a command with a
        // duration rather than a stored setting, so an unattended restart that re-armed it would
        // silently keep the pack idle.
        services.AddSingleton<IBatteryHoldSelector>(provider => new BatteryHoldSelector(
            initialHold: false,
            provider.GetRequiredService<ILogger<BatteryHoldSelector>>()));

        services.AddSingleton<IBatteryDischargeControl>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<BatteryHoldOptions>>().Value;
            return new BatteryDischargeControl(
                provider.GetRequiredKeyedService<IModbusClient>(ModbusClientKeys.Inverter),
                provider.GetRequiredService<ILogger<BatteryDischargeControl>>(),
                dryRun: options.DryRun,
                duration: options.Duration,
                targetChangeThresholdWatts: options.TargetChangeThresholdWatts);
        });

        services.AddHostedService<SolaxPollingService>();

        // "Stop the service" as a control like any other: both surfaces drive this one seam, exactly as
        // they both drive the mode selector. It is what turns a stop into the host's own graceful
        // shutdown -- the charger paused, the open session closed, the store flushed -- instead of a
        // killed process that leaves the car drawing at our last setpoint. See IServiceShutdown and
        // HostShutdown for why the process's exit code, not the fact that it exited, is what keeps the
        // container down afterwards.
        services.AddSingleton<HostShutdown>();
        services.AddSingleton<IServiceShutdown>(provider => provider.GetRequiredService<HostShutdown>());

        // Charging session store (issue #32). Observes only -- it subscribes to the same status
        // snapshots the Home Assistant worker consumes and writes them to a local SQLite file, so it
        // touches no register and no device. On by default for that reason; a failure to open the store
        // disables recording for the run and leaves everything else running.
        services.Configure<SessionStoreOptions>(configuration.GetSection(SessionStoreOptions.SectionName));

        services.AddSingleton<IChargingSessionStore>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<SessionStoreOptions>>().Value;
            var environment = provider.GetRequiredService<IHostEnvironment>();

            // Resolved against the content root so a relative path means the same thing however the
            // service was started -- `dotnet run`, the debugger, or the container's working directory.
            var path = Path.IsPathRooted(options.Path)
                ? options.Path
                : Path.Combine(environment.ContentRootPath, options.Path);

            return new SqliteChargingSessionStore(path, provider.GetRequiredService<ILogger<SqliteChargingSessionStore>>());
        });

        services.AddHostedService<SessionRecordingWorker>();

        // Home Assistant integration over MQTT (issue #17). Disabled by default; broker credentials are
        // secrets supplied via .env / env var (HomeAssistant__Username / HomeAssistant__Password).
        services.Configure<HomeAssistantOptions>(configuration.GetSection(HomeAssistantOptions.SectionName));
        services.AddHostedService<HomeAssistantMqttWorker>();

        AddWebSurface(services, configuration);

        return services;
    }

    /// <summary>
    /// The controller, plus the one piece of host setup a registration cannot express: the static web
    /// assets manifest the UI needs when it runs from an unpublished build.
    /// </summary>
    public static WebApplicationBuilder AddSolaxController(this WebApplicationBuilder builder)
    {
        builder.Services.AddSolaxController(builder.Configuration);

        if (ReadWebOptions(builder.Configuration).Enabled)
        {
            // Assets that live in build output rather than in a wwwroot folder -- Solax.Web's stylesheet
            // and Blazor's own script -- are only wired up automatically in the Development environment.
            // Anywhere else, running from `dotnet run` without publishing serves every one of them as an
            // empty 200: no 404, no log line, and a page that renders correctly and then never updates,
            // because blazor.web.js arrived zero bytes long. Asking for them explicitly costs nothing in
            // a published app, where the manifest this reads does not exist and the call does nothing.
            builder.WebHost.UseStaticWebAssets();
        }

        return builder;
    }

    /// <summary>
    /// Maps the web UI's endpoints when it is enabled, and nothing at all when it is not.
    /// </summary>
    public static WebApplication UseSolaxController(this WebApplication app)
    {
        var web = app.Services.GetRequiredService<IOptions<WebOptions>>().Value;
        if (!web.Enabled)
        {
            return app;
        }

        // Static assets come from Solax.Web's wwwroot, served at /_content/Solax.Web/... Reads the
        // manifest UseStaticWebAssets() above wires up, which is why this stays here rather than moving
        // into WebUiHost with everything else -- a test host has no such manifest.
        app.MapStaticAssets();
        app.MapSolaxWebUi(web);

        return app;
    }

    // The self-hosted web UI (issue #44). It is a second adapter over the same seam the MQTT worker
    // uses -- it reads ChargeControlStatusHolder and the Core selector interfaces, and owns no control
    // logic of its own -- so the two surfaces are independent: either, both, or neither may run.
    // On by default, unlike the Home Assistant integration: this is the surface a fresh install is
    // operated through, and it needs no broker, no credentials and no onboarding to be useful.
    private static void AddWebSurface(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WebOptions>(configuration.GetSection(WebOptions.SectionName));

        var web = ReadWebOptions(configuration);

        // Only catches the unsatisfiable combination -- a login demanded with no password to check
        // against, which would lock everyone out permanently. Whether a login is required at all is
        // decided by whether a password was configured; see WebOptions.RequireAuthentication.
        web.ValidateAuthenticationConfig();

        if (web.Enabled)
        {
            // The port has exactly one source: the Web section. A code-backed endpoint outranks the
            // hosting addresses, so an inherited ASPNETCORE_URLS cannot quietly move the UI somewhere
            // else. Configured through Kestrel's options rather than IWebHostBuilder.ConfigureKestrel,
            // which is the same registration, so that a host without a WebApplicationBuilder can still
            // make this call.
            services.Configure<KestrelServerOptions>(kestrel => kestrel.ListenAnyIP(web.Port));

            // What the UI displays as "this build". The host owns the answer -- the version is stamped
            // on this assembly -- and hands it over, rather than Solax.Web guessing from its own
            // attributes.
            services.AddSingleton(new WebBuildInfo(BuildInfo.Describe()));

            // Everything else -- Razor components, cookie authentication, the RequireAuthentication
            // toggle, login/logout -- is host-independent and lives in Solax.Web so it can be exercised
            // by a test host too (issues #46, #47).
            services.AddSolaxWebUi(web);
        }
        else
        {
            // "Disabled" has to mean there is no listening socket, not an unmapped one. Kestrel binds
            // its default address when no endpoint is configured, so leaving it in place would expose a
            // port nobody asked for. Replacing IServer is what actually removes the server: Kestrel
            // registers itself with TryAdd during CreateBuilder, and the last registration is the one
            // resolved.
            services.AddSingleton<IServer, NoListenServer>();
        }
    }

    // Read straight from configuration rather than through IOptions: every caller here runs before the
    // service provider exists, and the answer decides what gets registered at all.
    private static WebOptions ReadWebOptions(IConfiguration configuration) =>
        configuration.GetSection(WebOptions.SectionName).Get<WebOptions>() ?? new WebOptions();

    // Enforces the dry-run guarantee structurally: when a device may not be written to, its client
    // physically cannot write, so even a caller that forgot its own guard can never reach the hardware.
    private static IModbusClient WriteProof(IServiceProvider services, IModbusClient client, bool writable) =>
        writable
            ? client
            : new ReadOnlyModbusClient(client, services.GetRequiredService<ILogger<ReadOnlyModbusClient>>());
}
