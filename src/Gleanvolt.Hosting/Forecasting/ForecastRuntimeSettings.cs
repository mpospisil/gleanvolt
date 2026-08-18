using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Hosting.Configuration;

namespace Gleanvolt.Hosting.Forecasting;

/// <summary>
/// Thread-safe runtime forecast settings (see <see cref="IForecastRuntimeSettings"/>). Registered as a
/// singleton and seeded from <see cref="ForecastChargeOptions"/>; the configured values are only the
/// boot defaults, and a change made from Home Assistant does not survive a restart — the same contract
/// as the charge-control mode and the battery hold.
/// </summary>
public sealed class ForecastRuntimeSettings : IForecastRuntimeSettings
{
    private readonly ILogger<ForecastRuntimeSettings> _logger;
    private readonly Lock _gate = new();

    private double _dailyEvTargetWh;
    private double _sessionEnergyTargetWh;
    private double _minBatterySocFloorPercent;
    private double _floorResumeMarginPercent;

    /// <summary>
    /// The lower bound on the resume margin, whatever configuration or Home Assistant asks for: the
    /// battery hold auto-arms at the floor and releases a margin above it, so a car allowed back
    /// sooner than that would return while the pack is still held — SOC pinned to the floor with the
    /// grid covering every dip, instead of the battery recovering. See <c>PollingService.AutoHold</c>.
    /// </summary>
    private readonly double _minResumeMarginPercent;

    public ForecastRuntimeSettings(IOptions<ForecastChargeOptions> options, ILogger<ForecastRuntimeSettings> logger)
    {
        var value = options.Value;
        _dailyEvTargetWh = Math.Max(0, value.DailyEvTargetKWh * 1000);
        _sessionEnergyTargetWh = Math.Max(0, value.SessionEnergyTargetKWh * 1000);
        _minBatterySocFloorPercent = Math.Clamp(value.MinBatterySocFloorPercent, 0, 100);
        _minResumeMarginPercent = value.AutoArmBatteryHoldAtFloor ? Math.Clamp(value.HoldReleaseMarginPercent, 0, 100) : 0;
        _floorResumeMarginPercent = ClampMargin(value.FloorResumeMarginPercent);
        _logger = logger;
    }

    public double DailyEvTargetWh
    {
        get { lock (_gate) { return _dailyEvTargetWh; } }
    }

    public double SessionEnergyTargetWh
    {
        get { lock (_gate) { return _sessionEnergyTargetWh; } }
    }

    public double MinBatterySocFloorPercent
    {
        get { lock (_gate) { return _minBatterySocFloorPercent; } }
    }

    public double FloorResumeMarginPercent
    {
        get { lock (_gate) { return _floorResumeMarginPercent; } }
    }

    public void SetDailyEvTargetWh(double wattHours, string source) =>
        Set(ref _dailyEvTargetWh, Math.Max(0, wattHours), "Daily EV target", $"{wattHours / 1000:F1}kWh", source);

    public void SetSessionEnergyTargetWh(double wattHours, string source) =>
        Set(ref _sessionEnergyTargetWh, Math.Max(0, wattHours), "Session energy target", $"{wattHours / 1000:F1}kWh", source);

    public void SetMinBatterySocFloorPercent(double percent, string source) =>
        Set(ref _minBatterySocFloorPercent, Math.Clamp(percent, 0, 100), "Minimum battery SOC", $"{percent:F0}%", source);

    public void SetFloorResumeMarginPercent(double percent, string source) =>
        Set(ref _floorResumeMarginPercent, ClampMargin(percent), "SOC floor resume margin", $"{percent:F0}%", source);

    private double ClampMargin(double percent) => Math.Clamp(percent, _minResumeMarginPercent, 100);

    private void Set(ref double field, double value, string name, string display, string source)
    {
        lock (_gate)
        {
            if (Math.Abs(field - value) < 0.001)
            {
                return;
            }

            field = value;
        }

        _logger.LogInformation("{Setting} set to {Value} by {Source}.", name, display, source);
    }
}
