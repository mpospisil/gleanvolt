using Gleanvolt.Core.Enums;

namespace Gleanvolt.Core.Models;

/// <summary>
/// The charger's controllable settings that we read, back up, compare against, and write:
/// its use-mode and target charging current. Used both to capture the owner's original
/// configuration (for restore on disconnect) and to express the setpoint the controller wants.
/// Current is a whole number of amps because that is the granularity the charger's setpoint
/// register accepts.
/// </summary>
public sealed record EvChargerSettings(EvChargerMode Mode, int ChargeCurrentAmps);
