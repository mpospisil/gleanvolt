using System.Net.Sockets;
using Gleanvolt.Core.Models;
using Gleanvolt.Infrastructure.RegisterMaps;
using Gleanvolt.Devices;
using Gleanvolt.Devices.Huawei;
using Gleanvolt.Devices.Solax;

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
    private const string ConnectionTakenHint =
        "Is Gleanvolt or Home Assistant connected to it right now? SolaX devices accept very few Modbus "
        + "connections, and Huawei's SDongle and EMMA only one; stop the other client for the scan. A Huawei "
        + "SDongle or EMMA also answers like this until Modbus TCP is enabled with the installer account.";

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

        // Huawei keeps a model name at 30000 rather than a serial number in SolaX's shape (#324).
        var huaweiName = await ReadAsync(
            pacer, () => client.ReadHoldingRegistersAsync(
                HuaweiRegisterMap.ModelName, HuaweiRegisterMap.ModelNameCount, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        if (huaweiName is { Outcome: ReadOutcome.Data, Registers: { } nameRegisters }
            && HuaweiSun2000Driver.DecodeString(nameRegisters) is { Length: > 0 } huaweiModel)
        {
            if (HuaweiSun2000Driver.IsSun2000(huaweiModel))
            {
                return await HuaweiInverterAsync(client, pacer, host, port, unitId, huaweiModel, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (huaweiModel.StartsWith("EMMA", StringComparison.OrdinalIgnoreCase))
            {
                return new UnitAttempt.Answered(new DiscoveredDevice(
                    host, port, unitId, DiscoveryFinding.HuaweiDevice, null, huaweiModel, "EMMA energy manager", null,
                    null, null,
                    "A Huawei EMMA energy manager. Use the inverter behind it instead: the same address, on the unit id "
                    + "the EMMA gave the inverter, connected through the EMMA."));
            }
        }

        // Huawei's SCharger keeps no serial number and no model name: its phase voltages at 0x1000 are what
        // its own driver's probe reads (#353).
        if (serial is null && otherSerial is null)
        {
            var voltages = await ReadAsync(
                pacer, () => client.ReadHoldingRegistersAsync(SChargerRegisterMap.PhaseL1Voltage, 6, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            if (voltages is { Outcome: ReadOutcome.Data, Registers: { Length: >= 6 } phases }
                && HuaweiSChargerDriver.IsMainsVoltage(Word32(phases, 0)))
            {
                return new UnitAttempt.Answered(SCharger(host, port, unitId, phases));
            }
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
            "Answers Modbus, but has no SolaX serial number, no Huawei model name and is no SCharger: not a device "
            + "Gleanvolt knows."));
    }

    // A SUN2000 (#324): its serial number, to tell repeated answers apart; the battery's state of charge,
    // as the driver's own probe reads it; and whether an EMMA answers for the grid on unit 0 of the same
    // connection, which decides the topology. Readings only are Expected; the hold is untested.
    private async Task<UnitAttempt> HuaweiInverterAsync(
        IModbusClient client,
        HostPacer pacer,
        string host,
        int port,
        byte unitId,
        string model,
        CancellationToken cancellationToken)
    {
        var serialRead = await ReadAsync(
            pacer, () => client.ReadHoldingRegistersAsync(
                HuaweiRegisterMap.SerialNumber, HuaweiRegisterMap.SerialNumberCount, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        var serial = serialRead is { Outcome: ReadOutcome.Data, Registers: { } serialRegisters }
            && HuaweiSun2000Driver.DecodeString(serialRegisters) is { Length: > 0 } text
                ? text
                : null;

        var socRead = await ReadAsync(
            pacer, () => client.ReadHoldingRegistersAsync(HuaweiRegisterMap.BatterySoc, 1, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        bool? confirmed = null;
        string note;
        if (socRead is { Outcome: ReadOutcome.Data, Registers: [var soc, ..] })
        {
            confirmed = soc <= 1000;
            note = confirmed.Value
                ? $"Battery at {soc / 10.0:0.#} %."
                : $"The model name says inverter, but its battery register reads {soc}, which is no state of charge.";
        }
        else
        {
            note = $"Did not answer the battery register at {HuaweiRegisterMap.BatterySoc}: is a LUNA2000 battery connected?";
        }

        var behindEmma = unitId != HuaweiRegisterMap.EmmaUnitId
            && await AnswersAsEmmaAsync(client, pacer, cancellationToken).ConfigureAwait(false);
        note += behindEmma
            ? " Behind a Huawei EMMA, which accepts only one Modbus client: set Connected through to EMMA."
            : " Readings are Expected, the battery hold untested; see SUPPORTED_PV_SYSTEMS.md.";

        return new UnitAttempt.Answered(new DiscoveredDevice(
            host, port, unitId, DiscoveryFinding.HuaweiDevice, PvDeviceRole.Inverter, model, "SUN2000 hybrid", serial,
            SupportTier.Expected, confirmed, note,
            Driver: HuaweiSun2000DriverFactory.DriverId,
            Topology: behindEmma ? HuaweiSun2000DriverFactory.EmmaTopology : null));
    }

    // An SCharger (#353). The protocol names no model, so the phases decide it: L2 and L3 with no mains voltage
    // is the one-phase 7KS. A 22KT wired to one phase reads the same, which the note says.
    private static DiscoveredDevice SCharger(string host, int port, byte unitId, ushort[] phases)
    {
        var l1 = Word32(phases, 0);
        var threePhase = HuaweiSChargerDriver.IsMainsVoltage(Word32(phases, 2)) || HuaweiSChargerDriver.IsMainsVoltage(Word32(phases, 4));

        return new DiscoveredDevice(
            host, port, unitId, DiscoveryFinding.HuaweiDevice, PvDeviceRole.Charger,
            threePhase ? "SCharger-22KT-S0" : "SCharger-7KS-S0", "SCharger EV charger", null, SupportTier.Expected, true,
            $"Phase L1 at {l1 / 10.0:0.#} V; {(threePhase ? "three phases" : "one phase")} live, so taken for the "
            + $"{(threePhase ? "22KT" : "7KS")}: check the label. It reports no status, so an idle charger reads unknown. "
            + "Untested on hardware; behind an EMMA, leave it to the EMMA.",
            Driver: threePhase ? HuaweiSChargerDriverFactory.TwentyTwoKtId : HuaweiSChargerDriverFactory.SevenKsId);
    }

    private static uint Word32(ushort[] registers, int offset) =>
        ((uint)registers[offset] << 16) | registers[offset + 1];

    // Unit 0 on the same connection -- the EMMA takes one client -- answering the grid register the driver
    // reads from it. An SDongle has nothing there.
    private static async Task<bool> AnswersAsEmmaAsync(IModbusClient client, HostPacer pacer, CancellationToken cancellationToken)
    {
        IModbusClient emma;
        try
        {
            emma = client.ForUnit(HuaweiRegisterMap.EmmaUnitId);
        }
        catch (NotSupportedException)
        {
            return false;
        }

        var read = await ReadAsync(
            pacer, () => emma.ReadHoldingRegistersAsync(HuaweiRegisterMap.EmmaGridActivePower, 2, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return read.Outcome == ReadOutcome.Data;
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
        // A gateway's 0x0A "path unavailable" or 0x0B "target failed to respond": nothing at that unit id
        // replied on its RS485 side. "This unit id is not there", never "found a device".
        catch (ModbusDeviceException ex) when (ex.IsGateway)
        {
            return new RegisterRead(ReadOutcome.Gateway, null);
        }
        catch (ModbusDeviceException)
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
