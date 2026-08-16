using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;

namespace Gleanvolt.Hosting;

/// <summary>
/// Thread-safe runtime charge-control mode (see <see cref="IChargeControlModeSelector"/>). Registered
/// as a singleton and always started in <see cref="ChargeControlMode.Off"/>: the service takes no
/// control of the charger until somebody asks, so a restart after a crash, a power cut or a deploy
/// leaves the charger exactly as its owner set it. A runtime change does not persist across restarts.
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
