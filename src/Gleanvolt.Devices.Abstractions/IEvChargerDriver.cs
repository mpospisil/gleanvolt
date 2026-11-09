namespace Gleanvolt.Devices;

/// <summary>
/// One EV charger family, read and controlled over a connection the host owns (issue #348), as
/// <see cref="IInverterDriver"/> is for inverters. The driver knows the family's registers, encodings and
/// states; nothing outside it does. What it hands back is in fixed units (watts, amps) and a status from
/// <see cref="EvChargerStatus"/>, so no code downstream of a driver knows which brand is on the wall.
///
/// <para><b>Start and stop are one switch.</b> The controller only ever needs to let the charger charge or
/// stop it (<see cref="SetChargingAllowedAsync"/>); each family maps that onto its own control — a use-mode,
/// a force state. Anything else a family can be set to is the owner's, read for display and never written.</para>
///
/// <para>A driver never opens a socket. The host builds the connection, and in dry-run hands it one that
/// refuses every write, so a driver cannot write even by mistake.</para>
/// </summary>
public interface IEvChargerDriver
{
    /// <summary>What this family can do.</summary>
    EvChargerCapabilities Capabilities { get; }

    /// <summary>
    /// One telemetry poll. Connects first if the connection is not open. Throws when the status or power
    /// cannot be read: the caller reports a charger it could not read as <see cref="EvChargerStatus.Unknown"/>,
    /// never as a car that is gone. The setpoint and the mode are best-effort, null when they could not be read.
    /// </summary>
    Task<EvChargerReading> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// What the control loop compares against before it writes: whether charging is allowed, and the
    /// active current setpoint. Unlike <see cref="ReadAsync"/>, throws when either cannot be read, because a
    /// decision on a guessed setpoint is worse than none. In dry-run, a value the driver would have written
    /// stands in for the device's.
    /// </summary>
    Task<EvChargerSettings> ReadSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the charging-current setpoint, but only if it differs from <paramref name="activeAmps"/> (the
    /// freshly read active value) by at least the configured threshold
    /// (<see cref="EvChargerDriverOptions.CurrentChangeThresholdAmps"/>), so the charger is not re-commanded
    /// on every small fluctuation. Clamped to 0..<see cref="EvChargerCapabilities.MaxCurrentAmps"/>; 0 pauses.
    /// The change is logged with <paramref name="reason"/>.
    /// </summary>
    Task SetCurrentAsync(int activeAmps, int targetAmps, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lets the charger charge, or stops it. Unconditional: no hysteresis and no read-back, because "stop"
    /// has to stop whatever the device already said. Never touches the current setpoint. Throws when the
    /// charger does not accept the write. Callers write on a transition, not on every poll.
    /// </summary>
    Task SetChargingAllowedAsync(bool allowed, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the device on the connection is this family's charger: "Test connection". Connects first,
    /// reads as little as tells the family apart, and never writes. Throws when nothing answers; a device
    /// that answers with something this family would not report is a result, not an exception.
    /// </summary>
    Task<EvChargerProbeResult> ProbeAsync(CancellationToken cancellationToken = default);
}

/// <summary>What a charger family supports.</summary>
/// <param name="MinCurrentAmps">The lowest setpoint at which it charges at all (IEC 61851: 6 A).</param>
/// <param name="MaxCurrentAmps">The highest setpoint it accepts.</param>
/// <param name="PauseCurrentAmps">The setpoint that pauses charging without stopping the session.</param>
/// <param name="Phases">How many phases it supplies.</param>
/// <param name="CanSwitchPhases">
/// Whether it can switch between one phase and three while plugged in. Known now so the configuration can be
/// checked; the command itself comes with the first family that has it (go-e, issue #352).
/// </param>
/// <param name="HasUseMode">
/// Whether it has modes of its own beyond charging allowed or not (SolaX's Eco and Green), which readings
/// carry in <see cref="EvChargerReading.Mode"/> for display.
/// </param>
public sealed record EvChargerCapabilities(
    int MinCurrentAmps,
    int MaxCurrentAmps,
    int PauseCurrentAmps,
    int Phases,
    bool CanSwitchPhases,
    bool HasUseMode)
{
    /// <summary>
    /// Whether the family reports what it is doing (issue #351). A family that does not, such as Huawei's
    /// SCharger, shows only that it is delivering: power above zero reads <see cref="EvChargerStatus.Charging"/>,
    /// and anything else <see cref="EvChargerStatus.Unknown"/>. No car and a car plugged in and waiting both
    /// read 0 W, and Unknown is the only status that claims neither.
    /// </summary>
    public bool ReportsStatus { get; init; } = true;
}

/// <summary>One charger poll in the contract's units.</summary>
/// <param name="Status">
/// What the charger reports it is doing, mapped by the driver. For a family without a status
/// (<see cref="EvChargerCapabilities.ReportsStatus"/>), Charging or Unknown.
/// </param>
/// <param name="PowerWatts">What it is delivering to the car, all phases together; never negative.</param>
/// <param name="CurrentAmps">The active current setpoint, or null when it could not be read.</param>
/// <param name="Mode">
/// The family's own name for the mode it is in (SolaX: <c>Fast</c>, <c>Eco</c>), for display and the log
/// only; null when the family has none, or it could not be read or was not one the driver knows. Never a
/// statement of the owner's intent: a link that drops reports junk modes when it recovers.
/// </param>
public sealed record EvChargerReading(
    EvChargerStatus Status,
    double PowerWatts,
    int? CurrentAmps,
    string? Mode);

/// <summary>The settings the control loop reads before it writes.</summary>
/// <param name="ChargingAllowed">
/// Whether the charger is in the state <see cref="IEvChargerDriver.SetChargingAllowedAsync"/> with
/// <c>true</c> puts it in. False for any other state, the owner's own modes included.
/// </param>
/// <param name="ChargeCurrentAmps">The active current setpoint, in whole amps.</param>
/// <param name="Mode">
/// The family's own name for what was read, for the log: <c>Fast</c>, <c>Eco</c>, or the raw value when the
/// driver does not know it. Null for a family without modes.
/// </param>
public sealed record EvChargerSettings(bool ChargingAllowed, int ChargeCurrentAmps, string? Mode = null)
{
    /// <summary>What to call the charger's state in a log line or a reason.</summary>
    public string ModeName => Mode ?? (ChargingAllowed ? "charging allowed" : "charging stopped");
}

/// <summary>What <see cref="IEvChargerDriver.ProbeAsync"/> found at an address that answered.</summary>
/// <param name="Recognised">Whether what was read is something this family's charger would report.</param>
/// <param name="Reading">The raw value read, for a message that says what came back instead.</param>
public sealed record EvChargerProbeResult(bool Recognised, double Reading);
