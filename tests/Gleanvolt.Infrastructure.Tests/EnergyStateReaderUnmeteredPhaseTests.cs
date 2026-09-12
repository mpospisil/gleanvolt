using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Infrastructure.RegisterMaps;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// The reader's half of the blind-meter correction: it applies only when the site names a phase, reads the
/// inverter's per-phase output from the block it already fetched, and keeps the meter's own figure.
/// </summary>
public class EnergyStateReaderUnmeteredPhaseTests
{
    private static PvSystemInfo Site(GridPhase? unmetered) => new(
        Id: "", Name: "", Address: "", Latitude: null, Longitude: null, AzimuthDegrees: null, TiltDegrees: null,
        CapacityKwp: null, InverterCapacityKw: null, LossFactor: null, InstallDate: null,
        Inverter: new PvDeviceInfo("Inverter", "", "", new DeviceConfig { Host = "inverter" }),
        Chargers: [])
    {
        UnmeteredGridPhase = unmetered,
    };

    /// <summary>The reference install at 2026-09-12 13:32: car paused, battery full, L3 unmetered.</summary>
    private static FakeModbusClient Inverter()
    {
        var inverter = new FakeModbusClient();
        inverter.SetHolding(InverterRegisterMap.BatteryCapacity.Address, 99);
        inverter.SetHolding(InverterRegisterMap.Powerdc1.Address, 1702);
        inverter.SetHolding(InverterRegisterMap.Powerdc2.Address, 1534);
        inverter.SetHolding(InverterRegisterMap.FeedinPowerLow.Address, 1031); // SolaX: positive = export
        inverter.SetHolding(InverterRegisterMap.GridPowerR.Address, 459);
        inverter.SetHolding(InverterRegisterMap.GridPowerS.Address, 888);
        inverter.SetHolding(InverterRegisterMap.GridPowerT.Address, 1735);
        return inverter;
    }

    private static Task<EnergyState> Read(GridPhase? unmetered) =>
        new EnergyStateReader(
            Inverter(),
            new FakeModbusClient(),
            NullLogger<EnergyStateReader>.Instance,
            Site(unmetered),
            new ChargingLimits(6, 16, 3)).ReadAsync();

    [Fact]
    public async Task A_meter_that_sees_every_phase_is_reported_as_it_reads()
    {
        var state = await Read(unmetered: null);

        Assert.Equal(-1031, state.GridPowerWatts);
        Assert.Null(state.MeteredGridPowerWatts);
    }

    [Fact]
    public async Task A_blind_phase_is_estimated_and_the_meter_kept_beside_it()
    {
        var state = await Read(GridPhase.L3);

        Assert.Equal(-2608, state.GridPowerWatts);
        Assert.Equal(-1031, state.MeteredGridPowerWatts);
        Assert.Equal(628, state.OtherLoadsPowerWatts);
    }
}
