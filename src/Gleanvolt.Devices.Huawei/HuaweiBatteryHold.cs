using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Gleanvolt.Devices.Huawei;

/// <summary>
/// The SUN2000's battery hold (issue #323): the maximum discharging power
/// (<see cref="HuaweiRegisterMap.MaxDischargePower"/>) written as 0 to hold, and its original value written
/// back to release, as evcc does it. Written only on a change of state, never every poll.
///
/// <para><b>A lasting setting, unlike SolaX's command.</b> If Gleanvolt stops during a hold without
/// releasing it, the battery stays blocked until something writes the register again. So:</para>
/// <list type="bullet">
/// <item>The original is stored (<see cref="IDriverStateStore"/>, on disk in the host) <i>before</i> 0 is
/// written; a store that fails means no write.</item>
/// <item>It is written back on release, and on a normal stop (<see cref="ReleaseOnShutdownAsync"/>).</item>
/// <item>The first reconciliation after a start recovers from a crash: if a stored original exists, the
/// register reads 0 and no hold is due, the original goes back, and the log says so.</item>
/// <item>0 is never stored as the original. A register that already reads 0 with nothing stored is somebody
/// else's setting: it is logged and left alone.</item>
/// </list>
///
/// <para><b>Arming is spaced.</b> Whether the register lives in EEPROM, with a limited number of writes, is
/// not known yet, so a hold is not armed again within <see cref="MinArmInterval"/> of the last write.
/// Releasing is never delayed: a blocked battery is the outcome to avoid.</para>
///
/// <para>The target the controller computes is not used: the register takes a limit, not an active-power
/// set-point, and the hold blocks discharge outright.</para>
/// </summary>
public sealed class HuaweiBatteryHold : IBatteryDischargeControl
{
    /// <summary>The state key the original maximum discharging power is kept under.</summary>
    public const string OriginalKey = "huawei:maxDischargePowerOriginal";

    /// <summary>The shortest time between a write and the next arming.</summary>
    public static TimeSpan MinArmInterval { get; } = TimeSpan.FromMinutes(1);

    private readonly IModbusClient _client;
    private readonly IDriverStateStore _state;
    private readonly ILogger _logger;
    private readonly bool _dryRun;
    private readonly TimeProvider _time;

    private bool _recovered;
    private bool _held;
    private DateTimeOffset? _armedAt;
    private DateTimeOffset? _lastWriteAt;

    // In dry-run nothing is stored, so the original the release would write back is remembered here, for
    // the log line only.
    private uint? _dryRunOriginal;

    public HuaweiBatteryHold(IModbusClient client, IDriverStateStore state, ILogger logger, bool dryRun, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _state = state;
        _logger = logger;
        _dryRun = dryRun;
        _time = time ?? TimeProvider.System;
    }

