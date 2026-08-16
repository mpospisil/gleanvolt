namespace Gleanvolt.Core.Strategies;

/// <summary>
/// Converts between charging power (watts) and current (amps) for the charger, accounting for the
/// number of phases it charges on. One amp corresponds to <c>nominalVoltage × phases</c> watts, so
/// the universal 6 A EVSE minimum (IEC 61851) is ~1.4 kW on a single-phase charger but ~4.2 kW on a
/// three-phase one. Getting the phase count wrong makes the minimum power floor and the amp setpoint
/// wrong -- e.g. a three-phase charger treated as single-phase would start on a ~1.4 kW surplus while
/// the car draws ~4.2 kW, importing the difference from the grid.
/// </summary>
public sealed class ChargePowerConverter
{
    private readonly double _wattsPerAmp;

    public ChargePowerConverter(double nominalVoltage, int phases)
    {
        if (nominalVoltage <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nominalVoltage), nominalVoltage, "Nominal voltage must be positive.");
        }

        if (phases < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(phases), phases, "Phase count must be at least 1.");
        }

        _wattsPerAmp = nominalVoltage * phases;
    }

    public double AmpsToWatts(double amps) => amps * _wattsPerAmp;

    public double WattsToAmps(double watts) => watts / _wattsPerAmp;
}
