namespace Gleanvolt.Core.Enums;

/// <summary>
/// Why a recorded charging session stopped. Set once, when the session is closed.
///
/// <para>Stored by name, so members are only ever appended and a row written before a member existed
/// still parses. A session a controller ended itself carries that controller's own reason (#198). Before
/// that every such session was filed as <see cref="SessionComplete"/>, and rows written then cannot be told
/// apart after the fact.</para>
/// </summary>
public enum ChargingSessionEndReason
{
    /// <summary>The charge-control mode returned to <see cref="ChargeControlMode.Off"/>: somebody switched it off.</summary>
    ModeOff,

    /// <summary>
    /// The car stopped drawing on its own limit while it was being asked to charge, and the mode ended
    /// itself. <see cref="ChargeControlMode.FastNoBattery"/>, <see cref="ChargeControlMode.Targeted"/> and
    /// <see cref="ChargeControlMode.SolarGrid"/> all detect it. Distinguished from <see cref="ModeOff"/>
    /// because "the car is full" and "somebody switched it off" are the two outcomes worth telling apart
    /// when reading a session back. Sessions recorded before #198 carry this for every self-ended session,
    /// whatever ended it.
    /// </summary>
    SessionComplete,

    /// <summary>The car was unplugged, whether the mode noticed it and ended itself or the session did.</summary>
    CarUnplugged,

    /// <summary>The service shut down gracefully while the session was open.</summary>
    ServiceStopped,

    /// <summary>
    /// The session was still open when the service next started, so it was ended retroactively at its
    /// last recorded sample: a crash, a power cut, or a container replaced mid-session.
    /// </summary>
    Interrupted,

    /// <summary>
    /// The energy asked for was delivered and the mode ended itself: <see cref="ChargeControlMode.Targeted"/>,
    /// or <see cref="ChargeControlMode.FastNoBattery"/> with an amount set.
    /// </summary>
    TargetReached,

    /// <summary>
    /// The departure time passed before the energy asked for was delivered, and the mode ended itself:
    /// <see cref="ChargeControlMode.Targeted"/>, or <see cref="ChargeControlMode.FastNoBattery"/> with a
    /// departure set.
    /// </summary>
    DeparturePassed,

    /// <summary>
    /// <see cref="ChargeControlMode.SolarGrid"/> ended itself for the day: the live surplus was under its
    /// minimum and the forecast had nothing left today that would clear it. It says nothing about the car,
    /// which may be far from full.
    /// </summary>
    NoSunLeftToday,

    /// <summary>
    /// The charger stayed out of Fast for longer than a Modbus dropout lasts, so it was no longer the mode's
    /// to drive and the mode ended itself: the wallbox went to Stop after the car finished, or its owner set
    /// another use-mode.
    /// </summary>
    ChargerTakenOver,
}
