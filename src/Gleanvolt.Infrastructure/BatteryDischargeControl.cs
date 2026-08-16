using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Infrastructure.RegisterMaps;

namespace Gleanvolt.Infrastructure;

/// <summary>
/// <see cref="IBatteryDischargeControl"/> over Modbus, on the keyed <b>inverter</b> client. This is
/// the first write this project ever makes to the inverter — everything before it only wrote the
/// charger's current setpoint — so it keeps the same discipline as <see cref="EvChargerControl"/>:
/// write only when something has to change, log the encoded register values, and skip the write
/// entirely in dry-run.
///
/// <para>The command it writes is not a stored setting: nothing reaches EEPROM, and it lapses on its
/// own after the commanded duration. That is the failsafe — if this service stops, no renewal is
/// issued and the inverter returns to its own work mode within one duration, with no shutdown hook
/// or cleanup path involved.</para>
/// </summary>
public sealed class BatteryDischargeControl : IBatteryDischargeControl
{
    private const string RenewalReason = "Renewing before the command lapses.";

    private readonly IModbusClient _client;
    private readonly ILogger<BatteryDischargeControl> _logger;
    private readonly bool _dryRun;
    private readonly int _durationSeconds;
    private readonly TimeSpan _renewAfter;
    private readonly double _targetChangeThresholdWatts;

    // What we believe is armed on the inverter: null when released. There is no read-back to
    // reconcile against (the register isn't readable), so this is our own record of the last write.
    private double? _armedTargetWatts;
    private DateTimeOffset? _armedAt;

    public BatteryDischargeControl(
        [FromKeyedServices(ModbusClientKeys.Inverter)] IModbusClient client,
        ILogger<BatteryDischargeControl> logger,
        bool dryRun = false,
        TimeSpan? duration = null,
        double targetChangeThresholdWatts = 100)
    {
        _client = client;
        _logger = logger;
        _dryRun = dryRun;

        var durationSeconds = (int)Math.Round((duration ?? TimeSpan.FromSeconds(60)).TotalSeconds);
        _durationSeconds = Math.Clamp(durationSeconds, 1, PowerControlPayload.MaxDurationSeconds);

        // Renew at half the commanded duration, so a missed or slow poll still lands well before the
        // command lapses and the battery is never briefly unprotected.
        _renewAfter = TimeSpan.FromSeconds(_durationSeconds / 2.0);
        _targetChangeThresholdWatts = Math.Max(0, targetChangeThresholdWatts);
    }

    public async Task<BatteryHoldState> ApplyAsync(
        bool hold,
        double activePowerTargetWatts,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!hold)
        {
            return await ReleaseAsync(cancellationToken).ConfigureAwait(false);
        }

        var targetWatts = Math.Round(activePowerTargetWatts);
        var reason = ArmReason(targetWatts, now);
        if (reason is null)
        {
            // Steady state: the armed command still matches the target and isn't near expiry.
            return new BatteryHoldState(Held: true, _armedTargetWatts, _armedAt, Wrote: false);
        }

        var registers = PowerControlPayload.Hold(targetWatts, _durationSeconds);
        var prefix = _dryRun ? "[DRY RUN] would hold " : "hold ";

        // Renewals are expected and frequent, so they go to debug; arming and retargeting are the
        // events worth seeing in a normal log.
        var log = reason == RenewalReason ? LogLevel.Debug : LogLevel.Information;
        _logger.Log(
            log,
            "{Prefix}battery discharge: active power target {TargetWatts}W for {DurationSeconds}s (registers [{Registers}] at 0x{Address:X2}). {Reason}",
            prefix, targetWatts, _durationSeconds, string.Join(",", registers), InverterRegisterMap.PowerControlBlockStart.Address, reason);

        if (!_dryRun)
        {
            await WriteAsync(registers, cancellationToken).ConfigureAwait(false);
        }

        _armedTargetWatts = targetWatts;
        _armedAt = now;
        return new BatteryHoldState(Held: true, targetWatts, now, Wrote: true);
    }

    // Why this cycle needs a write, or null if it doesn't.
    private string? ArmReason(double targetWatts, DateTimeOffset now)
    {
        if (_armedTargetWatts is not double armedTarget || _armedAt is not DateTimeOffset armedAt)
        {
            return "Hold switched on.";
        }

        // The target tracks live house load and PV, so it moves constantly. Only re-command once it
        // has moved far enough to matter, otherwise every poll would be a write.
        var movedWatts = Math.Abs(targetWatts - armedTarget);
        if (movedWatts >= _targetChangeThresholdWatts)
        {
            return $"Target moved {movedWatts:F0}W (from {armedTarget}W).";
        }

        // Clock going backwards (NTP step, telemetry timestamp jitter) must not defer the renewal
        // indefinitely, so treat any non-forward elapsed time as due.
        var elapsed = now - armedAt;
        return elapsed >= _renewAfter || elapsed < TimeSpan.Zero ? RenewalReason : null;
    }

    private async Task<BatteryHoldState> ReleaseAsync(CancellationToken cancellationToken)
    {
        if (_armedTargetWatts is null)
        {
            return new BatteryHoldState(Held: false, null, null, Wrote: false);
        }

        var registers = PowerControlPayload.Release();
        var prefix = _dryRun ? "[DRY RUN] would release " : "releasing ";

        _logger.LogInformation(
            "{Prefix}battery discharge hold (registers [{Registers}] at 0x{Address:X2}). Takes effect immediately; the inverter resumes its own work mode.",
            prefix, string.Join(",", registers), InverterRegisterMap.PowerControlBlockStart.Address);

        if (!_dryRun)
        {
            await WriteAsync(registers, cancellationToken).ConfigureAwait(false);
        }

        _armedTargetWatts = null;
        _armedAt = null;
        return new BatteryHoldState(Held: false, null, null, Wrote: true);
    }

    private async Task WriteAsync(ushort[] registers, CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _client
                .WriteMultipleRegistersAsync(InverterRegisterMap.PowerControlBlockStart.Address, registers, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Leave the recorded state alone so the next poll retries rather than assuming success.
            throw new InvalidOperationException(
                $"Failed to write the inverter power-control block at address {InverterRegisterMap.PowerControlBlockStart.Address}.", ex);
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (!_client.IsConnected)
        {
            await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
