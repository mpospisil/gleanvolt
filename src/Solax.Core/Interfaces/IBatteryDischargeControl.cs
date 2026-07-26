using Solax.Core.Models;

namespace Solax.Core.Interfaces;

/// <summary>
/// Holds the home battery out of discharge by issuing the inverter's Modbus Power Control command
/// (issue #20). Two states only: held, or released.
///
/// <para>There is no <c>ReadHoldAsync</c> counterpart, because the command is not readable — the
/// holding register the block is written to reports the inverter's firmware version when read, and
/// the inverter exposes no register for the active remote-control state. The reported state is
/// therefore what we last successfully wrote, not a device read-back. See docs/DECISIONS.md.</para>
/// </summary>
public interface IBatteryDischargeControl
{
    /// <summary>
    /// Reconciles the inverter with <paramref name="hold"/>, writing only when something has to
    /// change: on arming, on releasing, when the target has moved by more than the configured
    /// threshold, and when the armed command is close enough to expiring that it needs renewing.
    /// A steady state writes nothing.
    /// </summary>
    /// <param name="hold">Whether the hold should be active.</param>
    /// <param name="activePowerTargetWatts">
    /// The target from <see cref="Strategies.BatteryDischargeHoldStrategy"/>. Ignored when
    /// <paramref name="hold"/> is false.
    /// </param>
    /// <param name="now">
    /// The timestamp of the telemetry this decision is based on; drives command renewal.
    /// </param>
    Task<BatteryHoldState> ApplyAsync(
        bool hold,
        double activePowerTargetWatts,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
