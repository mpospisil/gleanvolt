namespace Gleanvolt.Hosting.Configuration;

/// <summary>
/// Configuration for the fast mode (<c>FastNoBattery</c>) beyond what <see cref="ChargeControlOptions"/>
/// already holds for every mode. Bound from the <c>"ChargeControl:Fast"</c> section, nested like the
/// forecast, targeted and solar-grid sections and for the same reason.
/// </summary>
public sealed class FastChargeOptions
{
    public const string SectionName = "ChargeControl:Fast";

    /// <summary>
    /// The boot default for <b>low tariff only</b> (issue #274): wait through high tariff and charge in
    /// low. Off by default. Settable at runtime from the web UI and Home Assistant; a runtime change
    /// does not survive a restart. Ignored while no tariff is set up on Settings.
    /// </summary>
    public bool LowTariffOnly { get; init; }
}
