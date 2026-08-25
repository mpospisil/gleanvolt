namespace Gleanvolt.Core.Models;

/// <summary>
/// The installation itself, bound from the <c>"Pv"</c> configuration section (issue #111): where the
/// array is, what it is made of, and what to call it.
///
/// <para>It lives in <c>Gleanvolt.Core</c> rather than beside a feature's options because it belongs
/// to no feature: the weather client wants its coordinates, the composition root wants its devices,
/// and the API and the web UI want its name. Core is the one assembly all of them may reference.</para>
///
/// <para><b>Nothing here is required.</b> An empty section is a valid state — it is what an existing
/// deployment upgrades into, still described by the older keys (<c>Solax:Inverter</c>,
/// <c>Solax:EvCharger</c>, <c>Weather:Latitude</c>, <c>Weather:Longitude</c>). The rules for reconciling
/// the two, and every validation message, live in the host's <c>PvSystemResolver</c>; this type is only
/// the shape of the section. What comes out of that resolution is <see cref="PvSystemInfo"/>, and that
/// is what the rest of the codebase reads.</para>
/// </summary>
public sealed class PvSystemOptions
{
    public const string SectionName = "Pv";

    /// <summary>
    /// The system's stable identity: a slug (<c>a-z</c>, <c>0-9</c>, <c>-</c>, <c>_</c>) chosen once and
    /// never changed casually, because it is what an MQTT topic, a Home Assistant device and anything
    /// upstream in the cloud agree on.
    ///
    /// <para>Optional while nothing consumes it — the topics still carry the fixed
    /// <c>HomeAssistant:DeviceId</c>. It becomes required in the phase that puts it in a topic, which
    /// is also the first phase in which changing it would cost anything.</para>
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>What a human calls this system ("Home Roof"). Free text; never used as a key.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// The postal address, for a person reading a dashboard. Deliberately never parsed or geocoded —
    /// <see cref="Latitude"/> and <see cref="Longitude"/> are what anything computes from.
    /// </summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>
    /// The site's latitude in decimal degrees. Null rather than 0 when unset, on the reasoning already
    /// written on <c>WeatherOptions.Latitude</c>: 0,0 is a real place in the Atlantic, and a defaulted
    /// coordinate silently records the weather in the Gulf of Guinea rather than saying nothing.
    /// </summary>
    public double? Latitude { get; init; }

    /// <summary>The site's longitude in decimal degrees. Null when unset — see <see cref="Latitude"/>.</summary>
    public double? Longitude { get; init; }

    /// <summary>
    /// The direction the array faces, as a compass bearing from true north: 0 = north, 90 = east,
    /// 180 = south, 270 (or -90) = west. Stored normalised into [0, 360).
    ///
    /// <para><b>Not yet sent anywhere.</b> Nothing computes from this today — the forecast is fetched by
    /// Solcast resource id, which already encodes the orientation in Solcast's account. Before this
    /// value is ever passed to a provider, check that provider's own azimuth convention against this
    /// one: an array described 180° from where it points still produces a plausible-looking forecast,
    /// which is why this is the field that goes silently wrong rather than loudly wrong.</para>
    /// </summary>
    public double? AzimuthDegrees { get; init; }

    /// <summary>The array's tilt from horizontal, in degrees: 0 = flat, 90 = vertical.</summary>
    public double? TiltDegrees { get; init; }

    /// <summary>Peak DC capacity of the array in kWp.</summary>
    public double? CapacityKwp { get; init; }

    /// <summary>
    /// The inverter's AC-side capacity in kW, where it is smaller than the array. Optional: it says
    /// where a forecast should be clipped, and nothing else.
    /// </summary>
    public double? InverterCapacityKw { get; init; }

    /// <summary>System loss factor in (0, 1] — the fraction of DC yield that reaches the meter.</summary>
    public double? LossFactor { get; init; }

