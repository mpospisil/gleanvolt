using Gleanvolt.Core.Interfaces;
using Gleanvolt.Devices;

namespace Gleanvolt.Infrastructure;

/// <summary>
/// <see cref="IEvChargerControl"/> over the charger's driver (issue #348): the control loop's three calls,
/// passed through. The family's encodings, the setpoint's hysteresis and dry-run all live in the driver.
/// </summary>
public sealed class EvChargerControl : IEvChargerControl
{
    private readonly IEvChargerDriver _driver;

    public EvChargerControl(IEvChargerDriver driver)
    {
        _driver = driver;
    }

    public Task<EvChargerSettings> ReadSettingsAsync(CancellationToken cancellationToken = default) =>
        _driver.ReadSettingsAsync(cancellationToken);

    public Task SetCurrentAsync(int activeAmps, int targetAmps, string reason, CancellationToken cancellationToken = default) =>
        _driver.SetCurrentAsync(activeAmps, targetAmps, reason, cancellationToken);

    public Task SetChargingAllowedAsync(bool allowed, string reason, CancellationToken cancellationToken = default) =>
        _driver.SetChargingAllowedAsync(allowed, reason, cancellationToken);
}
