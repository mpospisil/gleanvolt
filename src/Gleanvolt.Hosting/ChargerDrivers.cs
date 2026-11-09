using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Models;
using Gleanvolt.Devices;
using Gleanvolt.Devices.Huawei;
using Gleanvolt.Devices.Solax;
using Gleanvolt.Hosting.Configuration;

namespace Gleanvolt.Hosting;

/// <summary>
/// The EV charger drivers this build has (issue #348), in one list, as <see cref="InverterDrivers"/> is for
/// inverters. SolaX first: it is the default. Then Huawei's SCharger, one entry per model (#351), because the
/// two differ in phase count. <c>Pv:Chargers:0:Driver</c> picks one
/// (#349); the configuration is checked against this list, the container registers a factory from it, and
/// Settings offers it in a drop-down.
/// </summary>
public static class ChargerDrivers
{
    /// <summary>The id of the driver used when the configuration names none.</summary>
    public const string DefaultId = SolaxChargerDriverFactory.DriverId;

    /// <summary>One factory per driver, in the order a drop-down shows them.</summary>
    public static IReadOnlyList<Func<ILoggerFactory?, IEvChargerDriverFactory>> Factories { get; } =
    [
        loggers => new SolaxChargerDriverFactory(loggers),
        loggers => HuaweiSChargerDriverFactory.SevenKs(loggers),
        loggers => HuaweiSChargerDriverFactory.TwentyTwoKt(loggers),
    ];

    /// <summary>What each driver is, without creating one: for the configuration rules and the UI.</summary>
    public static IReadOnlyList<EvChargerDriverInfo> Known { get; } =
        [.. Factories.Select(make => EvChargerDriverInfo.Of(make(null)))];

    /// <summary>
    /// <c>ChargeControl</c>'s current range and phase count against what the controlled charger's driver can
    /// do (#349), with that section's defaults for what is not set. Nothing when the charger or its driver is
    /// unknown: the resolver has already said so.
    /// </summary>
    public static void CheckLimits(IConfiguration configuration, PvSystemInfo site, ConfigurationProblems problems)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(site);

        if (site.Chargers.Count == 0 || PvSystemRules.ChargerDriver(site.Chargers[0].Driver, Known) is not { } driver)
        {
            return;
        }

        var options = configuration.GetSection(ChargeControlOptions.SectionName).Get<ChargeControlOptions>() ?? new ChargeControlOptions();
        PvSystemRules.CheckChargeLimits(driver, options.MinChargingCurrentAmps, options.MaxChargingCurrentAmps, options.Phases, problems);
    }
}
