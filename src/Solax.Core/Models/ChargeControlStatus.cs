using Solax.Core.Enums;

namespace Solax.Core.Models;

/// <summary>
/// A snapshot of what charge control is doing, for reporting to the outside world (e.g. Home
/// Assistant). Pure data — assembled once per poll and published by the integration layer.
/// </summary>
/// <param name="Mode">The selected mode (Off / Solar / Force).</param>
/// <param name="DryRun">Whether it is in dry-run (deciding and logging, but not writing to the charger).</param>
/// <param name="HoldingControl">Whether it is currently driving the charger (took control of a session).</param>
/// <param name="State">Coarse state for display.</param>
/// <param name="SurplusWatts">The (averaged) solar surplus it is deciding on, or null when not solar-charging.</param>
/// <param name="TargetCurrentAmps">The current it wants to charge at, or null when not charging.</param>
/// <param name="ActiveCurrentAmps">The charger's active current setpoint as read back, or null if unknown.</param>
/// <param name="BatterySocPercent">Home battery state of charge.</param>
/// <param name="ChargerStatus">The charger's status (Available / Preparing / Charging / ChargePaused / ...).</param>
/// <param name="CarConnected">Whether a vehicle is plugged into the charger.</param>
/// <param name="SolarPowerWatts">Actual solar (PV) production.</param>
/// <param name="EvChargerPowerWatts">Actual power the EV charger is drawing.</param>
/// <param name="EvChargingCurrentAmps">Actual charging current, derived from the charger power (phase-aware).</param>
/// <param name="Timestamp">When this snapshot was taken.</param>
public sealed record ChargeControlStatus(
    ChargeControlMode Mode,
    bool DryRun,
    bool HoldingControl,
    ChargeControlState State,
    double? SurplusWatts,
    int? TargetCurrentAmps,
    int? ActiveCurrentAmps,
    double BatterySocPercent,
    EvChargerStatus ChargerStatus,
    bool CarConnected,
    double SolarPowerWatts,
    double EvChargerPowerWatts,
    int EvChargingCurrentAmps,
    DateTimeOffset Timestamp);
