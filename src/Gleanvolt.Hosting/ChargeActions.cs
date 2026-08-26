using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Hosting;

/// <summary>
/// <see cref="IChargeActions"/> against the real charger and the real mode selector. Registered as a
/// singleton; both surfaces (the web UI and the MQTT worker) call the same instance, so they cannot
/// disagree about what was asked for.
///
/// <para><b>On concurrency.</b> This is the first write that does not come from the poll loop, so a
/// press can land mid-cycle. Nothing here needs a lock of its own: <c>ModbusTcpClient</c> serialises
/// every transaction behind a semaphore, so the use-mode write queues behind whatever exchange the
/// poll is in the middle of rather than interleaving with it.</para>
/// </summary>
public sealed class ChargeActions : IChargeActions
{
    private readonly IEvChargerControl _charger;
    private readonly IChargeControlModeSelector _mode;
    private readonly FastChargingController _fast;
    private readonly ILogger<ChargeActions> _logger;

    /// <param name="fast">
    /// Read for one number: the current the fast mode runs at. Taken from the controller itself rather
    /// than re-derived from configuration, so the value written here and the value commanded on every
    /// subsequent cycle cannot be two different figures.
    /// </param>
    public ChargeActions(
        IEvChargerControl charger,
        IChargeControlModeSelector mode,
        FastChargingController fast,
        ILogger<ChargeActions> logger)
    {
        _charger = charger;
        _mode = mode;
        _fast = fast;
        _logger = logger;
    }

    public async Task<ChargeActionResult> StartAsync(
        ChargeControlMode mode, string source, CancellationToken cancellationToken = default)
    {
        if (mode == ChargeControlMode.Off)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mode), mode, $"Off is not a strategy to start; call {nameof(StopAsync)} instead.");
        }

        // The charger first: a mode selected over a charger that refused Fast would sit Idle and say
        // nothing more than "Idle", which is the failure this whole shape exists to remove.
        var written = await WriteUseModeAsync(EvChargerMode.Fast, $"{mode} started by {source}.", cancellationToken)
            .ConfigureAwait(false);

        if (written is not null)
        {
            return ChargeActionResult.Failed(
                $"The charger did not accept Fast, so {mode} was not started — it is still in its previous mode. {written}");
        }

        // Only the fast mode, and only because its current is knowable before the first cycle: it is a
        // constant. Every other mode computes a setpoint from surplus or from a plan, and writing the
        // maximum ahead of them would charge flat out for one poll interval, which is the opposite of
        // what they were started to do.
        if (mode == ChargeControlMode.FastNoBattery)
        {
            await SetMaxCurrentAsync(source, cancellationToken).ConfigureAwait(false);
        }

        _mode.Set(mode, source);
        return ChargeActionResult.Success;
    }

    public async Task<ChargeActionResult> StopAsync(string source, CancellationToken cancellationToken = default)
    {
        var written = await WriteUseModeAsync(EvChargerMode.Stop, $"Charging stopped by {source}.", cancellationToken)
            .ConfigureAwait(false);

        // Off is set either way, unlike a failed start. A controller left driving a charger it cannot
        // reach would go on commanding currents at a wallbox whose owner has just said stop; releasing
        // control is the one half of the button that never depends on the hardware answering.
        _mode.Set(ChargeControlMode.Off, source);

        return written is null
            ? ChargeActionResult.Success
            : ChargeActionResult.Failed($"Charge control was switched off, but the charger did not accept Stop. {written}");
    }

    /// <summary>
    /// Puts the charger at the fast mode's current straight away, instead of leaving the first control
    /// cycle to do it a poll interval later.
    ///
    /// <para>The gap this closes is real and is easiest to see after a completed charge: the mode ends
    /// by writing <c>PauseCurrentAmps</c> — 0 A on the reference install — so the next fast charge
    /// starts with the charger sitting at the pause current. Until this, it stayed there until a poll
    /// completed, which is a window in which the charger is in Fast and the car is being told to take
    /// nothing.</para>
    ///
    /// <para><b>Not a failure when it doesn't write.</b> <see cref="IEvChargerControl.SetCurrentAsync"/>
    /// skips a write that would not move the setpoint, so a charger already at the maximum is left
    /// alone — which is the ordinary case when one fast charge follows another.</para>
    ///
    /// <para><b>And not a failure when the write fails.</b> The use-mode is already written and the
    /// mode is about to be selected; the control loop commands this same current on its next cycle
    /// anyway. Refusing the whole start over a setpoint that will be retried in seconds would turn a
    /// recoverable hiccup into a charge that never began.</para>
    /// </summary>
    private async Task SetMaxCurrentAsync(string source, CancellationToken cancellationToken)
    {
        try
        {
            // Read rather than assumed: SetCurrentAsync needs to know what is on the device to decide
            // whether the write is worth making at all.
            var settings = await _charger.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);

            await _charger
                .SetCurrentAsync(
                    settings.ChargeCurrentAmps,
                    _fast.ChargeCurrentAmps,
                    $"FastNoBattery started by {source}: charge at the maximum.",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Could not set the charge current to {Amps}A when starting FastNoBattery; the next control "
                + "cycle will command it.",
                _fast.ChargeCurrentAmps);
        }
    }

    /// <summary>Writes the use-mode, returning null on success or the failure in words.</summary>
    private async Task<string?> WriteUseModeAsync(EvChargerMode mode, string reason, CancellationToken cancellationToken)
    {
        try
        {
            await _charger.SetModeAsync(mode, reason, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to write the charger's use-mode ({Mode}). {Reason}", mode, reason);
            return ex.Message;
        }
    }
}
