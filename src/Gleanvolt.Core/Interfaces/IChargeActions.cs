using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// Starting and stopping controlled charging. Every kind of controlled charging begins with one of
/// these calls and with nothing else: the action puts the charger into
/// <see cref="EvChargerMode.Fast"/> and then selects the strategy, so a press does something on a
/// charger sitting in Green rather than waiting for the owner to have set the wallbox by hand.
///
/// <para>The seam lives in Core because both surfaces drive it — the web UI, which sees only Core,
/// and the Home Assistant worker — exactly like <see cref="IChargeControlModeSelector"/>, which this
/// does not replace: the mode is still the state everything downstream reads, and this is only the
/// one way in.</para>
/// </summary>
public interface IChargeActions
{
    /// <summary>
    /// Starts <paramref name="mode"/>: writes <see cref="EvChargerMode.Fast"/> to the charger, then
    /// selects the mode, so the next poll reads Fast back and starts modulating the current. A failed
    /// write leaves the mode exactly as it was and reports why.
    ///
    /// <para><see cref="ChargeControlMode.Off"/> is a caller error — use <see cref="StopAsync"/>. So
    /// is <see cref="ChargeControlMode.Targeted"/> with no request set, which reports "no target set"
    /// every poll as it always has.</para>
    /// </summary>
    /// <param name="source">Who pressed it, for the log (e.g. "Web UI", "Home Assistant").</param>
    Task<ChargeActionResult> StartAsync(ChargeControlMode mode, string source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops charging: writes <see cref="EvChargerMode.Stop"/> to the charger and returns the mode to
    /// <see cref="ChargeControlMode.Off"/>, which releases any hold a mode had armed and closes the
    /// session. Always writes, even when the controller was already Off and never took control — the
    /// button says stop charging, so it stops charging.
    ///
    /// <para>The current setpoint is deliberately left where the last control cycle put it: Stop is
    /// what stops the car, and a second write to say the same thing buys nothing.</para>
    /// </summary>
    Task<ChargeActionResult> StopAsync(string source, CancellationToken cancellationToken = default);
}
