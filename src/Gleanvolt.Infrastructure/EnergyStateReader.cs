using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Devices;
using Gleanvolt.Infrastructure.RegisterMaps;

namespace Gleanvolt.Infrastructure;

/// <summary>
/// One telemetry poll, composed (issue #317): the inverter through its driver, the charger as before and
/// best-effort, the correction for a grid meter blind on one phase, and the <see cref="EnergyState"/> the
/// rest of the controller reads. Nothing here knows the inverter's brand.
/// </summary>
public sealed class EnergyStateReader : IEnergyStateReader
{
    // A RunMode value outside the register's real 0-13 range, which EvChargerStatusMapping turns into
    // EvChargerStatus.Unknown -- exactly what the UI and Home Assistant already show for a charger
    // whose state can't be determined. Reusing it means "the charger is unreachable" needs no new
    // field on EnergyState, and therefore no new entity anywhere downstream.
    private const ushort UnreachableStatusRaw = ushort.MaxValue;

    private readonly IInverterDriver _inverter;
    private readonly IModbusClient _evChargerClient;
    private readonly ILogger<EnergyStateReader> _logger;

    // The phase the grid meter cannot see, or null for a meter that sees all three. Corrected here, once,
    // rather than in each consumer: surplus, house profile, battery hold, energy intervals and Home
    // Assistant then all read the same number without knowing there was anything to correct.
    private readonly GridPhase? _unmeteredGridPhase;
    private readonly int _evPhases;

    // Whether the last poll reached the charger. Only used to keep the log to one line per outage
    // rather than one per poll; this service is a singleton, so it survives between polls.
    private bool _evChargerReachable = true;

    /// <param name="inverter">The inverter's driver.</param>
    /// <param name="site">The installation, for its unmetered grid phase. Null reads the meter as it is.</param>
    /// <param name="limits">The car's phase count, for how its load divides between phases. Null assumes three.</param>
    public EnergyStateReader(
        IInverterDriver inverter,
        [FromKeyedServices(ModbusClientKeys.EvCharger)] IModbusClient evChargerClient,
        ILogger<EnergyStateReader> logger,
        PvSystemInfo? site = null,
        ChargingLimits? limits = null)
    {
        _inverter = inverter;
        _evChargerClient = evChargerClient;
        _logger = logger;
        _unmeteredGridPhase = site?.UnmeteredGridPhase;
        _evPhases = limits?.Phases ?? 3;

        // The correction needs the inverter's output per phase. Only SolaX exists today and it reports
        // one, so this cannot fire yet; once the driver is chosen in configuration (#318), the rules
        // refuse the combination before it gets here.
        if (_unmeteredGridPhase is { } blind && !inverter.Capabilities.HasPhaseOutput)
        {
            _logger.LogWarning(
                "The grid meter is configured as blind on {Phase}, but this inverter does not report its output per phase, so the meter is read as it is.",
                blind);
        }
    }

    public async Task<EnergyState> ReadAsync(CancellationToken cancellationToken = default)
    {
        var inverter = await _inverter.ReadAsync(cancellationToken).ConfigureAwait(false);
        var evCharger = await ReadEvChargerAsync(cancellationToken).ConfigureAwait(false);

        // Unsigned: the HAC charger doesn't support V2G.
        var state = new EnergyState(
            DateTimeOffset.UtcNow,
            inverter.BatterySocPercent,
            inverter.BatteryWatts,
            inverter.PvWatts,
            inverter.GridWatts,
            EvChargerStatusMapping.FromRaw(evCharger.StatusRaw),
            evCharger.PowerRaw)
        {
            ChargeMode = evCharger.Mode,
            ChargeCurrentAmps = evCharger.CurrentAmps,
        };

        return _unmeteredGridPhase is { } blind && inverter.InverterOutputPerPhase is { } inverterOutput
            ? WithUnmeteredPhaseEstimated(state, inverterOutput, blind)
            : state;
    }

