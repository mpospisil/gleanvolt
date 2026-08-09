namespace Solax.Core.Enums;

/// <summary>Why a recorded charging session stopped. Set once, when the session is closed.</summary>
public enum ChargingSessionEndReason
{
    /// <summary>The charge-control mode returned to <see cref="ChargeControlMode.Off"/>.</summary>
    ModeOff,

    /// <summary>
    /// The controller ended the mode itself because the car had finished — today only
    /// <see cref="ChargeControlMode.FastNoBattery"/> does this. Distinguished from
    /// <see cref="ModeOff"/> because "the car is full" and "somebody switched it off" are the two
    /// outcomes worth telling apart when reading a session back.
    /// </summary>
    SessionComplete,

    /// <summary>The car was unplugged.</summary>
    CarUnplugged,

    /// <summary>The service shut down gracefully while the session was open.</summary>
    ServiceStopped,

    /// <summary>
    /// The session was still open when the service next started, so it was ended retroactively at its
    /// last recorded sample: a crash, a power cut, or a container replaced mid-session.
    /// </summary>
    Interrupted,
}
