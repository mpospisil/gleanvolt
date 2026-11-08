using Gleanvolt.Devices;
using Gleanvolt.Devices.Huawei;
using Gleanvolt.Devices.Solax;

namespace Gleanvolt.Hosting;

/// <summary>
/// The inverter drivers this build has (issue #318), in one list. The configuration is checked against it,
/// the container registers a factory from it, and Settings offers it in a drop-down, so a driver added
/// here is a driver everywhere. SolaX first: it is the default, and the one an empty id means.
/// </summary>
public static class InverterDrivers
{
    /// <summary>One factory per driver, in the order a drop-down shows them.</summary>
    public static IReadOnlyList<Func<ILoggerFactory?, IInverterDriverFactory>> Factories { get; } =
    [
        loggers => new SolaxInverterDriverFactory(loggers),
        loggers => new HuaweiSun2000DriverFactory(loggers),
    ];

    /// <summary>What each driver is, without creating one: for the configuration rules and the UI.</summary>
    public static IReadOnlyList<InverterDriverInfo> Known { get; } =
        [.. Factories.Select(make => InverterDriverInfo.Of(make(null)))];
}
