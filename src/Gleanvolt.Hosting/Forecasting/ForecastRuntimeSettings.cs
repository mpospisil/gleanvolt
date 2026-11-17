using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Resume;
using static System.FormattableString;

namespace Gleanvolt.Hosting.Forecasting;

/// <summary>
/// Thread-safe runtime forecast settings (see <see cref="IForecastRuntimeSettings"/>). Registered as a
/// singleton and seeded from <see cref="ForecastChargeOptions"/>; the configured values are only the
/// defaults. A change made from the web UI, MQTT or the API is kept in <see cref="ChargeSettingsFile"/>
/// and survives a restart (#421).
/// </summary>
public sealed class ForecastRuntimeSettings : IForecastRuntimeSettings
{
    private readonly ILogger<ForecastRuntimeSettings> _logger;
    private readonly ChargeSettingsFile? _file;
    private readonly Lock _gate = new();

    private double _minBatterySocFloorPercent;
    private double _floorResumeMarginPercent;

    /// <param name="options">The configured values, used for every value never changed.</param>
    /// <param name="logger">Where changes are recorded.</param>
    /// <param name="file">Where changes are kept across restarts; null keeps them in memory only.</param>
    public ForecastRuntimeSettings(
        IOptions<ForecastChargeOptions> options,
        ILogger<ForecastRuntimeSettings> logger,
        ChargeSettingsFile? file = null)
    {
        var value = options.Value;
        var saved = file?.Saved ?? new SavedChargeSettings();
        _minBatterySocFloorPercent = Math.Clamp(saved.MinBatterySocFloorPercent ?? value.MinBatterySocFloorPercent, 0, 100);
        _floorResumeMarginPercent = Math.Clamp(saved.FloorResumeMarginPercent ?? value.FloorResumeMarginPercent, 0, 100);
        _logger = logger;
        _file = file;

        if (saved.MinBatterySocFloorPercent is not null)
        {
            _logger.LogInformation(
                "Minimum battery SOC {Value:F0}% as last set (configured {Configured:F0}%).",
                _minBatterySocFloorPercent,
                value.MinBatterySocFloorPercent);
        }

        if (saved.FloorResumeMarginPercent is not null)
        {
            _logger.LogInformation(
                "SOC floor resume margin {Value:F0}% as last set (configured {Configured:F0}%).",
                _floorResumeMarginPercent,
                value.FloorResumeMarginPercent);
        }
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
        Set(
            ref _minBatterySocFloorPercent,
            Math.Clamp(percent, 0, 100),
            "Minimum battery SOC",
            Invariant($"{percent:F0}%"),
            source,
            (saved, value) => saved with { MinBatterySocFloorPercent = value });

    public void SetFloorResumeMarginPercent(double percent, string source) =>
        Set(
            ref _floorResumeMarginPercent,
            Math.Clamp(percent, 0, 100),
            "SOC floor resume margin",
            Invariant($"{percent:F0}%"),
            source,
            (saved, value) => saved with { FloorResumeMarginPercent = value });

    private void Set(
        ref double field,
        double value,
        string name,
        string display,
        string source,
        Func<SavedChargeSettings, double, SavedChargeSettings> keep)
    {
        lock (_gate)
        {
            if (Math.Abs(field - value) < 0.001)
            {
                return;
            }

            field = value;
            _file?.Update(saved => keep(saved, value));
        }

        _logger.LogInformation("{Setting} set to {Value} by {Source}.", name, display, source);
    }
}