    private EnergyState WithUnmeteredPhaseEstimated(EnergyState state, PhaseWatts inverterOutput, GridPhase blind)
    {
        var estimate = UnmeteredGridPhaseEstimate.GridPowerWatts(
            state.GridPowerWatts, blind, inverterOutput, state.EvChargerPowerWatts, _evPhases);

        return state with
        {
            GridPowerWatts = Math.Round(estimate),
            MeteredGridPowerWatts = state.GridPowerWatts,
        };
    }

    // The charger is optional in a way the inverter is not: it can be switched off, moved to another
    // address by DHCP, or simply absent from the installation -- and none of that should cost us the
    // inverter telemetry that was just read successfully. So everything charger-shaped is
    // best-effort, the connect included. That connect is what the per-register Try* helpers below
    // could never guard: they only run once a connection exists, and an absent charger fails earlier
    // than that.
    private async Task<EvChargerReading> ReadEvChargerAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!_evChargerClient.IsConnected)
            {
                await _evChargerClient.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }

            var statusRaw = await ReadAsync(_evChargerClient, EvChargerRegisterMap.RunMode, cancellationToken).ConfigureAwait(false);
            var powerRaw = await ReadAsync(_evChargerClient, EvChargerRegisterMap.ChargePowerTotal, cancellationToken).ConfigureAwait(false);
            var mode = await TryReadChargeModeAsync(cancellationToken).ConfigureAwait(false);
            var currentAmps = await TryReadChargeCurrentAsync(cancellationToken).ConfigureAwait(false);

            if (!_evChargerReachable)
            {
                _evChargerReachable = true;
                _logger.LogInformation("EV charger is reachable again; its telemetry is live.");
            }

            return new EvChargerReading(statusRaw, powerRaw, mode, currentAmps);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One line per outage, not one per poll: this runs on the polling interval, and a charger
            // that is switched off stays switched off for hours. The recovery above closes the pair,
            // so the log says when it went and when it came back.
            if (_evChargerReachable)
            {
                _evChargerReachable = false;
                _logger.LogWarning(
                    ex,
                    "EV charger is unreachable; reporting it as {Status} and continuing with inverter telemetry.",
                    EvChargerStatus.Unknown);
            }

            return new EvChargerReading(UnreachableStatusRaw, PowerRaw: 0, Mode: null, CurrentAmps: null);
        }
    }

    private readonly record struct EvChargerReading(ushort StatusRaw, ushort PowerRaw, EvChargerMode? Mode, int? CurrentAmps);

    // Reads the charger's active current setpoint (a holding register, 0.01A scale). Best-effort for
    // the same reason as the mode: a failed read must not fail the whole telemetry poll.
    private async Task<int?> TryReadChargeCurrentAsync(CancellationToken cancellationToken)
    {
        try
        {
            var values = await _evChargerClient
                .ReadHoldingRegistersAsync(EvChargerRegisterMap.ChargeCurrentSetpoint.Address, numberOfPoints: 1, cancellationToken)
                .ConfigureAwait(false);
            return (int)Math.Round(values[0] * 0.01);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    // Reads the charger's work/use mode (a holding register). Best-effort: this register isn't
    // available on every charger/firmware, so a failed read yields null rather than failing the
    // whole telemetry poll. A value outside the known enum range is also treated as unknown.
    private async Task<EvChargerMode?> TryReadChargeModeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var values = await _evChargerClient
                .ReadHoldingRegistersAsync(EvChargerRegisterMap.ChargerUseMode.Address, numberOfPoints: 1, cancellationToken)
                .ConfigureAwait(false);
            var raw = values[0];
            return raw <= (ushort)EvChargerMode.Green ? (EvChargerMode)raw : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<ushort> ReadAsync(
        IModbusClient client,
        RegisterDescriptor register,
        CancellationToken cancellationToken)
    {
        try
        {
            var values = await client
                .ReadInputRegistersAsync(register.Address, numberOfPoints: 1, cancellationToken)
                .ConfigureAwait(false);
            return values[0];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Failed to read register '{register.Name}' at address {register.Address}.", ex);
        }
    }
}
