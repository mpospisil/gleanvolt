using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Models;
using Gleanvolt.Devices;
using Gleanvolt.Devices.GoE;
using Gleanvolt.Devices.Huawei;
using Gleanvolt.Devices.Solax;
using Gleanvolt.Hosting.Chargers;
using Gleanvolt.Hosting.Configuration;

namespace Gleanvolt.Hosting;

/// <summary>
/// The EV charger drivers this build has (issue #348), in one list, as <see cref="InverterDrivers"/> is for
/// inverters. SolaX first: it is the default. Then Huawei's SCharger, one entry per model (#351), because the
/// two differ in phase count. Then go-e, over its local HTTP API (#352). Last, a charger behind a plugin (#435),
/// which reaches the charger itself. <c>Pv:Chargers:0:Driver</c> picks one (#349); the configuration is checked
/// against this list, the container registers a factory from it, and Settings offers it in a drop-down.
/// </summary>
public static class ChargerDrivers
{
    /// <summary>The id of the driver used when the configuration names none.</summary>
    public const string DefaultId = SolaxChargerDriverFactory.DriverId;

    /// <summary>
    /// One factory per driver, in the order a drop-down shows them, made from the container. Null makes one that
    /// only describes its driver: what it can do, without a log or, for the plugin's, a hub to reach it through.
    /// </summary>
    public static IReadOnlyList<Func<IServiceProvider?, IEvChargerDriverFactory>> Factories { get; } =
    [
        services => new SolaxChargerDriverFactory(Loggers(services)),
        services => HuaweiSChargerDriverFactory.SevenKs(Loggers(services)),
        services => HuaweiSChargerDriverFactory.TwentyTwoKt(Loggers(services)),
        services => new GoeChargerDriverFactory(Loggers(services)),
        services => new ExternalChargerDriverFactory(services?.GetService<ExternalChargerLink>(), Loggers(services)),
    ];

    /// <summary>What each driver is, without creating one: for the configuration rules and the UI.</summary>
    public static IReadOnlyList<EvChargerDriverInfo> Known { get; } =
        [.. Factories.Select(make => EvChargerDriverInfo.Of(make(null)))];

    private static ILoggerFactory? Loggers(IServiceProvider? services) => services?.GetService<ILoggerFactory>();

    /// <summary>
    /// The configuration as the controller resolves the installation from it (#444): with <c>ExternalCharger:UsePlugin</c>
    /// set, the charger is behind a plugin whatever <c>Pv:Chargers</c>, the environment or the overrides file names. The
    /// deploy scripts set it when a charger plugin is in GLEANVOLT_PLUGINS, so the built-in driver and the plugin never
    /// both open the charger: a charger that serves one Modbus client drops a second. An overlay, so every other key is
    /// read as it is.
    /// </summary>
    public static IConfiguration WithPluginCharger(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration.GetValue<bool>(ExternalChargerOptions.SectionName + ":" + nameof(ExternalChargerOptions.UsePlugin))
            ? new ConfigurationBuilder()
                .AddConfiguration(configuration)
                .AddInMemoryCollection([new KeyValuePair<string, string?>(PvSystemSettingKeys.ChargerDriver, ExternalChargerDriverFactory.DriverId)])
                .Build()
            : configuration;
    }

    /// <summary>
    /// <c>ChargeControl</c>'s current range and phase count against what the controlled charger's driver can
    /// do (#349), with that section's defaults for what is not set. Nothing when the charger or its driver is
    /// unknown: the resolver has already said so. Nothing for a charger behind a plugin either: the charger hub
    /// checks it against the plugin's own capabilities at its hello (#440).
    /// </summary>
    public static void CheckLimits(IConfiguration configuration, PvSystemInfo site, ConfigurationProblems problems)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(site);

        // A plugin's charger is checked when the plugin says hello (#440): only then is it known what it can do.
        if (site.Chargers.Count == 0
            || site.Chargers[0].Driver == ExternalChargerDriverFactory.DriverId
            || PvSystemRules.ChargerDriver(site.Chargers[0].Driver, Known) is not { } driver)
        {
            return;
        }

        var options = configuration.GetSection(ChargeControlOptions.SectionName).Get<ChargeControlOptions>() ?? new ChargeControlOptions();
        PvSystemRules.CheckChargeLimits(driver, options.MinChargingCurrentAmps, options.MaxChargingCurrentAmps, options.Phases, problems);
    }
}
