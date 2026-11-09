namespace Gleanvolt.Devices;

/// <summary>
/// What the EV charger reports it is doing, in OCPP 1.6's status names. Each charger driver maps its own
/// family's state onto these (issue #348). The values happen to match the SolaX charger's RunMode
/// register (0-13), which follows the same names, so its driver maps by a cast.
///
/// <para>Read carefully rather than at face value: three of these look like "the car is done" and only
/// one of them means it — see <c>EvChargerStatusExtensions.IsCarKnownDisconnected</c> in Core and the
/// members below.</para>
/// </summary>
public enum EvChargerStatus
{
    /// <summary>
    /// The charger did not answer, or answered with a value outside the register's range. The
    /// <b>absence of information</b>, not a fact about the car — a dropped Modbus read must never be
    /// read as an unplugged car.
    /// </summary>
    Unknown = -1,

    /// <summary>Ready, with no car plugged in.</summary>
    Available = 0,

    /// <summary>A car is plugged in and the two are still negotiating; no energy is flowing yet.</summary>
    Preparing = 1,

    /// <summary>Delivering energy to the car.</summary>
    Charging = 2,

    /// <summary>The session is closing. The car's own doing, so it counts as the car having finished.</summary>
    Finishing = 3,

    /// <summary>The charger reports a fault.</summary>
    Faulted = 4,

    /// <summary>The charger is out of service.</summary>
    Unavailable = 5,

    /// <summary>Reserved for a specific user or session.</summary>
    Reserved = 6,

    /// <summary>
    /// The <b>car</b> has stopped drawing, typically because it reached its own charge limit. The
    /// car's decision, so it counts as finished.
    /// </summary>
    SuspendedEv = 7,

    /// <summary>
    /// The <b>charger</b> has stopped delivering. Deliberately <em>not</em> treated as a finished
    /// charge: this is what our own pause write produces, and mistaking it for one would let the
    /// controller read its own pause as the car being full.
    /// </summary>
    SuspendedEvse = 8,

    /// <summary>The charger is updating its firmware.</summary>
    Update = 9,

    /// <summary>Waiting for an RFID card to authorise the session.</summary>
    CardActivation = 10,

    /// <summary>Waiting on a start delay — the charger's own timer, not ours.</summary>
    StartDelay = 11,

    /// <summary>
    /// Charging is paused at the charger. Excluded from "the car is done" for the same reason as
    /// <see cref="SuspendedEvse"/>: it is the charger's doing, and usually ours.
    /// </summary>
    ChargePaused = 12,

    /// <summary>The session is stopping.</summary>
    Stopping = 13,
}
