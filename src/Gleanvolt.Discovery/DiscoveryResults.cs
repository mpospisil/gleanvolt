using Gleanvolt.Core.Models;

namespace Gleanvolt.Discovery;

/// <summary>What was found at one host and port (and, for Modbus, one unit id).</summary>
public enum DiscoveryFinding
{
    /// <summary>Answered Modbus with a SolaX serial number, known to Gleanvolt or not.</summary>
    SolaxDevice,

    /// <summary>Answered Modbus, but with no SolaX serial number: "answers Modbus, not a SolaX".</summary>
    NotSolax,

    /// <summary>
    /// The port is open and nothing answered Modbus on it: most often a device whose few Modbus
    /// connections are already taken, by Gleanvolt or Home Assistant.
    /// </summary>
    NoModbusReply,

    /// <summary>
    /// A Modbus gateway answered, but only with "nobody here" for every unit id tried: an RS485 bridge
    /// with nothing (or nothing at those addresses) behind it.
    /// </summary>
    NoUnitAnswered,

    /// <summary>A SolaX Wi-Fi or LAN dongle's web page. Not Modbus.</summary>
    SolaxDongle,
}

/// <summary>
/// How far Gleanvolt can drive a device, as <c>docs/SUPPORTED_PV_SYSTEMS.md</c> tiers it. The doc and
/// <see cref="SolaxModels"/> must agree.
/// </summary>
public enum SupportTier
{
    /// <summary>Run against the reference installation.</summary>
    Verified,

    /// <summary>The same registers, per the upstream map; not tested.</summary>
    Expected,

    /// <summary>Telemetry and charge control work; the battery hold does not exist on this family.</summary>
    ReadingsAndChargingOnly,

    /// <summary>A different register map, or registers that mean something else.</summary>
    NotSupported,

    /// <summary>A serial number in SolaX's shape that Gleanvolt's table does not know.</summary>
    UnknownSolax,

    /// <summary>Answers Modbus, but nothing about it says SolaX.</summary>
    NotSolax,
}

/// <summary>What a serial-number prefix says a SolaX box is.</summary>
/// <param name="Role">The inverter or the charger.</param>
/// <param name="Model">The model as <c>SUPPORTED_PV_SYSTEMS.md</c> names it, e.g. <c>X3-HYB-G4 PRO</c>.</param>
/// <param name="Family">The upstream family, e.g. <c>GEN6 hybrid</c>.</param>
/// <param name="Tier">How far Gleanvolt can drive it.</param>
public sealed record SolaxModel(PvDeviceRole Role, string Model, string Family, SupportTier Tier);

/// <summary>
/// One result of a scan. The schema of <c>--json</c>, and what <c>/pv-system</c> will consume.
/// </summary>
/// <param name="Host">The address scanned.</param>
/// <param name="Port">The TCP port.</param>
/// <param name="UnitId">The Modbus unit id that answered, or null where none did (or for a web page).</param>
/// <param name="Finding">What kind of answer this is.</param>
/// <param name="Role">The inverter or the charger, when the serial number says which.</param>
/// <param name="Model">The model, when the serial number names one.</param>
/// <param name="Family">The family, when the serial number names one.</param>
/// <param name="SerialNumber">The serial number as read, when there was one.</param>
/// <param name="Tier">How far Gleanvolt can drive it, for anything that answered Modbus.</param>
/// <param name="RoleConfirmed">
/// Whether the role check (<c>DeviceRoleCheck</c>, the same one <c>/pv-system</c>'s probe uses) agrees
/// with the serial number; null when it was not run.
/// </param>
/// <param name="Note">One sentence for a human: what was read, or what to do next.</param>
public sealed record DiscoveredDevice(
    string Host,
    int Port,
    byte? UnitId,
    DiscoveryFinding Finding,
    PvDeviceRole? Role,
    string? Model,
    string? Family,
    string? SerialNumber,
    SupportTier? Tier,
    bool? RoleConfirmed,
    string Note);

/// <summary>Everything one scan found, and what it was told to scan.</summary>
/// <param name="Networks">The subnets (or single hosts) scanned, as text.</param>
/// <param name="HostsScanned">How many addresses were probed.</param>
/// <param name="Devices">The results, ordered by address, port and unit id.</param>
/// <param name="Warnings">Anything about the scan itself a human should know: a capped subnet, a Docker network, a host that ran out of time.</param>
/// <param name="Cancelled">Whether the scan was stopped before it finished; <see cref="Devices"/> is then what it had found so far.</param>
public sealed record DiscoveryReport(
    IReadOnlyList<string> Networks,
    int HostsScanned,
    IReadOnlyList<DiscoveredDevice> Devices,
    IReadOnlyList<string> Warnings,
    bool Cancelled);
