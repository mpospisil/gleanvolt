using Gleanvolt.Core.Enums;

namespace Gleanvolt.Core.Models;

/// <summary>
/// One stretch of the plan: from when to when, from which source, at what power, for how much energy.
///
/// <para>Solar and grid blocks may <b>overlap in time</b>, and that is not a bug. Where the grid block
/// reaches back over a sunny slice the sun keeps supplying what it can and the grid only makes up the
/// difference to the charger's maximum power — so the two blocks describe one charging period with two
/// contributors, and their energies do not double-count.</para>
/// </summary>
/// <param name="Start">When this contribution starts.</param>
/// <param name="End">When it ends.</param>
/// <param name="Source">Sun or grid.</param>
/// <param name="PowerWatts">The average power this block contributes over its span.</param>
/// <param name="EnergyWh">The energy it contributes — <paramref name="PowerWatts"/> over its span.</param>
public sealed record TargetedChargeBlock(
    DateTimeOffset Start,
    DateTimeOffset End,
    TargetedChargeSource Source,
    double PowerWatts,
    double EnergyWh)
{
    /// <summary>How long this block runs for.</summary>
    public TimeSpan Duration => End - Start;

    /// <summary>Whether <paramref name="instant"/> falls inside <c>[Start, End)</c>.</summary>
    public bool Covers(DateTimeOffset instant) => instant >= Start && instant < End;
}
