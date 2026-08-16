namespace Gleanvolt.Core.Enums;

/// <summary>A coarse, display-friendly summary of what charge control is doing right now.</summary>
public enum ChargeControlState
{
    /// <summary>Control is switched off; the charger is left to the owner.</summary>
    Disabled,

    /// <summary>Enabled, but conditions aren't met (battery not full, no surplus, or not controllable).</summary>
    Idle,

    /// <summary>Actively driving the charger from solar surplus.</summary>
    Charging,

    /// <summary>Held control but suspended charging (surplus below the minimum).</summary>
    Paused,
}
