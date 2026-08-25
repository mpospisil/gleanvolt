using Gleanvolt.Core.Models;

namespace Gleanvolt.Hosting.Configuration;

/// <summary>
/// What is left of the vendor-named section while the installation moves into <c>Pv</c> (issue #111).
///
/// <para><see cref="Inverter"/> and <see cref="EvCharger"/> are <b>deprecated</b>: an inverter and a
/// wallbox are what the PV system is made of, and they are described by <c>Pv:Inverter</c> and
/// <c>Pv:Chargers</c> from now on. They stay here, and stay authoritative wherever they are set, so
/// that an existing deployment upgrades with an untouched <c>.env</c> and behaves exactly as it did —
/// see <see cref="PvSystemResolver"/>, which is the only thing that reads them.</para>
///
/// <para><see cref="PollIntervalSeconds"/> is not deprecated and is not a fact about the hardware: it
/// is how often we choose to ask. It moves to the controller's own section when the devices leave.</para>
/// </summary>
public sealed class SolaxOptions
{
    public const string SectionName = "Solax";

    /// <summary>Deprecated; use <c>Pv:Inverter</c>. Null once the installation is described there.</summary>
    public DeviceConfig? Inverter { get; init; }

    /// <summary>Deprecated; use <c>Pv:Chargers</c>. Null once the installation is described there.</summary>
    public DeviceConfig? EvCharger { get; init; }

    public int PollIntervalSeconds { get; init; } = 5;

}
