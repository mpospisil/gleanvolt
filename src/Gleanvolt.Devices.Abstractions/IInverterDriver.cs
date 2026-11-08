namespace Gleanvolt.Devices;

/// <summary>
/// One inverter family, read and controlled over a Modbus client the host owns (issue #317). The driver
/// knows the family's registers, encodings and control procedures; nothing outside it does. What it hands
/// back is in fixed units and signs (see <see cref="InverterReading"/>), so no code downstream of a driver
/// knows which brand is on the wall.
///
/// <para>A driver never opens a socket. The host builds the client, and in dry-run hands it a client that
/// refuses every write, so a driver cannot write even by mistake.</para>
/// </summary>
public interface IInverterDriver
{
    /// <summary>What this family can do, so the host can tell a missing feature from a broken one.</summary>
    InverterCapabilities Capabilities { get; }

    /// <summary>
    /// The battery hold for this family, or null when it has none. Present exactly when
    /// <see cref="InverterCapabilities.HasBatteryHold"/> is true.
    /// </summary>
    IBatteryDischargeControl? BatteryHold { get; }

    /// <summary>
    /// One telemetry poll. Connects first if the client is not connected. Throws when the inverter cannot
    /// be read: without the inverter there is no telemetry worth reporting.
    /// </summary>
    Task<InverterReading> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the device on the client is this family's inverter: "Test connection" in Settings and the
    /// setup wizard. Connects first if the client is not connected, reads as little as tells the family
    /// apart, and never writes. Throws when nothing answers; a device that answers with something this
    /// family would not report is a result, not an exception.
    /// </summary>
    Task<InverterProbeResult> ProbeAsync(CancellationToken cancellationToken = default);
}

/// <summary>What an inverter family supports.</summary>
/// <param name="HasBatteryHold">
/// Whether the family has a battery hold (<see cref="IInverterDriver.BatteryHold"/> is not null).
/// </param>
/// <param name="HasPhaseOutput">
/// Whether readings carry the inverter's output per phase (<see cref="InverterReading.InverterOutputPerPhase"/>
/// is not null). The correction for a grid meter blind on one phase needs it.
/// </param>
public sealed record InverterCapabilities(bool HasBatteryHold, bool HasPhaseOutput);

/// <summary>
/// One inverter poll in the contract's units and signs: watts and percent, with grid <b>import positive</b>
/// and battery <b>charging positive</b>. The driver converts from whatever its family reports.
/// </summary>
/// <param name="PvWatts">Solar production, all strings together.</param>
/// <param name="GridWatts">The grid meter at the connection point; positive = importing.</param>
/// <param name="BatteryWatts">The battery; positive = charging, negative = discharging.</param>
/// <param name="BatterySocPercent">The battery's state of charge, 0–100.</param>
/// <param name="InverterOutputPerPhase">
/// The inverter's own AC output on each phase, positive = into the house; null for a family that does not
/// report it. Not the grid meter.
/// </param>
public sealed record InverterReading(
    double PvWatts,
    double GridWatts,
    double BatteryWatts,
    double BatterySocPercent,
    PhaseWatts? InverterOutputPerPhase);

/// <summary>What <see cref="IInverterDriver.ProbeAsync"/> found at an address that answered.</summary>
/// <param name="Recognised">Whether what was read is something this family's inverter would report.</param>
/// <param name="Reading">
/// The value read: the battery's state of charge in percent when <paramref name="Recognised"/>, otherwise
/// the raw value, for a message that says what came back instead.
/// </param>
public sealed record InverterProbeResult(bool Recognised, double Reading);
