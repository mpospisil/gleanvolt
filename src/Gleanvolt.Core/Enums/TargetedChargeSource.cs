namespace Gleanvolt.Core.Enums;

/// <summary>Where the energy in a <see cref="Models.TargetedChargeBlock"/> is planned to come from.</summary>
public enum TargetedChargeSource
{
    /// <summary>Forecast surplus the home battery has not already claimed.</summary>
    Solar,

    /// <summary>Imported energy: whatever the charger's maximum power leaves above the sun of the moment.</summary>
    Grid,
}
