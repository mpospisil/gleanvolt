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

    public ForecastRuntimeSettings(IOptions<ForecastChargeOptions> options, ILogger<ForecastRuntimeSettings> logger)
    {
        var value = options.Value;
        _dailyEvTargetWh = Math.Max(0, value.DailyEvTargetKWh * 1000);
        _sessionEnergyTargetWh = Math.Max(0, value.SessionEnergyTargetKWh * 1000);
        _minBatterySocFloorPercent = Math.Clamp(value.MinBatterySocFloorPercent, 0, 100);
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

    public void SetDailyEvTargetWh(double wattHours, string source) =>
        Set(ref _dailyEvTargetWh, Math.Max(0, wattHours), "Daily EV target", $"{wattHours / 1000:F1}kWh", source);

    public void SetSessionEnergyTargetWh(double wattHours, string source) =>
        Set(ref _sessionEnergyTargetWh, Math.Max(0, wattHours), "Session energy target", $"{wattHours / 1000:F1}kWh", source);

    public void SetMinBatterySocFloorPercent(double percent, string source) =>
        Set(ref _minBatterySocFloorPercent, Math.Clamp(percent, 0, 100), "Minimum battery SOC", $"{percent:F0}%", source);

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
