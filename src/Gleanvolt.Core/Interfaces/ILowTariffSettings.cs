using Gleanvolt.Core.Enums;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// The owner's <b>low tariff only</b> option for the modes that buy from the grid (issue #274): with it
/// on, <see cref="ChargeControlMode.FastNoBattery"/> and <see cref="ChargeControlMode.SolarGrid"/> wait
/// through high tariff and charge in low. Seeded from configuration and settable at runtime from the web
/// UI and Home Assistant, exactly like <see cref="ISolarGridSettings"/> — and, like it, a runtime change
/// does not persist across restarts.
///
/// <para>A flag is only a wish. With no tariff selected there is nothing to wait for, and the flag is
/// ignored: the host says so in the status rather than pretending the mode is waiting.</para>
/// </summary>
public interface ILowTariffSettings
{
    /// <summary>Whether <paramref name="mode"/> waits for low tariff. False for every mode that has no such option.</summary>
    bool IsLowTariffOnly(ChargeControlMode mode);

    /// <summary>
    /// Sets the option for <paramref name="mode"/>, one of <see cref="LowTariffModes.All"/>.
    /// <paramref name="source"/> names who changed it, for logging.
    /// </summary>
    void SetLowTariffOnly(ChargeControlMode mode, bool on, string source);
}

/// <summary>The modes that have a low-tariff-only option, and nothing else does.</summary>
public static class LowTariffModes
{
    /// <summary>The two modes whose grid share can be bought in low tariff.</summary>
    public static IReadOnlyList<ChargeControlMode> All { get; } = [ChargeControlMode.FastNoBattery, ChargeControlMode.SolarGrid];

    /// <summary>Whether <paramref name="mode"/> is one of them.</summary>
    public static bool Has(ChargeControlMode mode) => All.Contains(mode);
}
