using Gleanvolt.Providers.Client.Vehicle;
using Gleanvolt.Providers.Contracts.Vehicle;

namespace Gleanvolt.Plugin.VehicleSample;

/// <summary>
/// One pretend car, plugged in and charging: 40 % when the sample starts, one point every
/// <see cref="Step"/>, capped at 80 %, then complete. The range follows the SOC and the pack size from
/// Gleanvolt's welcome. Nothing is random, so two runs started together report the same car.
/// </summary>
public sealed class SimulatedCar(TimeProvider time) : IVehicleSource
{
    /// <summary>How long one point of SOC takes.</summary>
    public static readonly TimeSpan Step = TimeSpan.FromMinutes(3);

    public const double StartSocPercent = 40;
    public const double LimitSocPercent = 80;

    /// <summary>A pack to assume when Gleanvolt does not know the car's.</summary>
    public const double DefaultCapacityKWh = 60;

    /// <summary>What the pretend car uses: 18 kWh per 100 km.</summary>
    public const double KWhPerKm = 0.18;

    private readonly DateTimeOffset _started = time.GetUtcNow();

    /// <summary>How many steps the car has charged since the sample started.</summary>
    public int Steps => (int)((time.GetUtcNow() - _started) / Step);

    /// <summary>When the next step is due.</summary>
    public DateTimeOffset NextStepAt => _started + (Steps + 1) * Step;

    public Task<VehicleReading?> GetReadingAsync(VehicleInfo car, CancellationToken cancellationToken) =>
        Task.FromResult<VehicleReading?>(Reading(car));

    /// <summary>The car now. Its capture time is the last step's, as a real car reports when it last changed.</summary>
    public VehicleReading Reading(VehicleInfo car)
    {
        var steps = Steps;
        var soc = Math.Min(LimitSocPercent, StartSocPercent + steps);
        var complete = soc >= LimitSocPercent;

        // Captured when the SOC last moved: once complete, the car stops reporting changes.
        var capturedSteps = complete ? (int)(LimitSocPercent - StartSocPercent) : steps;
        var capacity = car.BatteryCapacityKWh is > 0 and var known ? known : DefaultCapacityKWh;

        return new VehicleReading(
            CapturedAt: _started + capturedSteps * Step,
            SocPercent: soc,
            RangeMetres: Math.Round(soc / 100 * capacity / KWhPerKm) * 1000,
            ChargeTimeRemaining: (LimitSocPercent - soc) * Step,
            ChargeState: complete ? CarChargeState.Complete : CarChargeState.Charging,
            PlugState: CarPlugState.Connected);
    }
}
