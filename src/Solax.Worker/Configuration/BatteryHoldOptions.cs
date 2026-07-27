namespace Solax.Worker.Configuration;

/// <summary>
/// Configuration for the battery discharge hold (issue #20). Bound from the <c>"BatteryHold"</c>
/// section. Disabled by default: this is the only feature that writes to the <b>inverter</b>, and the
/// register addresses and field layout must be verified against your device before it is safe to
/// enable (see <c>InverterControlRegister</c> and docs/DECISIONS.md).
/// </summary>
public sealed class BatteryHoldOptions
{
    public const string SectionName = "BatteryHold";

    /// <summary>
    /// Master switch for the feature, off by default. Unlike <see cref="ChargeControlOptions.Enabled"/>
    /// this is <b>not</b> merely a boot default: while it is false the feature is entirely inert — no
    /// Home Assistant switch is published, the poll loop skips it, and the inverter's Modbus client is
    /// wrapped read-only so no inverter write is even possible. Use <see cref="HoldAtStartup"/> for
    /// the boot value of the hold itself.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// The boot value of the hold, for running without Home Assistant. Off by default. A runtime
    /// change from HA does not persist across restarts — the same contract as the charge-control mode.
    /// </summary>
    public bool HoldAtStartup { get; init; }

    /// <summary>
    /// When true (and <see cref="Enabled"/>), the hold is decided and logged — including the encoded
    /// register values it would write — but no Modbus write is performed. Use it to validate the
    /// power-control block against your inverter before letting it write for real. Defaults to
    /// <c>true</c>: enabling the feature without saying anything else gets you the safe option.
    /// </summary>
    public bool DryRun { get; init; } = true;

    /// <summary>
    /// How long each issued command stays armed before the inverter drops it. This is the failsafe:
    /// if the service stops, nothing renews the command and the inverter returns to normal operation
    /// within this window. Renewal happens at half of it, so it must comfortably exceed
    /// <c>Solax:PollIntervalSeconds</c>. Hardware maximum is 8 hours (u16 seconds, 28800).
    /// </summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How far the computed active-power target must move (in watts) before the command is reissued.
    /// The target tracks live house load and PV, so without this every poll would be a write. The
    /// command is not EEPROM-backed, so this is about Modbus traffic and log noise, not wear.
    /// </summary>
    public double TargetChangeThresholdWatts { get; init; } = 100;
}
