using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Infrastructure.RegisterMaps;

namespace Gleanvolt.Infrastructure;

/// <summary>
/// <see cref="IEvChargerControl"/> over Modbus. Reads the charger's use-mode and current setpoint, and
/// writes <em>only</em> the current setpoint — never the use-mode, never a start/stop command.
///
/// In dry-run mode nothing is written: the intended change is logged (including the encoded register
/// value) and a simulated current stands in for the hardware, so the logs read like a real run (change
/// once, then quiet) without touching the charger.
/// </summary>
public sealed class EvChargerControl : IEvChargerControl
{
    // The SolaX charge-current holding register stores hundredths of an amp: registerValue = amps * 100
    // (16A -> 1600). We allow 0 (pause) up to the hardware maximum.
    private const double CurrentRegisterAmpsPerCount = 0.01;

    private readonly IModbusClient _client;
    private readonly ILogger<EvChargerControl> _logger;
    private readonly bool _dryRun;
    private readonly int _currentChangeThresholdAmps;

    // Dry-run only: the current setpoint the charger "would" now have, so reads reflect prior simulated
    // writes and change-detection behaves like a real run.
    private int? _simulatedCurrentAmps;

    public EvChargerControl(
        [FromKeyedServices(ModbusClientKeys.EvCharger)] IModbusClient client,
        ILogger<EvChargerControl> logger,
        bool dryRun = false,
        int currentChangeThresholdAmps = 1)
    {
        _client = client;
        _logger = logger;
        _dryRun = dryRun;
        _currentChangeThresholdAmps = Math.Max(1, currentChangeThresholdAmps);
    }

    public async Task<EvChargerSettings> ReadSettingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var mode = await ReadRegisterAsync(EvChargerRegisterMap.ChargerUseMode, cancellationToken).ConfigureAwait(false);

        // The use-mode is always read live (we never write it); the current is the simulated value in
        // dry-run once we've "written" one, else read live and decoded from the 0.01A scale.
        int currentAmps;
        if (_dryRun && _simulatedCurrentAmps is int simulated)
        {
            currentAmps = simulated;
        }
        else
        {
            var currentRaw = await ReadRegisterAsync(EvChargerRegisterMap.ChargeCurrentSetpoint, cancellationToken).ConfigureAwait(false);
            currentAmps = (int)Math.Round(currentRaw * CurrentRegisterAmpsPerCount);
        }

        return new EvChargerSettings((EvChargerMode)mode, currentAmps);
    }

    public async Task SetCurrentAsync(int activeAmps, int targetAmps, string reason, CancellationToken cancellationToken = default)
    {
        // Never below 0 (pause) or above the hardware maximum.
        var clampedAmps = Math.Clamp(targetAmps, 0, EvChargerLimits.MaxCurrentAmps);

        // Hysteresis: only re-command once the target has moved by at least the threshold (1A default).
        if (Math.Abs(clampedAmps - activeAmps) < _currentChangeThresholdAmps)
        {
            return;
        }

        var registerValue = (ushort)Math.Round(clampedAmps / CurrentRegisterAmpsPerCount);
        var prefix = _dryRun ? "[DRY RUN] would set " : "";

        _logger.LogInformation(
            "{Prefix}charger current setpoint: {OldAmps}A -> {NewAmps}A (register {RegisterValue}). {Reason}",
            prefix, activeAmps, clampedAmps, registerValue, reason);

        if (_dryRun)
        {
            _simulatedCurrentAmps = clampedAmps;
            return;
        }

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _client
            .WriteSingleRegisterAsync(EvChargerRegisterMap.ChargeCurrentSetpoint.Address, registerValue, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ushort> ReadRegisterAsync(RegisterDescriptor register, CancellationToken cancellationToken)
    {
        try
        {
            var values = await _client
                .ReadHoldingRegistersAsync(register.Address, numberOfPoints: 1, cancellationToken)
                .ConfigureAwait(false);
            return values[0];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Failed to read charger register '{register.Name}' at address {register.Address}.", ex);
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
