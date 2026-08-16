namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// The runtime on/off state of the battery discharge hold, mirroring
/// <see cref="IChargeControlModeSelector"/>. Seeded from configuration at startup, then changeable at
/// runtime (e.g. from Home Assistant) without restarting. The polling loop reads <see cref="Hold"/>
/// each cycle.
///
/// <para>Deliberately orthogonal to the charge-control mode: the hold is about where the house's
/// power comes from, not about the charger, and either can be on without the other.</para>
/// </summary>
public interface IBatteryHoldSelector
{
    /// <summary>Whether the hold is currently requested.</summary>
    bool Hold { get; }

    /// <summary>
    /// Sets the hold. <paramref name="source"/> names who changed it (for logging). No-op (and no
    /// event) if the value is unchanged.
    /// </summary>
    void Set(bool hold, string source);

    /// <summary>Raised when the hold actually changes, with the new value.</summary>
    event Action<bool>? Changed;
}
