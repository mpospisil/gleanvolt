using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// Decides, from the live energy state and the solar forecast, what the EV charger should be doing
/// this cycle: fast-charge at a computed setpoint, pause, restore the owner's original settings, or
/// nothing. Pure and side-effect free — it reads inputs and returns intent; the orchestrator is
/// responsible for reading current settings, applying writes only on change, backup/restore, and
/// logging.
/// </summary>
public interface IChargingController
{
    ChargingControlDecision Decide(ChargingControlInput input);
}
