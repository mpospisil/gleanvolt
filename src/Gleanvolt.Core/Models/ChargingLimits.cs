namespace Gleanvolt.Core.Models;

/// <summary>
/// What the charger and the car will <b>both</b> accept: the intersection of the installation's limits
/// and the vehicle's (issue #124).
///
/// <para><b>One place, deliberately.</b> The rule is three lines of arithmetic and it would be
/// entirely reasonable to write it at each call site — which is exactly how the four call sites end up
/// disagreeing about whether "the maximum" means the wallbox's or the car's. Everything that needs a
/// current or a phase count asks this.</para>
///
/// <para><b>A car can only ever narrow the band.</b> Nothing here raises a ceiling: an installation
/// limited to 16 A stays limited to 16 A behind a car that would take 32, because that limit is the
/// site's supply and the wiring in the wall. The car's figures are a second constraint, never a
/// replacement for the first.</para>
///
/// <para><b>An unstated figure narrows nothing.</b> A car described only by its pack size charges
/// exactly as it did before anybody described it.</para>
/// </summary>
/// <param name="MinAmps">The lowest current both will accept — whichever refuses first.</param>
/// <param name="MaxAmps">The highest current both will accept — whichever gives out first.</param>
/// <param name="Phases">The phases both can use — whichever offers fewer.</param>
public sealed record ChargingLimits(int MinAmps, int MaxAmps, int Phases)
{
    /// <summary>
    /// Narrows the installation's limits by the car's.
    /// </summary>
    /// <param name="chargerMinAmps">The charger's floor — the current below which it will not run.</param>
    /// <param name="chargerMaxAmps">The installation's ceiling: the site's supply limit.</param>
    /// <param name="chargerPhases">How many phases the wallbox charges on.</param>
    /// <param name="ev">The car, or <see cref="EvInfo.Unknown"/> when none is described.</param>
    public static ChargingLimits Intersect(int chargerMinAmps, int chargerMaxAmps, int chargerPhases, EvInfo ev)
    {
        ArgumentNullException.ThrowIfNull(ev);

        var min = Math.Max(chargerMinAmps, ev.MinChargingCurrentAmps ?? chargerMinAmps);
        var max = Math.Min(chargerMaxAmps, ev.MaxChargingCurrentAmps ?? chargerMaxAmps);
        var phases = Math.Min(chargerPhases, ev.Phases ?? chargerPhases);

        return new ChargingLimits(min, max, phases);
    }

    /// <summary>
    /// Whether the band is empty — a floor above the ceiling, so no current satisfies both. It means
    /// the car can never charge here, and it is a configuration mistake rather than a runtime state:
    /// the resolver refuses it at startup naming both keys, because discovering it at 23:00 as "nothing
    /// happens" is the failure this whole validation posture exists to prevent.
    /// </summary>
    public bool IsEmpty => MinAmps > MaxAmps;
}
