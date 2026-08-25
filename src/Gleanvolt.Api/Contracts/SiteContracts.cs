using Gleanvolt.Core.Models;

namespace Gleanvolt.Api.Contracts;

/// <summary>
/// The installation this API speaks for (issue #111): which system, where it is, what the array does,
/// and which boxes it is made of.
///
/// <para>The first question a program should be able to ask — an MCP client above all — is <i>what am I
/// connected to</i>. Every other endpoint answers what it is <i>doing</i>, and a caller that cannot tell
/// two installations apart cannot safely act on either.</para>
///
/// <para>Every measurement is nullable and null means <b>unset</b>, never zero: a defaulted site is a
/// site in the Atlantic facing north with no capacity, and a client that cannot tell "not configured"
/// from "zero" will draw exactly that conclusion.</para>
/// </summary>
/// <param name="Id">The system's stable id, or empty on an installation that has not claimed one.</param>
/// <param name="Name">What a human calls it; falls back to the id.</param>
/// <param name="Address">The postal address, for display. Never parsed, and not necessarily present.</param>
/// <param name="Latitude">Site latitude in decimal degrees, or null. Always null together with longitude.</param>
/// <param name="Longitude">Site longitude in decimal degrees, or null.</param>
/// <param name="AzimuthDegrees">
/// The direction the array faces, as a compass bearing from true north in [0, 360): 0 north, 90 east,
/// 180 south. Normalised, so a site configured as -180 reports 180.
/// </param>
/// <param name="TiltDegrees">Tilt from horizontal: 0 flat, 90 vertical.</param>
/// <param name="CapacityKwp">Peak DC capacity of the array, in kWp.</param>
/// <param name="InverterCapacityKw">AC-side capacity in kW, where it clips the array.</param>
/// <param name="LossFactor">System loss factor in (0, 1] — the fraction of DC yield that reaches the meter.</param>
/// <param name="InstallDate">Commissioning date.</param>
/// <param name="Inverter">The inverter every figure in <c>/status</c> is read from.</param>
/// <param name="Chargers">The EV chargers. Exactly one is supported today; it is a list because that will not always be true.</param>
public sealed record SiteResponse(
    string Id,
    string Name,
    string Address,
    double? Latitude,
    double? Longitude,
    double? AzimuthDegrees,
    double? TiltDegrees,
    double? CapacityKwp,
    double? InverterCapacityKw,
    double? LossFactor,
    DateOnly? InstallDate,
    SiteDeviceResponse Inverter,
    IReadOnlyList<SiteDeviceResponse> Chargers)
{
    public static SiteResponse From(PvSystemInfo site) => new(
        site.Id,
        site.Name,
        site.Address,
        site.Latitude,
        site.Longitude,
        site.AzimuthDegrees,
        site.TiltDegrees,
        site.CapacityKwp,
        site.InverterCapacityKw,
        site.LossFactor,
        site.InstallDate,
        SiteDeviceResponse.From(site.Inverter),
        [.. site.Chargers.Select(SiteDeviceResponse.From)]);
}

/// <summary>One Modbus device on the system: what it is, and where it is on the network.</summary>
/// <param name="Id">Its identity within the system — for a charger, the id its topics are named after.</param>
/// <param name="Name">Display name, or empty.</param>
/// <param name="Model">
/// What the box is. <b>Reported, not acted on:</b> the register maps are compiled for the devices in the
/// README's hardware targets and are chosen by nothing, so this says what the operator believes is on
/// the other end of the address, not what the controller is speaking.
/// </param>
/// <param name="Host">Hostname or IP address on the local network.</param>
/// <param name="Port">Modbus TCP port.</param>
/// <param name="UnitId">Modbus unit id.</param>
public sealed record SiteDeviceResponse(string Id, string Name, string Model, string Host, int Port, int UnitId)
{
    public static SiteDeviceResponse From(PvDeviceInfo device) => new(
        device.Id,
        device.Name,
        device.Model,
        device.Connection.Host,
        device.Connection.Port,
        device.Connection.UnitId);
}
