using Solax.Core.Enums;

namespace Solax.Core.Interfaces;

/// <summary>
/// The runtime charge-control mode (Off / Solar / Force). Seeded from configuration at startup, then
/// changeable at runtime (e.g. from Home Assistant) without restarting. The polling loop reads
/// <see cref="Mode"/> each cycle.
/// </summary>
public interface IChargeControlModeSelector
{
    /// <summary>The current mode.</summary>
    ChargeControlMode Mode { get; }

    /// <summary>
    /// Sets the mode. <paramref name="source"/> names who changed it (for logging). No-op (and no
    /// event) if the value is unchanged.
    /// </summary>
    void Set(ChargeControlMode mode, string source);

    /// <summary>Raised when the mode actually changes, with the new value.</summary>
    event Action<ChargeControlMode>? Changed;
}
