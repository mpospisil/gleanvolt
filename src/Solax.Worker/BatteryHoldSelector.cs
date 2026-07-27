using Solax.Core.Interfaces;

namespace Solax.Worker;

/// <summary>
/// Thread-safe runtime battery-hold state (see <see cref="IBatteryHoldSelector"/>). Registered as a
/// singleton, seeded from configuration. The configured value is only the boot default — a runtime
/// change (e.g. from Home Assistant) does not persist across restarts, the same contract as
/// <see cref="ChargeControlModeSelector"/>.
/// </summary>
public sealed class BatteryHoldSelector : IBatteryHoldSelector
{
    private readonly ILogger<BatteryHoldSelector> _logger;
    private readonly Lock _gate = new();
    private bool _hold;

    public BatteryHoldSelector(bool initialHold, ILogger<BatteryHoldSelector> logger)
    {
        _hold = initialHold;
        _logger = logger;
    }

    public bool Hold
    {
        get { lock (_gate) { return _hold; } }
    }

    public event Action<bool>? Changed;

    public void Set(bool hold, string source)
    {
        lock (_gate)
        {
            if (_hold == hold)
            {
                return;
            }

            _hold = hold;
        }

        _logger.LogInformation("Battery discharge hold turned {State} by {Source}.", hold ? "on" : "off", source);
        Changed?.Invoke(hold);
    }
}
