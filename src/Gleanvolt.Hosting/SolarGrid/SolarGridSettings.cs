using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Hosting.Configuration;

namespace Gleanvolt.Hosting.SolarGrid;

/// <summary>
/// Thread-safe runtime solar-grid settings (see <see cref="ISolarGridSettings"/>). Registered as a
/// singleton and seeded from <see cref="SolarGridChargeOptions"/>; the configured value is only the boot
/// default, and a change made from the web UI or Home Assistant does not survive a restart — the same
/// contract as the forecast settings and the mode itself.
/// </summary>
public sealed class SolarGridSettings : ISolarGridSettings
{
    private readonly ILogger<SolarGridSettings> _logger;
    private readonly Lock _gate = new();
    private double _minSurplusWatts;

    public SolarGridSettings(IOptions<SolarGridChargeOptions> options, ILogger<SolarGridSettings> logger)
    {
        _minSurplusWatts = Math.Max(0, options.Value.MinSurplusWatts);
        _logger = logger;
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
        }

        _logger.LogInformation("Solar-grid minimum surplus set to {Watts:F0}W by {Source}.", value, source);
    }
}
