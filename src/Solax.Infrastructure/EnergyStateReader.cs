using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Solax.Core.Enums;
using Solax.Core.Interfaces;
using Solax.Core.Models;
using Solax.Infrastructure.RegisterMaps;

namespace Solax.Infrastructure;

public sealed class EnergyStateReader : IEnergyStateReader
{
    // A RunMode value outside the register's real 0-13 range, which EvChargerStatusMapping turns into
    // EvChargerStatus.Unknown -- exactly what the UI and Home Assistant already show for a charger
    // whose state can't be determined. Reusing it means "the charger is unreachable" needs no new
    // field on EnergyState, and therefore no new entity anywhere downstream.
    private const ushort UnreachableStatusRaw = ushort.MaxValue;

    private readonly IModbusClient _inverterClient;
    private readonly IModbusClient _evChargerClient;
    private readonly ILogger<EnergyStateReader> _logger;

    // Whether the last poll reached the charger. Only used to keep the log to one line per outage
    // rather than one per poll; this service is a singleton, so it survives between polls.
    private bool _evChargerReachable = true;

    public EnergyStateReader(
        [FromKeyedServices(ModbusClientKeys.Inverter)] IModbusClient inverterClient,
        [FromKeyedServices(ModbusClientKeys.EvCharger)] IModbusClient evChargerClient,
        ILogger<EnergyStateReader> logger)
    {
        _inverterClient = inverterClient;
        _evChargerClient = evChargerClient;
        _logger = logger;
    }

    public async Task<EnergyState> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!_inverterClient.IsConnected)
        {
            await _inverterClient.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        var inverterBlock = await ReadInverterTelemetryBlockAsync(cancellationToken).ConfigureAwait(false);
        LogGridRegisterCandidates(inverterBlock);

        var evCharger = await ReadEvChargerAsync(cancellationToken).ConfigureAwait(false);

        return EnergyState.FromRawRegisters(
            DateTimeOffset.UtcNow,
            batterySocRaw: FromBlock(inverterBlock, InverterRegisterMap.BatteryCapacity),
            batteryPowerRaw: FromBlock(inverterBlock, InverterRegisterMap.BatteryPowerCharge1),
            pvPowerDc1Raw: FromBlock(inverterBlock, InverterRegisterMap.Powerdc1),
            pvPowerDc2Raw: FromBlock(inverterBlock, InverterRegisterMap.Powerdc2),
            feedinPowerLowRaw: FromBlock(inverterBlock, InverterRegisterMap.FeedinPowerLow),
            feedinPowerHighRaw: FromBlock(inverterBlock, InverterRegisterMap.FeedinPowerHigh),
            evChargerStatusRaw: evCharger.StatusRaw,
            evChargerPowerRaw: evCharger.PowerRaw)
            with
        { ChargeMode = evCharger.Mode, ChargeCurrentAmps = evCharger.CurrentAmps };
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

    // Reads the whole telemetry range in one Modbus request (the SolaX protocol requires
    // >=1 second between separate instructions, so batching beats many small reads).
    private async Task<ushort[]> ReadInverterTelemetryBlockAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _inverterClient
                .ReadInputRegistersAsync(
                    InverterRegisterMap.TelemetryBlockStart,
                    InverterRegisterMap.TelemetryBlockCount,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Failed to read inverter telemetry block starting at address {InverterRegisterMap.TelemetryBlockStart}.", ex);
        }
    }

    // Diagnostic: dumps every register that could plausibly be "the grid" so we can compare each one
    // against the live figures in the SolaX Cloud app and confirm which reading is real. FeedinPower
    // (0x46/0x47) is the grid METER we use for surplus; GridPowerR/S/T (0x6C/70/74) are the inverter's
    // AC output per phase (not the meter). All come from the one telemetry block, so this costs no
    // extra Modbus reads. Logs at Debug -- enable with Logging:LogLevel:Solax.Infrastructure=Debug.
    private void LogGridRegisterCandidates(ushort[] block)
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        var feedinLow = FromBlock(block, InverterRegisterMap.FeedinPowerLow);
        var feedinHigh = FromBlock(block, InverterRegisterMap.FeedinPowerHigh);
        var feedinWatts = unchecked((int)(((uint)feedinHigh << 16) | feedinLow));

        _logger.LogDebug(
            "Grid registers: FeedinPower(0x46/47)={FeedinWatts}W (raw low={FeedinLow} high={FeedinHigh}, +=export) | "
            + "inverter AC output GridPowerR(0x6C)={GridR}W S(0x70)={GridS}W T(0x74)={GridT}W",
            feedinWatts,
            feedinLow,
            feedinHigh,
            unchecked((short)FromBlock(block, InverterRegisterMap.GridPowerR)),
            unchecked((short)FromBlock(block, InverterRegisterMap.GridPowerS)),
            unchecked((short)FromBlock(block, InverterRegisterMap.GridPowerT)));
    }

    private static ushort FromBlock(ushort[] block, RegisterDescriptor register) => block[register.Address];

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