    public async Task<BatteryHoldState> ApplyAsync(
        bool hold, double activePowerTargetWatts, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (!_recovered)
        {
            await RecoverAsync(hold, now, cancellationToken).ConfigureAwait(false);
            _recovered = true;
        }

        if (hold == _held)
        {
            return State(wrote: false);
        }

        return hold
            ? await ArmAsync(now, cancellationToken).ConfigureAwait(false)
            : await ReleaseAsync(now, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReleaseOnShutdownAsync(CancellationToken cancellationToken = default)
    {
        // Also when this run never armed: a stored original means a hold from an earlier run is still on
        // the inverter, and recovery has not had its poll yet.
        if (_held || (!_dryRun && Stored() is not null))
        {
            _logger.LogInformation("Stopping: releasing the Huawei battery hold so the battery is not left blocked.");
            await ReleaseAsync(_time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }
    }

    // A stored original means the last run changed the register and did not put it back.
    private async Task RecoverAsync(bool hold, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (Stored() is not { } original)
        {
            return;
        }

        var current = await ReadMaxDischargeAsync(cancellationToken).ConfigureAwait(false);
        if (current != 0)
        {
            // Put back by someone else (the app, an installer): the stored value is stale.
            _logger.LogInformation(
                "A stored original maximum discharging power ({Original} W) was found, but the inverter reads {Current} W: "
                + "the hold was already released. Forgetting the stored value.",
                original, current);
            _state.Set(OriginalKey, null);
            return;
        }

        if (hold)
        {
            // Still due: the hold from the last run carries on, with the original it stored.
            _logger.LogInformation(
                "The Huawei battery hold from before the restart is still on (maximum discharging power 0 W, original {Original} W stored); keeping it.",
                original);
            _held = true;
            _armedAt = now;
            return;
        }

        _logger.LogWarning(
            "The Huawei battery hold was left on by an earlier run (maximum discharging power 0 W); restoring the original {Original} W.",
            original);
        await WriteMaxDischargeAsync(original, cancellationToken).ConfigureAwait(false);
        _state.Set(OriginalKey, null);
        _lastWriteAt = now;
    }

    private async Task<BatteryHoldState> ArmAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_lastWriteAt is { } last && now - last < MinArmInterval)
        {
            _logger.LogDebug("Huawei battery hold: not arming within {Interval} of the last write.", MinArmInterval);
            return State(wrote: false);
        }

        var current = await ReadMaxDischargeAsync(cancellationToken).ConfigureAwait(false);
        if (current == 0)
        {
            // Never store 0 as the original: a release would then write 0 back and block the battery for good.
            // The battery is blocked already, by someone else's setting, which is theirs to undo.
            _logger.LogWarning(
                "The Huawei battery hold was asked for, but the maximum discharging power already reads 0 W and no original "
                + "is stored: it was set by someone else. Leaving it alone; Gleanvolt will not change it on release either.");
            _held = true;
            _armedAt = now;
            return State(wrote: false);
        }

        if (_dryRun)
        {
            _dryRunOriginal = current;
            _logger.LogInformation(
                "[DRY RUN] would hold the Huawei battery: maximum discharging power {Original} W -> 0 W.", current);
        }
        else
        {
            // Stored first: if it cannot be kept, 0 is not written.
            _state.Set(OriginalKey, current.ToString(CultureInfo.InvariantCulture));
            _logger.LogInformation("Holding the Huawei battery: maximum discharging power {Original} W -> 0 W.", current);
            await WriteMaxDischargeAsync(0, cancellationToken).ConfigureAwait(false);
        }

        _held = true;
        _armedAt = now;
        _lastWriteAt = now;
        return State(wrote: true);
    }

    private async Task<BatteryHoldState> ReleaseAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_dryRun)
        {
            _logger.LogInformation(
                "[DRY RUN] would release the Huawei battery hold: maximum discharging power 0 W -> {Original} W.",
                (object?)_dryRunOriginal ?? "the stored original");
            _dryRunOriginal = null;
            return Released(now, wrote: true);
        }

        if (Stored() is not { } original)
        {
            _logger.LogWarning(
                "Releasing the Huawei battery hold, but no original maximum discharging power is stored; the register is left as it is.");
            return Released(now, wrote: false);
        }

        _logger.LogInformation("Releasing the Huawei battery hold: maximum discharging power 0 W -> {Original} W.", original);
        await WriteMaxDischargeAsync(original, cancellationToken).ConfigureAwait(false);
        _state.Set(OriginalKey, null);
        _lastWriteAt = now;
        return Released(now, wrote: true);
    }

    private BatteryHoldState Released(DateTimeOffset now, bool wrote)
    {
        _held = false;
        _armedAt = null;
        if (wrote)
        {
            _lastWriteAt = now;
        }

        return State(wrote);
    }

    private BatteryHoldState State(bool wrote) =>
        new(_held, _held ? 0 : null, _held ? _armedAt : null, wrote);

    private uint? Stored() =>
        uint.TryParse(_state.Get(OriginalKey), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value != 0
            ? value
            : null;

    private async Task<uint> ReadMaxDischargeAsync(CancellationToken cancellationToken)
    {
        var registers = await _client.ReadHoldingRegistersAsync(HuaweiRegisterMap.MaxDischargePower, 2, cancellationToken).ConfigureAwait(false);
        return ((uint)registers[0] << 16) | registers[1];
    }

    private Task WriteMaxDischargeAsync(uint watts, CancellationToken cancellationToken) =>
        _client.WriteMultipleRegistersAsync(
            HuaweiRegisterMap.MaxDischargePower, [(ushort)(watts >> 16), (ushort)watts], cancellationToken);
}
