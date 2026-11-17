using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Resume;

namespace Gleanvolt.Hosting.SolarGrid;

/// <summary>
/// Thread-safe runtime solar-grid settings (see <see cref="ISolarGridSettings"/>). Registered as a
/// singleton and seeded from <see cref="SolarGridChargeOptions"/>; the configured value is only the
/// default. A change made from the web UI, MQTT or the API is kept in <see cref="ChargeSettingsFile"/> and
/// survives a restart (#421), like the forecast settings and the mode itself.
/// </summary>
public sealed class SolarGridSettings : ISolarGridSettings
{
    private readonly ILogger<SolarGridSettings> _logger;
    private readonly ChargeSettingsFile? _file;
    private readonly Lock _gate = new();
    private double _minSurplusWatts;

    /// <param name="options">The configured value, used until somebody changes it.</param>
    /// <param name="logger">Where changes are recorded.</param>
    /// <param name="file">Where a change is kept across restarts; null keeps it in memory only.</param>
    public SolarGridSettings(
        IOptions<SolarGridChargeOptions> options,
        ILogger<SolarGridSettings> logger,
        ChargeSettingsFile? file = null)
    {
        var saved = file?.Saved.MinSolarSurplusWatts;
        _minSurplusWatts = Math.Max(0, saved ?? options.Value.MinSurplusWatts);
        _logger = logger;
        _file = file;

        if (saved is not null)
        {
            _logger.LogInformation(
                "Solar-grid minimum surplus {Watts:F0}W as last set (configured {Configured:F0}W).",
                _minSurplusWatts,
                options.Value.MinSurplusWatts);
        }
    }

    public double MinSurplusWatts
    {
        get { lock (_gate) { return _minSurplusWatts; } }
    }

    public void SetMinSurplusWatts(double watts, string source)
    {
        var value = Math.Max(0, watts);

        lock (_gate)
        {
            if (Math.Abs(_minSurplusWatts - value) < 0.5)
            {
                return;
            }

            _minSurplusWatts = value;
            _file?.Update(saved => saved with { MinSolarSurplusWatts = value });
        }

        _logger.LogInformation("Solar-grid minimum surplus set to {Watts:F0}W by {Source}.", value, source);
    }
}
