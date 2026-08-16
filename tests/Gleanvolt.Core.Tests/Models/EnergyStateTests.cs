using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Tests.Models;

public class EnergyStateTests
{
    [Fact]
    public void FromRawRegisters_MapsUnsignedBatterySoc()
    {
        var timestamp = DateTimeOffset.UtcNow;

        var state = EnergyState.FromRawRegisters(
            timestamp,
            batterySocRaw: 42,
            batteryPowerRaw: 0,
            pvPowerDc1Raw: 0,
            pvPowerDc2Raw: 0,
            feedinPowerLowRaw: 0,
            feedinPowerHighRaw: 0,
            evChargerStatusRaw: 0,
            evChargerPowerRaw: 0);

        Assert.Equal(42, state.BatterySocPercent);
        Assert.Equal(timestamp, state.Timestamp);
    }

    [Theory]
    [InlineData((ushort)1500, 1500)] // charging
    [InlineData(unchecked((ushort)(short)-1500), -1500)] // discharging
    public void FromRawRegisters_MapsSignedBatteryPower_PositiveIsCharging(ushort raw, double expectedWatts)
    {
        var state = EnergyState.FromRawRegisters(
            DateTimeOffset.UtcNow,
            batterySocRaw: 0,
            batteryPowerRaw: raw,
            pvPowerDc1Raw: 0,
            pvPowerDc2Raw: 0,
            feedinPowerLowRaw: 0,
            feedinPowerHighRaw: 0,
            evChargerStatusRaw: 0,
            evChargerPowerRaw: 0);

        Assert.Equal(expectedWatts, state.BatteryPowerWatts);
    }

    [Theory]
    // FeedinPower is the grid METER (int32, low word first): positive = export. This model negates
    // it so positive = import.
    [InlineData((ushort)1500, (ushort)0, -1500)]                        // exporting 1500W
    [InlineData(unchecked((ushort)(short)-1500), (ushort)0xFFFF, 1500)] // importing 1500W
    public void FromRawRegisters_DecodesFeedinPowerMeter_PositiveIsImporting(ushort low, ushort high, double expectedWatts)
    {
        var state = EnergyState.FromRawRegisters(
            DateTimeOffset.UtcNow,
            batterySocRaw: 0,
            batteryPowerRaw: 0,
            pvPowerDc1Raw: 0,
            pvPowerDc2Raw: 0,
            feedinPowerLowRaw: low,
            feedinPowerHighRaw: high,
            evChargerStatusRaw: 0,
            evChargerPowerRaw: 0);

        Assert.Equal(expectedWatts, state.GridPowerWatts);
    }

    [Fact]
    public void FromRawRegisters_MapsUnsignedEvChargerPower()
    {
        var state = EnergyState.FromRawRegisters(
            DateTimeOffset.UtcNow,
            batterySocRaw: 0,
            batteryPowerRaw: 0,
            pvPowerDc1Raw: 0,
            pvPowerDc2Raw: 0,
            feedinPowerLowRaw: 0,
            feedinPowerHighRaw: 0,
            evChargerStatusRaw: 0,
            evChargerPowerRaw: 7000);

        Assert.Equal(7000, state.EvChargerPowerWatts);
    }

    [Fact]
    public void FromRawRegisters_SumsSolarPowerAcrossBothMpptTrackers()
    {
        var state = EnergyState.FromRawRegisters(
            DateTimeOffset.UtcNow,
            batterySocRaw: 0,
            batteryPowerRaw: 0,
            pvPowerDc1Raw: 300,
            pvPowerDc2Raw: 450,
            feedinPowerLowRaw: 0,
            feedinPowerHighRaw: 0,
            evChargerStatusRaw: 0,
            evChargerPowerRaw: 0);

        Assert.Equal(750, state.SolarPowerWatts);
    }

    // Grid uses the import-positive convention (negative = exporting).
    private static EnergyState StateWith(double solar, double grid, double battery, double ev) =>
        new(
            DateTimeOffset.UtcNow,
            BatterySocPercent: 98,
            BatteryPowerWatts: battery,
            SolarPowerWatts: solar,
            GridPowerWatts: grid,
            EvChargerStatus: EvChargerStatus.Charging,
            EvChargerPowerWatts: ev);

    [Fact]
    public void SolarSurplus_IsSolarMinusHouseholdConsumption()
    {
        // 9000W sun, car taking 5000W, 3500W exported, battery idle.
        // Household consumption = 9000 - 3500 - 5000 = 500W, so the car may have 9000 - 500 = 8500W
        // (what it already draws plus what is being exported).
        var state = StateWith(solar: 9000, grid: -3500, battery: 0, ev: 5000);

        Assert.Equal(500, state.OtherLoadsPowerWatts);
        Assert.Equal(8500, state.SolarSurplusPowerWatts);
    }

    [Fact]
    public void SolarSurplus_WhenImporting_IsOnlyWhatTheSunActuallyCovers()
    {
        // The real failure case: 2422W of sun, car pulling 10785W, battery covering 5251W and the
        // rest imported. Household load is 500W, so only 1922W is genuinely solar -- far below the
        // 4140W three-phase 6A floor, so charging must pause.
        var state = StateWith(solar: 2422, grid: 3612, battery: -5251, ev: 10785);

        Assert.Equal(500, state.OtherLoadsPowerWatts);
        Assert.Equal(1922, state.SolarSurplusPowerWatts);
        Assert.True(state.SolarSurplusPowerWatts < state.SolarPowerWatts);
    }

    [Fact]
    public void SolarSurplus_ExcludesBatteryCharging_SoTheCarCanOutbidIt()
    {
        // Household consumption deliberately excludes battery charging, so the 1500W the battery is
        // taking still counts as available to the car.
        var state = StateWith(solar: 6000, grid: -4000, battery: 1500, ev: 0);

        Assert.Equal(500, state.OtherLoadsPowerWatts);
        Assert.Equal(5500, state.SolarSurplusPowerWatts);
    }

    [Fact]
    public void OtherLoadsPowerWatts_IsHouseholdBaseLoad_ExcludingPvEvAndBattery()
    {
        // Solar 7267W, exporting 7016W to grid (import convention -> Grid = -7016), battery idle,
        // no EV: household base load = 7267 - 7016 = 251W (the residual seen in real telemetry).
        var state = new EnergyState(
            DateTimeOffset.UtcNow,
            BatterySocPercent: 100,
            BatteryPowerWatts: 0,
            SolarPowerWatts: 7267,
            GridPowerWatts: -7016,
            EvChargerStatus: EvChargerStatus.Available,
            EvChargerPowerWatts: 0);

        Assert.Equal(251, state.OtherLoadsPowerWatts);
    }

    [Fact]
    public void FromRawRegisters_MapsEvChargerStatus()
    {
        var state = EnergyState.FromRawRegisters(
            DateTimeOffset.UtcNow,
            batterySocRaw: 0,
            batteryPowerRaw: 0,
            pvPowerDc1Raw: 0,
            pvPowerDc2Raw: 0,
            feedinPowerLowRaw: 0,
            feedinPowerHighRaw: 0,
            evChargerStatusRaw: 2,
            evChargerPowerRaw: 0);

        Assert.Equal(EvChargerStatus.Charging, state.EvChargerStatus);
    }
}
