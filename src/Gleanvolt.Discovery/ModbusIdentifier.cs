using System.Net.Sockets;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Infrastructure.RegisterMaps;
using NModbus;

namespace Gleanvolt.Discovery;

/// <summary>
/// Speaks Modbus to one open port of one host: tries the unit ids in turn, reads the serial number and
/// the role check, and says what answered. Strictly sequential, paced by the host's
/// <see cref="HostPacer"/>, and each unit id on its own short-lived connection, so a running Gleanvolt
/// is locked out for one probe at a time at most.
/// </summary>
internal sealed class ModbusIdentifier(
    DiscoveryOptions options,
    Func<string, int, byte, IModbusClient> openClient)
{
    // Modbus exception codes a gateway answers with when nothing at that unit id replied on its RS485
    // side: 0x0A "gateway path unavailable", 0x0B "gateway target device failed to respond". They mean
    // "this unit id is not there", never "found a device".
    private const byte GatewayPathUnavailable = 0x0A;
    private const byte GatewayTargetFailedToRespond = 0x0B;

    private const string ConnectionTakenHint =
        "Is Gleanvolt or Home Assistant connected to it right now? SolaX devices accept very few Modbus "
        + "connections; stop the other client for the scan.";

    public async Task<IReadOnlyList<DiscoveredDevice>> IdentifyAsync(
        string host,
        int port,
        HostPacer pacer,
        CancellationToken cancellationToken)
    {
        var found = new List<DiscoveredDevice>();
        var noReply = 0;
        var gatewayRefusals = 0;
        string? connectFailure = null;

        foreach (var unitId in options.UnitIds)
        {
            var attempt = await TryUnitAsync(host, port, unitId, pacer, cancellationToken).ConfigureAwait(false);

            switch (attempt)
            {
                case UnitAttempt.Answered answered:
                    // A device that ignores the unit id answers on every one of them, with the same serial:
                    // one result, not seven.
                    if (answered.Device.SerialNumber is null
                        || !found.Any(device => device.SerialNumber == answered.Device.SerialNumber))
                    {
                        found.Add(answered.Device);
                    }

                    break;
                case UnitAttempt.NoReply:
                    noReply++;
                    break;
                case UnitAttempt.NotThere:
                    gatewayRefusals++;
                    break;
                case UnitAttempt.ConnectFailed failed:
                    // The connection, not the unit id, is what failed: every other unit id would fail the
                    // same way and cost another connect timeout each.
                    connectFailure = failed.Reason;
                    break;
            }

            if (connectFailure is not null || (found.Count > 0 && !options.AllUnitIds))
            {
                break;
            }
        }

        if (found.Count > 0)
        {
            return found;
        }

        var tried = string.Join(",", options.UnitIds);

        if (connectFailure is not null)
        {
            return [Unanswered(host, port, DiscoveryFinding.NoModbusReply,
                $"Port open, but the Modbus connection failed ({connectFailure}). {ConnectionTakenHint}")];
        }

        if (gatewayRefusals > 0 && noReply == 0)
        {
            return [Unanswered(host, port, DiscoveryFinding.NoUnitAnswered,
                $"A Modbus gateway answered, but nothing behind it replied on unit ids {tried}. Check the "
                + "RS485 wiring and the device's Modbus address, or pass --unit-ids.")];
        }

        return [Unanswered(host, port, DiscoveryFinding.NoModbusReply,
            $"Port open, no Modbus reply on unit ids {tried}. {ConnectionTakenHint}")];
    }

    private async Task<UnitAttempt> TryUnitAsync(
        string host,
        int port,
        byte unitId,
        HostPacer pacer,
        CancellationToken cancellationToken)
    {
        await using var client = openClient(host, port, unitId);

        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new UnitAttempt.ConnectFailed(ex.Message);
        }

        var inverterSerial = await ReadAsync(
            pacer, () => client.ReadHoldingRegistersAsync(
                InverterRegisterMap.SerialNumberStart, InverterRegisterMap.SerialNumberCount, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        switch (inverterSerial.Outcome)
        {
            case ReadOutcome.NoReply:
                return new UnitAttempt.NoReply();
            case ReadOutcome.Gateway:
                return new UnitAttempt.NotThere();
        }

        var serial = Serial(inverterSerial);

        if (SolaxModels.Inverter(serial) is { } inverter)
        {
            return await IdentifiedAsync(client, pacer, host, port, unitId, serial!, inverter, cancellationToken)
                .ConfigureAwait(false);
        }

        // Something is at this unit id. The charger keeps its serial number elsewhere.
        var chargerSerial = await ReadAsync(
            pacer, () => client.ReadHoldingRegistersAsync(
                EvChargerRegisterMap.SerialNumberStart, EvChargerRegisterMap.SerialNumberCount, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        var otherSerial = Serial(chargerSerial);

        if (SolaxModels.Charger(otherSerial) is { } charger)
        {
            return await IdentifiedAsync(client, pacer, host, port, unitId, otherSerial!, charger, cancellationToken)
                .ConfigureAwait(false);
        }

        if ((serial ?? otherSerial) is { } unknown)
        {
            return new UnitAttempt.Answered(new DiscoveredDevice(
                host, port, unitId, DiscoveryFinding.SolaxDevice, null, null, null, unknown,
                SupportTier.UnknownSolax, null,
                $"Serial number {unknown} is not one Gleanvolt knows. Please open an issue with its first few "
                + "characters and the model on the device's label."));
        }

        return new UnitAttempt.Answered(new DiscoveredDevice(
            host, port, unitId, DiscoveryFinding.NotSolax, null, null, null, null, SupportTier.NotSolax, null,
            "Answers Modbus, but has no SolaX serial number: not a SolaX."));
    }

    // The serial number says what the box should be; the role check — the register /pv-system's probe
    // reads — says whether it behaves like it.
    private async Task<UnitAttempt> IdentifiedAsync(
        IModbusClient client,
        HostPacer pacer,
        string host,
        int port,
        byte unitId,
        string serial,
        SolaxModel model,
        CancellationToken cancellationToken)
    {
        var register = DeviceRoleCheck.Register(model.Role);
        var read = await ReadAsync(
            pacer, () => client.ReadInputRegistersAsync(register, 1, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        bool? confirmed = null;
        string note;

        if (read is { Outcome: ReadOutcome.Data, Registers: [var value, ..] })
        {
            confirmed = DeviceRoleCheck.IsPlausible(model.Role, value);
            note = (model.Role, confirmed) switch
            {
                (PvDeviceRole.Inverter, true) => $"Battery at {value} %.",
                (PvDeviceRole.Inverter, _) =>
                    $"The serial number says inverter, but its battery register reads {value}, which is no state of charge.",
                (_, true) => $"Charger run mode {value}.",
                _ => $"The serial number says charger, but its run-mode register reads {value}, which no charger reports.",
            };
        }
        else
        {
            note = $"Did not answer the role check at input register 0x{register:X2}.";
        }

        if (model.Tier == SupportTier.NotSupported)
        {
            note += " Gleanvolt cannot drive this model; see SUPPORTED_PV_SYSTEMS.md.";
        }

        return new UnitAttempt.Answered(new DiscoveredDevice(
            host, port, unitId, DiscoveryFinding.SolaxDevice, model.Role, model.Model, model.Family, serial,
            model.Tier, confirmed, note));
    }

    private static async Task<RegisterRead> ReadAsync(
        HostPacer pacer,
        Func<Task<ushort[]>> read,
        CancellationToken cancellationToken)
    {
        await pacer.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return new RegisterRead(ReadOutcome.Data, await read().ConfigureAwait(false));
        }
        catch (SlaveException ex) when (ex.SlaveExceptionCode is GatewayPathUnavailable or GatewayTargetFailedToRespond)
        {
            return new RegisterRead(ReadOutcome.Gateway, null);
        }
        catch (SlaveException)
        {
            // Illegal function, illegal address: a device is there, it just keeps nothing at this one.
            return new RegisterRead(ReadOutcome.Refused, null);
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or ObjectDisposedException
            or InvalidOperationException)
        {
            // A timeout, a connection the device closed, or a reply NModbus could not make sense of.
            return new RegisterRead(ReadOutcome.NoReply, null);
        }
        finally
        {
            pacer.Sent();
        }
    }

    private static string? Serial(RegisterRead read) =>
        read is { Outcome: ReadOutcome.Data, Registers: { } registers } ? SolaxModels.DecodeSerialNumber(registers) : null;

    private static DiscoveredDevice Unanswered(string host, int port, DiscoveryFinding finding, string note) =>
        new(host, port, null, finding, null, null, null, null, null, null, note);

    private enum ReadOutcome
    {
        Data,
        Refused,
        Gateway,
        NoReply,
    }

    private sealed record RegisterRead(ReadOutcome Outcome, ushort[]? Registers);

    private abstract record UnitAttempt
    {
        public sealed record Answered(DiscoveredDevice Device) : UnitAttempt;

        public sealed record NoReply : UnitAttempt;

        public sealed record NotThere : UnitAttempt;

        public sealed record ConnectFailed(string Reason) : UnitAttempt;
    }
}
