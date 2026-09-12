namespace Gleanvolt.Core.Enums;

/// <summary>
/// One phase of a three-phase grid connection. SolaX names them R, S and T; the values follow that order,
/// so <see cref="L1"/> is the inverter's R-phase register.
/// </summary>
public enum GridPhase
{
    L1 = 0,
    L2 = 1,
    L3 = 2,
}
