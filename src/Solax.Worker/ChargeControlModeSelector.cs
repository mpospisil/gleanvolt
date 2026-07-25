using Solax.Core.Enums;
using Solax.Core.Interfaces;

namespace Solax.Worker;

/// <summary>
/// Thread-safe runtime charge-control mode (see <see cref="IChargeControlModeSelector"/>). Registered
/// as a singleton, seeded from configuration. The configured value is only the boot default — a
/// runtime change (e.g. from Home Assistant) does not persist across restarts.
/// </summary>
public sealed class ChargeControlModeSelector : IChargeControlModeSelector
{
    private readonly ILogger<ChargeControlModeSelector> _logger;
    private readonly Lock _gate = new();
    private ChargeControlMode _mode;

    public ChargeControlModeSelector(ChargeControlMode initialMode, ILogger<ChargeControlModeSelector> logger)
    {
        _mode = initialMode;
        _logger = logger;
    }

    public ChargeControlMode Mode
    {
        get { lock (_gate) { return _mode; } }
    }

    public event Action<ChargeControlMode>? Changed;

    public void Set(ChargeControlMode mode, string source)
    {
        lock (_gate)
        {
            if (_mode == mode)
            {
                return;
            }

            _mode = mode;
        }

        _logger.LogInformation("Charge control mode set to {Mode} by {Source}.", mode, source);
        Changed?.Invoke(mode);
    }
}
