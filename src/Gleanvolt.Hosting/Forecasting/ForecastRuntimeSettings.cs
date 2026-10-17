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

    private double _minBatterySocFloorPercent;
    private double _floorResumeMarginPercent;

    public ForecastRuntimeSettings(IOptions<ForecastChargeOptions> options, ILogger<ForecastRuntimeSettings> logger)
    {
        var value = options.Value;
        _minBatterySocFloorPercent = Math.Clamp(value.MinBatterySocFloorPercent, 0, 100);
        _floorResumeMarginPercent = Math.Clamp(value.FloorResumeMarginPercent, 0, 100);
        _logger = logger;
    }

    public double MinBatterySocFloorPercent
    {
        get { lock (_gate) { return _minBatterySocFloorPercent; } }
    }

    public double FloorResumeMarginPercent
    {
        get { lock (_gate) { return _floorResumeMarginPercent; } }
    }

    public void SetMinBatterySocFloorPercent(double percent, string source) =>
        Set(ref _minBatterySocFloorPercent, Math.Clamp(percent, 0, 100), "Minimum battery SOC", $"{percent:F0}%", source);

    public void SetFloorResumeMarginPercent(double percent, string source) =>
        Set(ref _floorResumeMarginPercent, Math.Clamp(percent, 0, 100), "SOC floor resume margin", $"{percent:F0}%", source);

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
