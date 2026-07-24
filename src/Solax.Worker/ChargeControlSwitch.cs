using Solax.Core.Interfaces;

namespace Solax.Worker;

/// <summary>
/// Thread-safe runtime on/off switch for charge control (see <see cref="IChargeControlSwitch"/>).
/// Registered as a singleton, seeded from <c>ChargeControl:Enabled</c>. The configured value is only
/// the boot default — a runtime change (e.g. from Home Assistant) does not persist across restarts.
/// </summary>
public sealed class ChargeControlSwitch : IChargeControlSwitch
{
    private readonly ILogger<ChargeControlSwitch> _logger;
    private volatile bool _enabled;

    public ChargeControlSwitch(bool initiallyEnabled, ILogger<ChargeControlSwitch> logger)
    {
        _enabled = initiallyEnabled;
        _logger = logger;
    }

    public bool IsEnabled => _enabled;

    public event Action<bool>? Changed;

    public void Set(bool enabled, string source)
    {
        if (_enabled == enabled)
        {
            return;
        }

        _enabled = enabled;
        _logger.LogInformation("Charge control {State} by {Source}.", enabled ? "ENABLED" : "DISABLED", source);
        Changed?.Invoke(enabled);
    }
}
