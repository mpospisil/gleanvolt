using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Fast;
using Gleanvolt.Hosting.Forecasting;
using Gleanvolt.Hosting.HomeAssistant;
using Gleanvolt.Hosting.Monitoring;
using Gleanvolt.Hosting.Sessions;
using Gleanvolt.Hosting.Targeting;
using Gleanvolt.Hosting.Vehicles;
using Gleanvolt.Infrastructure.Vehicles.VwGroup;
using Gleanvolt.Infrastructure;
using Gleanvolt.Infrastructure.Modbus;
using Gleanvolt.Infrastructure.Monitoring;
using Gleanvolt.Infrastructure.Sessions;
using Gleanvolt.Infrastructure.OpenWeather;
using Gleanvolt.Infrastructure.Solcast;
using Gleanvolt.Api;
using Gleanvolt.Web;

namespace Gleanvolt.Hosting;

/// <summary>
/// The controller's composition root: every service the Gleanvolt runs on, registered
/// in one call.
///
/// <para>It lives here rather than in the executable so that "the controller" is a thing a host can
/// reference, rather than a thing only one <c>Program.cs</c> knows how to assemble. Gleanvolt.Worker is
/// then a host and nothing else — the .env load, the logging configuration and the exit code — and
/// a second host over the same stack needs no copy of any of this.</para>
/// </summary>
public static class GleanvoltHostingExtensions
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
    public static IServiceCollection AddGleanvolt(this IServiceCollection services, IConfiguration configuration)
    {
        // Keys that used to describe the installation and no longer do (issue #111). Checked before
        // anything is bound, because the failure they prevent is invisible: a build that ignored
        // Solax__Inverter__Host would run against the default address while the operator's file says
        // otherwise -- starting, polling and charging, pointed somewhere else.
        RetiredConfigurationKeys.Refuse(configuration);

        // The zone every "local" decision is made in. Registered as the app's TimeProvider rather than
        // read at each call site, so the services that already take one need no change and there is
        // exactly one answer to "what day is it here". Resolved eagerly: an unknown id must stop the
        // host at startup, not surface days later as a day boundary in the wrong place. See
        // ControllerOptions.TimeZone for why Windows containers cannot rely on TZ the way Linux does.
        services.Configure<ControllerOptions>(configuration.GetSection(ControllerOptions.SectionName));
        services.AddSingleton(ZonedTimeProvider.Resolve(
            configuration.GetSection(ControllerOptions.SectionName)[nameof(ControllerOptions.TimeZone)]));

        // The installation itself (issue #111): where the array is, and what it is made of. Resolved
        // eagerly, and once, for the same reason the time zone above is — a site that cannot be
        // described is a startup failure, not a connection error minutes later — and registered as one
        // object so that every surface reads the same answer.
        services.Configure<PvSystemOptions>(configuration.GetSection(PvSystemOptions.SectionName));
        var site = PvSystemResolver.Resolve(configuration);
        services.AddSingleton(site);

        services.AddKeyedSingleton<IModbusClient>(ModbusClientKeys.Inverter, (provider, _) =>
        {
            // The battery discharge hold is the only thing that ever writes to the inverter, so the
            // client is writable only while that feature is both enabled and out of dry-run. With
            // BatteryHold:Enabled false — the default — an inverter write is structurally impossible,
            // not merely skipped.
            var batteryHold = provider.GetRequiredService<IOptions<BatteryHoldOptions>>().Value;
            return WriteProof(
                provider,
                new ModbusTcpClient(site.Inverter.Connection),
                batteryHold.Enabled && !batteryHold.DryRun);
        });

        // A loop that runs once today. The composition root is what the charger list has to reach
        // first: registering each charger under its own id is what makes a second one a matter of
        // control logic rather than of wiring. What still cannot handle two is everything downstream —
        // one mode, one set of Home Assistant controls, one surplus to divide — which is why the
        // resolver refuses a second entry rather than this loop quietly accepting it.
        foreach (var charger in site.Chargers)
        {
            services.AddKeyedSingleton<IModbusClient>(charger.Id, (provider, _) =>
            {
                // Writable unless dry-run: the service always boots in Off, but Home Assistant can select
                // a controlling mode at any time, so the client has to be ready for it.
                var chargeControl = provider.GetRequiredService<IOptions<ChargeControlOptions>>().Value;
                return WriteProof(provider, new ModbusTcpClient(charger.Connection), !chargeControl.DryRun);
            });
        }

        // The charger the control path drives, under the fixed key, because [FromKeyedServices] takes a
        // compile-time constant and EvChargerControl and EnergyStateReader are written against one. It
        // resolves the registration above rather than constructing a second client: two clients would be
        // two sockets to the same wallbox, which is precisely the desynchronised-stream failure a single
        // client exists to prevent. The two keyspaces cannot collide — a charger id is a slug and this
        // key is not — so this is an alias and never a self-reference.
        var controlledCharger = site.Chargers[0].Id;
        services.AddKeyedSingleton<IModbusClient>(
            ModbusClientKeys.EvCharger,
            (provider, _) => provider.GetRequiredKeyedService<IModbusClient>(controlledCharger));

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

        // The weather a session ran in (issue #96). Off unless a key and the site's coordinates are
        // configured, and secret on the same terms as Solcast: Weather:ApiKey / Weather__ApiKey.
        // No refresh worker -- it is asked twice per charging session and never on a control path.
        services.Configure<WeatherOptions>(configuration.GetSection(WeatherOptions.SectionName));

        services.AddHttpClient(OpenWeatherMapService.HttpClientName, (provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<WeatherOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                client.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);
            }

            // The service bounds each call itself; this is the backstop for a connection that never
            // gets as far as a response.
            client.Timeout = options.RequestTimeout;
        });

        services.AddSingleton<IWeatherService, OpenWeatherMapService>();

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

        // The car, described once and resolved once (issue #124). An absent Ev section gives
        // EvInfo.Unknown, which narrows nothing -- so an installation that has never described its car
        // behaves exactly as it did before this existed.
        services.AddSingleton(provider =>
        {
            var chargeControl = provider.GetRequiredService<IOptions<ChargeControlOptions>>().Value;
            return EvResolver.Resolve(
                configuration, chargeControl.MinChargingCurrentAmps, chargeControl.MaxChargingCurrentAmps);
        });

        // What the charger and the car will BOTH accept. Every current and every phase count downstream
        // comes from here rather than from ChargeControl directly: the installation's limits are the
        // site's supply, the car's are a second constraint, and the two were previously the same three
        // settings doing both jobs.
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<ChargeControlOptions>>().Value;

            return ChargingLimits.Intersect(
                options.MinChargingCurrentAmps,
                options.MaxChargingCurrentAmps,
                options.Phases,
                provider.GetRequiredService<EvInfo>());
        });

        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<ChargeControlOptions>>().Value;

            // The CAR's phase count, not the wallbox's, when the two differ. Every watts-to-amps
            // conversion in the controller runs through this converter, so a single-phase car behind a
            // three-phase wallbox used to have every power figure overstated threefold -- the deferred
            // fast charge starting hours late, the day plan budgeting energy the car could never take.
            return new ChargePowerConverter(
                options.NominalVoltage, provider.GetRequiredService<ChargingLimits>().Phases);
        });

        services.AddSingleton<IChargingController>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<ChargeControlOptions>>().Value;
            var limits = provider.GetRequiredService<ChargingLimits>();

            return new LiveSolarChargingController(
                provider.GetRequiredService<ChargePowerConverter>(),
                limits.MinAmps,
                limits.MaxAmps,
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
            var limits = provider.GetRequiredService<ChargingLimits>();

            return new ForecastedChargingController(
                provider.GetRequiredService<ChargePowerConverter>(),
                // No usable forecast must never read as headroom: the mode degrades to the live-solar
                // controller, i.e. exactly the behaviour that shipped before this one existed.
                provider.GetRequiredService<IChargingController>(),
                new ForecastedChargingOptions(
                    MinChargingCurrentAmps: limits.MinAmps,
                    MaxChargingCurrentAmps: limits.MaxAmps,
                    CurrentStepAmps: chargeControl.CurrentStepAmps,
                    ResumeHysteresisWatts: chargeControl.ResumeHysteresisWatts,
                    // Only the fallback for a null runtime; in the host the runtime settings supply it
                    // (and are the ones that enforce the floor against the hold's release margin).
                    FloorResumeMarginPercent: forecast.FloorResumeMarginPercent,
                    FloorGuardReserveWatts: forecast.FloorGuardReserveWatts,
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
        // grid together, with the discharge hold armed when the mode starts. It needs no forecast and no
        // surplus -- only the ceiling and how long a silent car counts as a finished one.
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<ChargeControlOptions>>().Value;
            return new FastChargingController(
                provider.GetRequiredService<ChargingLimits>().MaxAmps, options.CompletionDwell);
        });

        // ...and how much of it to deliver before stopping (issue #119). A stopping condition, not a
        // plan: the limit is runtime state belonging to one charge, so like the mode and the targeted
        // request it starts empty -- which is the Full case, and the behaviour this mode had before it
        // could be given an amount.
        services.AddSingleton<IFastChargeSelector, FastChargeSelector>();
        services.AddSingleton<FastChargeProvider>();

        // Targeted charging (issue #80): a stated amount of energy by a stated departure time, with the
        // grid block placed over the sunniest hours it can reach. The request itself is runtime state rather than
        // configuration -- it belongs to one trip -- so the selector starts empty and, like the mode,
        // does not survive a restart.
        services.Configure<TargetedChargeOptions>(configuration.GetSection(TargetedChargeOptions.SectionName));
        services.AddSingleton<ITargetedChargeSelector, TargetedChargeSelector>();
        services.AddSingleton<TargetedChargeProvider>();

        // The same provider, read-only: "what would this request do?" for a request nobody has made
        // yet. The web UI puts the plan in front of the owner before the button that starts the
        // charger, and needs a seam that cannot possibly start one.
        services.AddSingleton<ITargetedChargePreview>(provider =>
            provider.GetRequiredService<TargetedChargeProvider>());

        services.AddSingleton(provider =>
        {
            var chargeControl = provider.GetRequiredService<IOptions<ChargeControlOptions>>().Value;
            var forecast = provider.GetRequiredService<IOptions<ForecastChargeOptions>>().Value;
            var targeted = provider.GetRequiredService<IOptions<TargetedChargeOptions>>().Value;
            var limits = provider.GetRequiredService<ChargingLimits>();

            return new TargetedChargingController(
                provider.GetRequiredService<ChargePowerConverter>(),
                new TargetedChargingOptions(
                    MinChargingCurrentAmps: limits.MinAmps,
                    MaxChargingCurrentAmps: limits.MaxAmps,
                    CurrentStepAmps: chargeControl.CurrentStepAmps,
                    ResumeHysteresisWatts: chargeControl.ResumeHysteresisWatts,
                    // The dwell timers are the forecast mode's: they exist to spare the contactor and
                    // the car, which is a property of the hardware rather than of either strategy.
                    MinRunTime: forecast.MinRunTime,
                    MinPauseTime: forecast.MinPauseTime,
                    CompletionDwell: chargeControl.CompletionDwell,
                    GridBridge: targeted.GridBridge,
                    // Deliberately the forecast mode's figure. "Is this surplus real enough to bridge?"
                    // is a question about the roof, not about which source pays for the gap, and two
                    // numbers for it would only ever drift apart.
                    MinBridgeSurplusWatts: forecast.MinBridgeSurplusWatts));
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
                [ChargeControlMode.Targeted] = provider.GetRequiredService<TargetedChargingController>(),
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

        // The one way controlled charging is started or stopped (issue #89): the button surfaces call
        // this, it writes the charger's use-mode, and only then does the mode above move. Nothing else
        // writes the use-mode, and nothing here writes anything until somebody presses something.
        services.AddSingleton<IChargeActions, ChargeActions>();
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

        services.AddHostedService<PollingService>();

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

        // Energy interval monitoring. A second observer of the same status snapshots, with its own
        // tables in its own file: it records what the *site* did in fixed windows — solar, forecast,
        // grid both ways, the car, and the home battery — for every quarter hour of every day, whether
        // or not anything was charging. The session store cannot answer that; most of the year has no
        // session in it. On by default for the same reason the session store is, and a failure to open
        // its file disables this feature alone.
        services.Configure<EnergyMonitorOptions>(configuration.GetSection(EnergyMonitorOptions.SectionName));

        services.AddSingleton<IEnergyIntervalStore>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<EnergyMonitorOptions>>().Value;
            var environment = provider.GetRequiredService<IHostEnvironment>();

            // Resolved against the content root so a relative path means the same thing however the
            // service was started -- `dotnet run`, the debugger, or the container's working directory.
            var path = Path.IsPathRooted(options.Path)
                ? options.Path
                : Path.Combine(environment.ContentRootPath, options.Path);

            return new SqliteEnergyIntervalStore(path, provider.GetRequiredService<ILogger<SqliteEnergyIntervalStore>>());
        });

        services.AddHostedService<EnergyMonitorWorker>();

        // Home Assistant integration over MQTT (issue #17). Disabled by default; broker credentials are
        // secrets supplied via .env / env var (HomeAssistant__Username / HomeAssistant__Password).
        services.Configure<HomeAssistantOptions>(configuration.GetSection(HomeAssistantOptions.SectionName));

        // The system's id fills a topic segment from here on (issue #111), so this is the point at which
        // an anonymous installation stops being a describable one. Demanded only when the integration is
        // actually on: a controller-only deployment publishes nothing and has nothing to collide with,
        // and failing it over a value nobody would read would be a rule for its own sake.
        if (configuration.GetSection(HomeAssistantOptions.SectionName).GetValue<bool>(nameof(HomeAssistantOptions.Enabled))
            && string.IsNullOrWhiteSpace(site.Id))
        {
            throw new InvalidOperationException(
                "Pv:Id is required when the Home Assistant integration is enabled: it is the topic segment "
                + "this system publishes under, and without it two installations on one broker overwrite "
                + "each other. Set Pv__Id to a slug (for example 'home-roof').");
        }

        // The topic layout, as a singleton rather than something the worker builds for itself: the
        // configuration page shows the same topics the worker publishes on (issue #143), and a second
        // copy of "{prefix}/battery_hold/set" anywhere else is a bug waiting for the next rename.
        // Constructible whether or not the integration is on -- with it off nothing resolves it -- so
        // it is registered unconditionally, like everything else the UI may have to describe.
        services.AddSingleton(provider => new HaDiscovery(
            provider.GetRequiredService<IOptions<HomeAssistantOptions>>().Value,
            provider.GetRequiredService<PvSystemInfo>(),
            provider.GetRequiredService<IOptions<BatteryHoldOptions>>().Value.Enabled,
            provider.GetServices<IVehicleUpdateService>().Any()));

        services.AddHostedService<HomeAssistantMqttWorker>();

        // Vehicle telemetry read off MQTT (issue #73). Disabled by default; broker credentials are
        // secrets supplied via .env / env var (Vehicle__Username / Vehicle__Password).
        //
        // The holder and IVehicleTelemetry are registered whether or not the feed is enabled, so the web
        // UI can inject the read side unconditionally and render "no reading" rather than having to know
        // about the configuration. Only the worker checks Enabled.
        services.Configure<VehicleOptions>(configuration.GetSection(VehicleOptions.SectionName));
        services.AddSingleton<VehicleStateHolder>();
        services.AddSingleton<IVehicleTelemetry>(provider => provider.GetRequiredService<VehicleStateHolder>());

        // Whether the manufacturer's own portal is read on a clock (issue #140). Decided here, before
        // anything is registered, because it is what settles which of two feeds owns the holder.
        var manufacturerFeed = VwGroupPortalOptionsResolver.IsFeedEnabled(configuration);

        // "If both sources are on, the manufacturer service wins" -- and the only honest way to make one
        // win over a last-write-wins holder is for the other not to be subscribed at all. Neither worker
        // is changed to know about the other: precedence between two sources is a composition decision,
        // so it is taken once, here. VehicleUpdateWorker says so in the log at startup, which is where
        // an operator whose MQTT feed has gone quiet will look.
        if (!manufacturerFeed)
        {
            services.AddHostedService<VehicleMqttWorker>();
        }

        // The car read from VW's own EU Data Act portal, on demand from a button in the web UI
        // (issues #137/#139). Registered unconditionally, like the holder above and for the same
        // reason: the page injects it and renders "not configured" itself, rather than the container
        // deciding whether a page may exist.
        //
        // Configuration is Vehicle:DataAct:*, and falls back to the VW_* environment variables the
        // console harness reads -- one .env then serves both, which is the whole point of having
        // documented those names.
        services.AddSingleton<IVehiclePortalReader>(provider => new VwGroupPortalReader(
            VwGroupPortalOptionsResolver.Resolve(configuration),
            provider.GetService<ILogger<VwGroupPortalReader>>()));

        // The same portal on its own clock (issue #140), which is a different thing from the button
        // above and is registered on different terms: only when Vehicle:DataAct:Enabled says so.
        // Credentials being present is not consent to replay them at an identity provider every
        // quarter of an hour -- pressing the button is how they are proved, and this is how a feed is
        // asked for.
        //
        // The service, not the host, owns the cadence: nothing here states an interval.
        if (manufacturerFeed)
        {
            services.AddSingleton<IVehicleUpdateService>(provider => new VwGroupUpdateService(
                provider.GetRequiredService<EvInfo>().Id,
                VwGroupPortalOptionsResolver.Resolve(configuration),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetService<ILogger<VwGroupUpdateService>>()));
        }

        // Registered whether or not a service exists: with none it logs that fact once and stops, which
        // is a supported installation rather than a misconfigured one.
        services.AddHostedService<VehicleUpdateWorker>();

        // The UI needs MaxAge to mark a reading stale and the pack's size to offer a target in state of
        // charge, but Gleanvolt.Web cannot see this assembly's options classes. Hand it the values,
        // exactly as WebBuildInfo is handed the version.
        services.AddSingleton(provider =>
        {
            var vehicle = provider.GetRequiredService<IOptions<VehicleOptions>>().Value;
            var ev = provider.GetRequiredService<EvInfo>();

            // MaxAge is the feed's; the pack is the car's. Two sections, and the split is the point of
            // issue #124.
            return new VehicleDisplayOptions(vehicle.MaxAge, ev.BatteryCapacityKWh, ev.ChargeEfficiency);
        });

        // Same arrangement for the targeted page: it has to reject a departure beyond the horizon
        // before the request is ever made, and that limit lives in this assembly's options.
        services.AddSingleton(provider =>
        {
            var targeted = provider.GetRequiredService<IOptions<TargetedChargeOptions>>().Value;
            return new TargetedDisplayOptions(targeted.MaxHorizon, targeted.JustInTime.RestSocPercent);
        });

        // The same figures as the two records above, in the shape TargetedChargeRequestFactory takes.
        // A Core type rather than a third per-surface copy: composing a request is decision logic, it
        // now has two doors (the form and the API), and both must reject the same things for the same
        // reasons -- so the factory is shared and this is what it is fed.
        services.AddSingleton(provider =>
        {
            var targeted = provider.GetRequiredService<IOptions<TargetedChargeOptions>>().Value;
            var ev = provider.GetRequiredService<EvInfo>();

            return new TargetedChargeRequestLimits(
                targeted.MaxHorizon,
                ev.BatteryCapacityKWh,
                ev.ChargeEfficiency,
                targeted.JustInTime.RestSocPercent);
        });

        AddHttpSurfaces(services, configuration);

        return services;
    }

    /// <summary>
    /// The controller, plus the one piece of host setup a registration cannot express: the static web
    /// assets manifest the UI needs when it runs from an unpublished build.
    /// </summary>
    public static WebApplicationBuilder AddGleanvolt(this WebApplicationBuilder builder)
    {
        builder.Services.AddGleanvolt(builder.Configuration);

        if (ReadWebOptions(builder.Configuration).Enabled)
        {
            // Assets that live in build output rather than in a wwwroot folder -- Gleanvolt.Web's stylesheet
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
    public static WebApplication UseGleanvolt(this WebApplication app)
    {
        var web = app.Services.GetRequiredService<IOptions<WebOptions>>().Value;
        var api = app.Services.GetRequiredService<IOptions<ApiOptions>>().Value;

        if (web.Enabled)
        {
            // Static assets come from Gleanvolt.Web's wwwroot, served at /_content/Gleanvolt.Web/... Reads the
            // manifest UseStaticWebAssets() above wires up, which is why this stays here rather than moving
            // into WebUiHost with everything else -- a test host has no such manifest.
            app.MapStaticAssets();
            app.MapGleanvoltWebUi(web);
        }

        // After the UI, so its authentication and antiforgery middleware are already in the pipeline when
        // both are on. Neither applies to these routes -- the API carries its own key check and speaks
        // JSON rather than forms -- and the order is what keeps that true rather than incidental.
        app.MapGleanvoltApi(api, app.Logger);

        return app;
    }

    // The two HTTP surfaces: the self-hosted web UI (issue #44) and the API (issue #103). Both are
    // adapters over the same seams the MQTT worker uses -- they read ChargeControlStatusHolder and drive
    // the Core interfaces, and own no control logic -- so all three surfaces are independent: any, all,
    // or none of them may run.
    //
    // They share one socket, because this is one appliance rather than two services that happen to be
    // co-hosted, and Web:Port is therefore the HTTP port for both. What differs is the default: the UI
    // is on, because it is the surface a fresh install is operated through and it needs no broker, no
    // credentials and no onboarding; the API is off, because it is a control surface a program drives
    // and two of its endpoints write to hardware.
    private static void AddHttpSurfaces(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WebOptions>(configuration.GetSection(WebOptions.SectionName));
        services.Configure<ApiOptions>(configuration.GetSection(ApiOptions.SectionName));

        var web = ReadWebOptions(configuration);
        var api = ReadApiOptions(configuration);

        // Only catches the unsatisfiable combinations -- a login demanded with no password to check
        // against, which would lock everyone out permanently, and an API switched on with no key, which
        // would let anything that can reach the port drive the charger. Whether the UI requires a login
        // at all is decided by whether a password was configured; see WebOptions.RequireAuthentication.
        web.ValidateAuthenticationConfig();
        api.ValidateKeyConfig();

        if (web.Enabled || api.Enabled)
        {
            // The port has exactly one source: the Web section. A code-backed endpoint outranks the
            // hosting addresses, so an inherited ASPNETCORE_URLS cannot quietly move the surfaces
            // somewhere else. Configured through Kestrel's options rather than IWebHostBuilder.ConfigureKestrel,
            // which is the same registration, so that a host without a WebApplicationBuilder can still
            // make this call.
            services.Configure<KestrelServerOptions>(kestrel => kestrel.ListenAnyIP(web.Port));
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

        if (web.Enabled)
        {
            // What the UI displays as "this build". The host owns the answer -- the version is stamped
            // on this assembly -- and hands it over, rather than Gleanvolt.Web guessing from its own
            // attributes.
            services.AddSingleton(new WebBuildInfo(BuildInfo.Describe()));

            // The third control surface (issue #142), on the same terms as the MQTT links below and for
            // the same reason: registered whenever the UI is enabled rather than inside the
            // `if (api.Enabled)` block further down, because "the API is off" is what the section most
            // often has to say -- and it is off by default.
            //
            // The key is gated exactly as the broker password is, and the gate is the host's: a key is
            // bearer-equivalent to the stop button on the wallbox, and rendering it on a UI that has no
            // login would hand the control API to anything that can reach port 8090 -- which is what
            // Api:Enabled defaulting to false and the fail-fast on a keyless API exist to prevent.
            //
            // The paths are GleanvoltApi's own constants rather than literals, so a route that moves
            // cannot leave the page pointing at where it used to be.
            services.AddSingleton(new ApiDisplayOptions(
                api.Enabled,
                GleanvoltApi.BasePath,
                GleanvoltApi.DocumentPath,
                web.Port,
                [.. api.Keys
                    .Where(key => !string.IsNullOrWhiteSpace(key.Value))
                    // By name, so the table does not reorder itself between restarts on an installation
                    // with several clients.
                    .OrderBy(key => key.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(key => new ApiKeyDisplay(
                        key.Key, web.AuthenticationRequired ? key.Value : null))]));

            // What the two MQTT links are configured to do (issue #143). Registered whenever the UI is
            // enabled rather than inside a check on either link, because "MQTT is off" is the thing the
            // section most often has to say -- and it is off by default.
            //
            // The broker password is the one real decision here, and the host is what makes it: the UI
            // is an open LAN dashboard unless a password is configured, and MQTT_PASSWORD is the account
            // that publishes to the .../set topics -- so handing it out would be handing out the stop
            // button on the wallbox by another route. The record is therefore built with a null password
            // unless a login is actually enforced, which is a structural guarantee in the spirit of
            // ReadOnlyModbusClient: the page cannot disclose what it was never given.
            services.AddSingleton(provider =>
            {
                var ha = provider.GetRequiredService<IOptions<HomeAssistantOptions>>().Value;
                var vehicle = provider.GetRequiredService<IOptions<VehicleOptions>>().Value;
                var discovery = provider.GetRequiredService<HaDiscovery>();
                var ev = provider.GetRequiredService<EvInfo>();

                return new MqttDisplayOptions(
                    new HomeAssistantMqttDisplay(
                        Connection(
                            ha.Enabled, ha.BrokerHost, ha.BrokerPort, ha.Username, ha.Password, discovery.ClientId),
                        ha.DiscoveryPrefix,
                        // The id in force, not the configured blank that may have produced it: empty
                        // means "take Pv:Id", and HaDiscovery is where that rule lives.
                        discovery.DeviceId,
                        discovery.TopicPrefix,
                        [.. discovery.WellKnownTopics().Select(topic =>
                            new MqttTopicDisplay(topic.Purpose, topic.Topic, topic.Inbound))],
                        ha.StatusInterval,
                        ha.RetireDeviceIds,
                        ha.RetireTopicPrefixes),
                    new VehicleMqttDisplay(
                        Connection(
                            vehicle.Enabled, vehicle.BrokerHost, vehicle.BrokerPort, vehicle.Username,
                            vehicle.Password, VehicleMqttWorker.ClientId),
                        // The topic the worker actually subscribed to, which comes from the car (#124)
                        // and not from Vehicle:Topic -- showing the latter would display a setting that
                        // is being ignored.
                        ev.TelemetryTopic,
                        vehicle.MaxAge,
                        vehicle.ReconnectInterval));

                MqttConnectionDisplay Connection(
                    bool enabled, string host, int port, string? username, string? password, string clientId) =>
                    new(
                        enabled,
                        host,
                        port,
                        username ?? string.Empty,
                        web.AuthenticationRequired && !string.IsNullOrEmpty(password) ? password : null,
                        !string.IsNullOrEmpty(password),
                        clientId);
            });

            // Everything else -- Razor components, cookie authentication, the RequireAuthentication
            // toggle, login/logout -- is host-independent and lives in Gleanvolt.Web so it can be exercised
            // by a test host too (issues #46, #47).
            services.AddGleanvoltWebUi(web);
        }

        if (api.Enabled)
        {
            // The same arrangement, for the same reason: the version is this assembly's, and Vehicle:MaxAge
            // is bound here, so the API is handed both rather than reaching for either.
            services.AddSingleton(provider => new ApiHostInfo(
                BuildInfo.Describe(),
                provider.GetRequiredService<IOptions<VehicleOptions>>().Value.MaxAge));

            services.AddGleanvoltApi(api);
        }
    }

    // Read straight from configuration rather than through IOptions: every caller here runs before the
    // service provider exists, and the answer decides what gets registered at all.
    private static WebOptions ReadWebOptions(IConfiguration configuration) =>
        configuration.GetSection(WebOptions.SectionName).Get<WebOptions>() ?? new WebOptions();

    private static ApiOptions ReadApiOptions(IConfiguration configuration) =>
        configuration.GetSection(ApiOptions.SectionName).Get<ApiOptions>() ?? new ApiOptions();

    // Enforces the dry-run guarantee structurally: when a device may not be written to, its client
    // physically cannot write, so even a caller that forgot its own guard can never reach the hardware.
    private static IModbusClient WriteProof(IServiceProvider services, IModbusClient client, bool writable) =>
        writable
            ? client
            : new ReadOnlyModbusClient(client, services.GetRequiredService<ILogger<ReadOnlyModbusClient>>());
}
