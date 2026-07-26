namespace Solax.Core.Enums;

/// <summary>
/// The device-level values of the <c>power_control</c> field, the first register of the Modbus Power
/// Control block (<see cref="InverterControlRegister.PowerControlBlockStart"/>).
///
/// <para><b>There is deliberately no "No Discharge" value here.</b> The upstream Home Assistant
/// integration offers an <c>Enabled No Discharge</c> option, but it never reaches the inverter — it
/// is a client-side strategy that resolves to <see cref="EnabledPowerControl"/> with a computed
/// <c>active_power</c> target (see <see cref="Strategies.BatteryDischargeHoldStrategy"/> and
/// docs/DECISIONS.md). The device enum itself is just these values.</para>
/// </summary>
public enum InverterPowerControlMode : ushort
{
    /// <summary>Remote control off; the inverter returns to its own work mode (e.g. Self Use).</summary>
    Disabled = 0,

    /// <summary>
    /// The inverter drives its grid-connection point to the commanded <c>active_power</c> target for
    /// the commanded duration. Upstream also lists (but does not use) 2 = Quantity Control and
    /// 3 = SOC Target Control.
    /// </summary>
    EnabledPowerControl = 1,
}
