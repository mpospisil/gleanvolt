namespace Gleanvolt.Core.Enums;

/// <summary>
/// Whether the car says a cable is in its socket, normalised across sources.
///
/// <para>Note this is the <b>car's</b> view, which is not the same fact as the charger's
/// <c>EvChargerStatus</c> — the charger knows a cable is connected to <i>it</i>, the car knows a
/// cable is connected to <i>itself</i>. On a single-charger site they agree; they are still separate
/// readings from separate devices, and only the charger's is fresh at LAN speed.</para>
/// </summary>
public enum VehiclePlugState
{
    /// <summary>Not reported, or a value this vocabulary doesn't cover.</summary>
    Unknown = 0,

    /// <summary>No cable in the car's socket.</summary>
    Disconnected,

    /// <summary>A cable is in the car's socket. Says nothing about whether power is flowing.</summary>
    Connected,
}
