namespace Gleanvolt.Core.Models;

/// <summary>
/// The installation as everything downstream sees it (issue #111): one resolved, validated snapshot of
/// what this PV system is, built once at startup from <see cref="PvSystemOptions"/> and whatever older
/// keys still describe the same facts.
///
/// <para>The point of resolving once is that no consumer ever has to know there were two sources. A
/// service that wants the site's coordinates asks for them here and gets an answer that has already
/// been through "did the deprecated key win?" — which is a question with exactly one right answer per
/// process, and no place in a weather client.</para>
/// </summary>
/// <param name="Id">The system's stable identity, or empty while nothing has claimed one.</param>
/// <param name="Name">The display name; falls back to <paramref name="Id"/> when unset.</param>
/// <param name="Address">The postal address, for display only.</param>
/// <param name="Latitude">Site latitude in decimal degrees, or null when the site has no coordinates.</param>
/// <param name="Longitude">Site longitude in decimal degrees, or null — always null together with latitude.</param>
/// <param name="AzimuthDegrees">Compass bearing the array faces, normalised into [0, 360). See <see cref="PvSystemOptions.AzimuthDegrees"/>.</param>
/// <param name="TiltDegrees">Tilt from horizontal in degrees.</param>
/// <param name="CapacityKwp">Peak DC capacity in kWp.</param>
/// <param name="InverterCapacityKw">AC-side capacity in kW, where it clips the array.</param>
/// <param name="LossFactor">System loss factor in (0, 1].</param>
/// <param name="InstallDate">Commissioning date.</param>
/// <param name="Inverter">The inverter — always present; a controller with no inverter does not start.</param>
/// <param name="Chargers">The EV chargers, in configuration order. Exactly one today.</param>
public sealed record PvSystemInfo(
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
    PvDeviceInfo Inverter,
    IReadOnlyList<PvDeviceInfo> Chargers)
{
    /// <summary>Whether the site has a usable pair of coordinates. Both or neither, by construction.</summary>
    public bool HasLocation => Latitude is not null && Longitude is not null;

    /// <summary>
    /// One line for the log at startup, so a `docker logs` dump answers "which installation was this,
    /// and what was it talking to" without the configuration beside it.
    /// </summary>
    public string Describe()
    {
        var name = string.IsNullOrWhiteSpace(Name) ? "unnamed" : Name;
        var id = string.IsNullOrWhiteSpace(Id) ? "no id" : Id;
        var where = HasLocation ? $"{Latitude:0.####},{Longitude:0.####}" : "no coordinates";
        var chargers = Chargers.Count == 0
            ? "no charger"
            : string.Join(", ", Chargers.Select(charger => charger.Describe()));

        return $"{name} ({id}) at {where}; inverter {Inverter.Describe()}; charger {chargers}";
    }
}

/// <summary>
/// One Modbus device on the system: what it is, what it is called, and how to reach it.
/// </summary>
/// <param name="Id">
/// The device's identity within the system — the key its <c>IModbusClient</c> is registered under.
/// </param>
/// <param name="Name">Display name, or empty.</param>
/// <param name="Model">
/// What the box is. Reported, not acted on — see <see cref="PvDeviceOptions.Model"/> for why that
/// distinction matters more than it looks.
/// </param>
/// <param name="Connection">Where and how to reach it, as the Modbus client wants it.</param>
public sealed record PvDeviceInfo(string Id, string Name, string Model, DeviceConfig Connection)
{
    /// <summary>The device in a log line: the model if it is known, and always the address.</summary>
    public string Describe()
    {
        var what = string.IsNullOrWhiteSpace(Model) ? Id : Model;
        return $"{what} at {Connection.Host}:{Connection.Port}";
    }
}
