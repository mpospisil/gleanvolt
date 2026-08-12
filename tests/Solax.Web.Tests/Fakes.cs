using Solax.Core.Enums;
using Solax.Core.Models;

namespace Solax.Web.Tests;

/// <summary>
/// A clock that does not move, in a zone that is not the build agent's. Both halves matter: the
/// pages format timestamps in the app's configured zone, and CI must not be able to pass a test that
/// would fail in Prague.
/// </summary>
internal sealed class FixedTimeProvider(DateTimeOffset now, TimeZoneInfo zone) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public override TimeZoneInfo LocalTimeZone { get; } = zone;
}

internal static class Statuses
{
    /// <summary>
    /// A status with every field filled in plausibly, so a test can name only what it is about.
    /// </summary>
    public static ChargeControlStatus Sample(
        DateTimeOffset timestamp,
        ChargeControlMode mode = ChargeControlMode.Off) =>
        new(
            Mode: mode,
            DryRun: false,
            HoldingControl: false,
            State: ChargeControlState.Idle,
            SurplusWatts: null,
            TargetCurrentAmps: null,
            ActiveCurrentAmps: 6,
            BatterySocPercent: 64,
            ChargerStatus: EvChargerStatus.Available,
            CarConnected: false,
            SolarPowerWatts: 1200,
            EvChargerPowerWatts: 0,
            EvChargingCurrentAmps: 0,
            BatteryPowerWatts: 400,
            GridPowerWatts: -150,
            BatteryHoldEnabled: false,
            BatteryHoldRequested: false,
            BatteryHoldActive: false,
            BatteryHoldTargetWatts: null,
            Plan: null,
            LoanPowerWatts: 0,
            SessionEnergyWh: 0,
            LoanedTodayWh: 0,
            TomorrowForecastWh: null,
            Timestamp: timestamp);
}