    /// <summary>
    /// When the system was commissioned, as <c>yyyy-MM-dd</c>. A string rather than a
    /// <see cref="DateOnly"/> so that an unparsable value is reported as a named configuration error
    /// instead of a binder exception with no key in it.
    /// </summary>
    public string InstallDate { get; init; } = string.Empty;

    /// <summary>The inverter this array feeds. Null when the older <c>Solax:Inverter</c> keys still describe it.</summary>
    public PvInverterOptions? Inverter { get; init; }

    /// <summary>
    /// The EV chargers on this system. A list from the start although <b>exactly one is supported</b> —
    /// a second entry is a startup failure, not a silently ignored one — so that the day the control
    /// logic can drive two, the configuration does not have to change shape. An array rather than a
    /// <c>List</c> or an <c>IReadOnlyList</c> because that is what the configuration binder is certain
    /// to populate.
    /// </summary>
    public PvChargerOptions[] Chargers { get; init; } = [];
}

/// <summary>
/// What every Modbus device on the system has: what it is, and how to reach it. The connection half
/// mirrors <see cref="DeviceConfig"/> rather than nesting one, so the configuration path stays
/// <c>Pv:Inverter:Host</c> instead of <c>Pv:Inverter:Connection:Host</c>; the defaults are taken from
/// <see cref="DeviceConfig"/> itself in <see cref="ToDeviceConfig"/>, so there is still only one place
/// that decides what a port or a request interval defaults to.
/// </summary>
public abstract class PvDeviceOptions
{
    /// <summary>
    /// What the box is — <c>SolaX X3-HYB-G4 PRO</c>, <c>SolaX X3-HAC</c>.
    ///
    /// <para><b>Documentation today, a selector later.</b> The register maps are compiled for those two
    /// devices and are chosen by nothing, so putting another model here does not make the controller
    /// speak that device's dialect — it only mislabels the one it is speaking to. What it does do is
    /// answer "what is on the other end of 192.168.2.10?" without an ssh session. The day a second
    /// register map exists this is the field that picks it, and it becomes a known-value check with a
    /// startup failure for anything unrecognised.</para>
    /// </summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>What a human calls this device ("Garage wallbox"). Display only.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The device's hostname or IP address. Required for a device that is configured at all.</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>Modbus TCP port. Null takes <see cref="DeviceConfig"/>'s default.</summary>
    public int? Port { get; init; }

    /// <summary>Modbus unit id. Null takes <see cref="DeviceConfig"/>'s default.</summary>
    public byte? UnitId { get; init; }

    /// <summary>
    /// Minimum gap between consecutive requests to this device. Null takes <see cref="DeviceConfig"/>'s
    /// default — see there for why the SolaX protocol needs one at all.
    /// </summary>
    public TimeSpan? MinRequestInterval { get; init; }

    // The defaults live on DeviceConfig; this instance exists only to read them off, so that adding a
    // knob there does not require remembering to repeat its default here.
    private static readonly DeviceConfig Defaults = new() { Host = string.Empty };

    /// <summary>The connection as the Modbus client wants it, with unset values taking their defaults.</summary>
    public DeviceConfig ToDeviceConfig() => new()
    {
        Host = Host,
        Port = Port ?? Defaults.Port,
        UnitId = UnitId ?? Defaults.UnitId,
        MinRequestInterval = MinRequestInterval ?? Defaults.MinRequestInterval,
    };
}

/// <summary>The system's inverter. No id: a PV system has one, and it is addressed as such.</summary>
public sealed class PvInverterOptions : PvDeviceOptions;

/// <summary>One EV charger on the system.</summary>
public sealed class PvChargerOptions : PvDeviceOptions
{
    /// <summary>
    /// This charger's identity within the system: the key its Modbus client is registered under, and
    /// the sub-topic it will publish under once there can be two. Slug-shaped, like
    /// <see cref="PvSystemOptions.Id"/>, and unique within the list. Defaults to <c>charger</c>.
    /// </summary>
    public string Id { get; init; } = string.Empty;
}
