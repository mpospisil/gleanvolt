using Gleanvolt.Core.Enums;

namespace Gleanvolt.Core.Models;

/// <summary>A power figure for each of the three phases, in watts.</summary>
public sealed record PhaseWatts(double L1, double L2, double L3)
{
    /// <summary>All three phases together.</summary>
    public double Total => L1 + L2 + L3;

    /// <summary>The figure for one phase.</summary>
    public double this[GridPhase phase] => phase switch
    {
        GridPhase.L1 => L1,
        GridPhase.L2 => L2,
        GridPhase.L3 => L3,
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Not a phase."),
    };
}
