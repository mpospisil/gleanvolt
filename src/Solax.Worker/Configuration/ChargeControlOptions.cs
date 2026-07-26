namespace Solax.Worker.Configuration;

/// <summary>
/// Configuration for EV charge control. Charging is driven from live PV surplus while the home
/// battery is full (see <see cref="BatteryFullSocPercent"/>). Bound from the <c>"ChargeControl"</c>
/// section. Disabled by default: this feature writes to the charger hardware, and the control
/// register addresses must be verified against your device before it is safe to enable.
/// </summary>
public sealed class ChargeControlOptions
{
    public const string SectionName = "ChargeControl";

    /// <summary>Master on/off switch. Off by default (writes to hardware — verify registers first).</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// When true (and <see cref="Enabled"/>), the control loop runs and logs exactly what it would
    /// write (mode and the encoded current-register value) but performs no Modbus writes. Use it to
    /// validate the setpoints against your charger before letting it write for real.
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>Voltage used to convert between watts and the charger's amp setpoint.</summary>
    public double NominalVoltage { get; init; } = 230;

    /// <summary>
    /// Number of phases the charger charges on. 1 for single-phase, 3 for three-phase (e.g. X3-HAC).
    /// One amp = NominalVoltage × Phases watts, so the 6 A minimum is ~1.4 kW single-phase but ~4.2 kW
    /// three-phase. Set this to match your charger or the setpoint and minimum floor will be wrong.
    /// </summary>
    public int Phases { get; init; } = 1;

    /// <summary>Minimum viable charging current (below which charging is paused).</summary>
    public int MinChargingCurrentAmps { get; init; } = 6;

    /// <summary>Maximum charging current the charger accepts (setpoint is clamped to this).</summary>
    public int MaxChargingCurrentAmps { get; init; } = 20;

    /// <summary>Granularity of the current setpoint the hardware accepts, in amps.</summary>
    public int CurrentStepAmps { get; init; } = 1;

    /// <summary>
    /// The current setpoint written to pause charging (the controller only changes the current, never
    /// the use-mode). 0 A suspends the car like Green mode does — but the SolaX docs list a 6–32 A
    /// range, so if your charger doesn't accept 0, set a sub-6 A value the car refuses instead.
    /// </summary>
    public int PauseCurrentAmps { get; init; }

    /// <summary>
    /// How far back the solar surplus is averaged before it drives any decision. Raw PV is erratic,
    /// so decisions are made on this rolling average rather than the instantaneous value — a brief
    /// cloud then can't interrupt a long charging session.
    /// </summary>
    public TimeSpan SurplusAverageWindow { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Minimum change (in amps) between the current setpoint and a new target before a Modbus write
    /// is issued. At the default 1 A, a charger sitting at 10 A is only re-commanded once the average
    /// calls for 11 A or 9 A. Raise it to damp the charger further.
    /// </summary>
    public int CurrentChangeThresholdAmps { get; init; } = 1;

    /// <summary>
    /// Extra surplus (watts) required above the minimum before charging (re)starts, so a surplus
    /// hovering near the minimum doesn't flap the charger on and off.
    /// </summary>
    public double ResumeHysteresisWatts { get; init; } = 200;

    /// <summary>
    /// LiveSolar strategy only: battery SOC (%) at or above which live-solar charging engages.
    /// </summary>
    public double BatteryFullSocPercent { get; init; } = 95;

    /// <summary>
    /// LiveSolar strategy only: once charging, the SOC (%) it must fall below to disengage the gate.
    /// Sits below <see cref="BatteryFullSocPercent"/> to form a hysteresis band (the car drawing solar
    /// can dip the battery slightly). Must not exceed <see cref="BatteryFullSocPercent"/>.
    /// </summary>
    public double BatteryReleaseSocPercent { get; init; } = 90;
}
