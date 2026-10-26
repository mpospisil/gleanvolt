using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Hosting.Configuration;

namespace Gleanvolt.Hosting.Forecasting;

/// <summary>
/// Thread-safe runtime <b>until full</b> flag (see <see cref="IUntilFullSettings"/>). Registered as a
/// singleton and seeded from <see cref="ForecastChargeOptions.UntilFull"/>. What survives a restart is
/// not this flag but a session running with it (<see cref="UntilFullSessionFile"/>), which turns it back
/// on as it restores the mode.
/// </summary>
public sealed class UntilFullSettings : IUntilFullSettings
{
    private readonly ILogger<UntilFullSettings> _logger;
    private readonly Lock _gate = new();
    private bool _on;

    public UntilFullSettings(IOptions<ForecastChargeOptions> options, ILogger<UntilFullSettings> logger)
    {
        _logger = logger;
        _on = options.Value.UntilFull;
    }

    public bool UntilFull
    {
        get { lock (_gate) { return _on; } }
    }

    public void SetUntilFull(bool on, string source)
    {
        lock (_gate)
        {
            if (_on == on)
            {
                return;
            }

            _on = on;
        }

        _logger.LogInformation("Forecasted until full turned {State} by {Source}.", on ? "on" : "off", source);
    }
}
