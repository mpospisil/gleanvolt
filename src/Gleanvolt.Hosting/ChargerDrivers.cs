using Gleanvolt.Devices;
using Gleanvolt.Devices.Solax;

namespace Gleanvolt.Hosting;

/// <summary>
/// The EV charger drivers this build has (issue #348), in one list, as <see cref="InverterDrivers"/> is for
/// inverters. SolaX first: it is the default, and today the only one. Choosing one in configuration is #349.
/// </summary>
public static class ChargerDrivers
{
    /// <summary>The id of the driver used when the configuration names none.</summary>
    public const string DefaultId = SolaxChargerDriverFactory.DriverId;

    /// <summary>One factory per driver, in the order a drop-down shows them.</summary>
    public static IReadOnlyList<Func<ILoggerFactory?, IEvChargerDriverFactory>> Factories { get; } =
    [
        loggers => new SolaxChargerDriverFactory(loggers),
    ];
}
