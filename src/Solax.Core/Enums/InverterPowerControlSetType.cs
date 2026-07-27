namespace Solax.Core.Enums;

/// <summary>
/// The <c>set_type</c> field of the Modbus Power Control block. Upstream always sends
/// <see cref="Set"/> "for simplicity", so we do the same.
/// </summary>
public enum InverterPowerControlSetType : ushort
{
    /// <summary>Absolute: the command replaces whatever target was active.</summary>
    Set = 1,

    /// <summary>Relative update to the active target. Unused here.</summary>
    Update = 2,
}
