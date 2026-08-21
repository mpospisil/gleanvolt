using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

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
    private readonly ILogger<ChargeActions> _logger;

    public ChargeActions(
        IEvChargerControl charger,
        IChargeControlModeSelector mode,
        ILogger<ChargeActions> logger)
    {
        _charger = charger;
        _mode = mode;
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
