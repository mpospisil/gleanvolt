using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Api.Tests;

/// <summary>Canned data, so each test says only what it is actually about.</summary>
internal static class Fixtures
{
    /// <summary>The instant every test runs at: a winter afternoon, so "tomorrow morning" is a real departure.</summary>
    internal static readonly DateTimeOffset Now = new(2026, 1, 15, 14, 0, 0, TimeSpan.FromHours(1));

    /// <summary>
    /// The installation the API speaks for (issue #111). Fully described on purpose: /site's job is to
    /// carry every field, and a fixture with half of them null would let a dropped one pass unnoticed.
    /// </summary>
    internal static readonly PvSystemInfo Site = new(
        Id: "home-roof",
        Name: "Home Roof",
        Address: "Krásného 12, Praha",
        Latitude: 50.0755,
        Longitude: 14.4378,
        AzimuthDegrees: 172,
        TiltDegrees: 35,
        CapacityKwp: 9.2,
        InverterCapacityKw: 8,
        LossFactor: 0.9,
        InstallDate: new DateOnly(2024, 5, 1),
        Inverter: new PvDeviceInfo("Inverter", string.Empty, "SolaX X3-HYB-G4 PRO", new DeviceConfig { Host = "192.168.2.10" }),
        Chargers: [new PvDeviceInfo("wallbox", "Garage wallbox", "SolaX X3-HAC", new DeviceConfig { Host = "192.168.2.6", Port = 502, UnitId = 1 })]);

    internal static ChargeControlStatus Status(
        ChargeControlMode mode = ChargeControlMode.Solar,
        bool batteryHoldEnabled = true,
        DateTimeOffset? timestamp = null) => new(
        Mode: mode,
        DryRun: false,
        HoldingControl: true,
        State: ChargeControlState.Charging,
        SurplusWatts: 4200,
        TargetCurrentAmps: 6,
        ActiveCurrentAmps: 6,
        BatterySocPercent: 78,
        ChargerStatus: EvChargerStatus.Charging,
        CarConnected: true,
        SolarPowerWatts: 5300,
        ForecastSolarPowerWatts: 5100,
        EvChargerPowerWatts: 4140,
        EvChargingCurrentAmps: 6,
        BatteryPowerWatts: 500,
        GridPowerWatts: -200,
        BatteryHoldEnabled: batteryHoldEnabled,
        BatteryHoldRequested: false,
        BatteryHoldActive: false,
        BatteryHoldTargetWatts: null,
        Plan: null,
        LoanPowerWatts: 0,
        SessionEnergyWh: 3200,
        LoanedTodayWh: 0,
        TomorrowForecastWh: 18000,
        Timestamp: timestamp ?? Now);

    internal static TargetedChargePlan Plan(TargetedChargeRequest request) => new(
        Strategy: TargetedChargeStrategy.SolarPlusGrid,
        Now: Now,
        DepartBy: request.DepartBy,
        Deadline: request.DepartBy.AddMinutes(-15),
        RequiredEnergyWh: request.RequiredEnergyWh,
        DeliveredEnergyWh: 0,
        RemainingEnergyWh: request.RequiredEnergyWh,
        SolarEnergyWh: 14600,
        ForecastSurplusWh: 16000,
        RequiredPaceWatts: 1800,
        GridEnergyWh: request.Priority == TargetedChargePriority.JustInTime ? 11000 : 7400,
        CeilingEnergyWh: 60000,
        ExpectedEnergyWh: request.RequiredEnergyWh,
        ShortfallWh: 0,
        GridStart: Now.AddHours(6),
        FeasibleDeparture: null,
        SocFloorPercent: 55,
        BatteryToFullWh: 2200,
        Blocks:
        [
            new TargetedChargeBlock(Now.AddHours(2), Now.AddHours(5), TargetedChargeSource.Solar, 4600, 13800),
            new TargetedChargeBlock(Now.AddHours(6), Now.AddHours(8), TargetedChargeSource.Grid, 3700, 7400),
        ],
        ForecastAsOf: Now.AddMinutes(-30),
        IsUsable: true,
        TailEnergyWh: request.TailEnergyWh,
        HoldUntil: request.HoldsTail ? request.DepartBy.AddHours(-3) : null,
        Reason: "Sun covers 14.6 kWh; the grid buys the rest from 20:00.");

    internal static EnergyInterval Interval(DateTimeOffset start, double solarKwh, double evKwh = 0) => new(
        PeriodStart: start,
        PeriodEnd: start.AddMinutes(15),
        TimeZoneId: "Europe/Prague",
        LocalDate: DateOnly.FromDateTime(start.DateTime),
        SolarKwh: solarKwh,
        ForecastSolarKwh: solarKwh * 0.9,
        GridImportKwh: 0.1,
        GridExportKwh: 0.2,
        EvKwh: evKwh,
        BatteryChargeKwh: 0.3,
        BatteryDischargeKwh: 0,
        SocStartPercent: 60,
        SocEndPercent: 62,
        SocMinPercent: 60,
        SocMaxPercent: 62,
        SocMeanPercent: 61,
        Covered: TimeSpan.FromMinutes(15),
        SampleCount: 180);

    internal static ChargingSession Session(Guid id, DateTimeOffset startedAt) => new(
        Id: id,
        StartedAt: startedAt,
        EndedAt: startedAt.AddHours(3),
        TimeZoneId: "Europe/Prague",
        StartMode: ChargeControlMode.Targeted,
        EndMode: ChargeControlMode.Off,
        EndReason: ChargingSessionEndReason.SessionComplete,
        StartBatterySocPercent: 80,
        EndBatterySocPercent: 95,
        EnergyDeliveredWh: 22000,
        FromSolarWh: 14600,
        FromGridWh: 7400,
        FromBatteryWh: 0,
        LoanedWh: 0,
        PeakChargingPowerWatts: 6900,
        StartPlan: null,
        ForecastRemainingAtStartWh: 18000,
        DayForecast: null,
        WeatherAtStart: null,
        WeatherAtEnd: null,
        Sunrise: null,
        Sunset: null,
        Controlled: true);
}

/// <summary>
/// A clock that does not move and a zone that is not the build agent's. Both matter: the local-day
/// endpoints are only correct if they use the site's zone rather than the machine's.
/// </summary>
internal sealed class FixedTimeProvider(DateTimeOffset now, TimeZoneInfo zone) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;

    public override TimeZoneInfo LocalTimeZone => zone;
}
