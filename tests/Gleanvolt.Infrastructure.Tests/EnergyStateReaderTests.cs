using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Enums;
using Gleanvolt.Devices;
using Gleanvolt.Devices.Solax;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// The charger is optional hardware; the inverter is not. These pin that asymmetry: a charger that is
/// switched off, absent, or sitting on an address DHCP has since handed to something else must not
/// cost us the inverter telemetry, which is the whole point of the poll.
/// </summary>
public class EnergyStateReaderTests
{
    private static EnergyStateReader Reader(IModbusClient inverter, IModbusClient evCharger) =>
        new(new SolaxInverterDriver(inverter), new SolaxChargerDriver(evCharger), NullLogger<EnergyStateReader>.Instance);

    [Fact]
    public async Task Reports_inverter_telemetry_when_the_charger_cannot_be_connected()
    {
        var inverter = new FakeModbusClient();
        inverter.SetHolding(InverterRegisterMap.BatteryCapacity.Address, 47);
        inverter.SetHolding(InverterRegisterMap.Powerdc1.Address, 1200);

        var state = await Reader(inverter, new UnreachableModbusClient()).ReadAsync();

        // The inverter half survives intact -- the regression this guard exists for.
        Assert.Equal(47, state.BatterySocPercent);
        Assert.Equal(1200, state.SolarPowerWatts);
    }

    [Fact]
    public async Task Reports_the_charger_as_unknown_when_it_cannot_be_connected()
    {
        var state = await Reader(new FakeModbusClient(), new UnreachableModbusClient()).ReadAsync();

        Assert.Equal(EvChargerStatus.Unknown, state.EvChargerStatus);
        Assert.Equal(0, state.EvChargerPowerWatts);
        Assert.Null(state.ChargeMode);
        Assert.Null(state.ChargeCurrentAmps);
    }

    [Fact]
    public async Task Reports_the_charger_as_unknown_when_it_connects_but_its_registers_fail()
    {
        // Distinct from being unconnectable: the socket opens and then reads throw, which is what a
        // charger dropping its Modbus server mid-session looks like. RunMode and ChargePowerTotal
        // used to be mandatory reads that threw straight out of the poll.
        var state = await Reader(new FakeModbusClient(), new ConnectsThenFailsModbusClient()).ReadAsync();

        Assert.Equal(EvChargerStatus.Unknown, state.EvChargerStatus);
        Assert.Equal(0, state.EvChargerPowerWatts);
    }

    [Fact]
    public async Task Still_fails_the_poll_when_the_inverter_is_unreachable()
    {
        // The other half of the asymmetry: no inverter means no telemetry worth reporting, so this
        // must keep throwing rather than quietly returning a state full of zeroes.
        var reader = Reader(new UnreachableModbusClient(), new FakeModbusClient());

        await Assert.ThrowsAnyAsync<Exception>(() => reader.ReadAsync());
    }

    [Fact]
    public async Task Reads_the_charger_normally_once_it_becomes_reachable_again()
    {
        var evCharger = new FlakyModbusClient { Reachable = false };
        var reader = Reader(new FakeModbusClient(), evCharger);

        var duringOutage = await reader.ReadAsync();
        Assert.Equal(EvChargerStatus.Unknown, duringOutage.EvChargerStatus);

        evCharger.Reachable = true;
        evCharger.SetHolding(EvChargerRegisterMap.RunMode.Address, (ushort)EvChargerStatus.Charging);
        evCharger.SetHolding(EvChargerRegisterMap.ChargePowerTotal.Address, 7000);

        var afterRecovery = await reader.ReadAsync();

        Assert.Equal(EvChargerStatus.Charging, afterRecovery.EvChargerStatus);
        Assert.Equal(7000, afterRecovery.EvChargerPowerWatts);
    }

    [Fact]
    public async Task Reports_inverter_telemetry_when_an_http_charger_times_out()
    {
        // An HttpClient timeout is a TaskCanceledException, which the charger's best-effort catch used to
        // let through: every poll of an HTTP charger that did not answer lost its inverter reading (#429).
        var inverter = new FakeModbusClient();
        inverter.SetHolding(InverterRegisterMap.BatteryCapacity.Address, 47);
        using var http = new HttpClient(new NeverAnswersHandler())
        {
            BaseAddress = new Uri("http://charger.test/"),
            Timeout = TimeSpan.FromMilliseconds(50),
        };
        var reader = new EnergyStateReader(
            new SolaxInverterDriver(inverter), new HttpChargerDriver(http), NullLogger<EnergyStateReader>.Instance);

        var state = await reader.ReadAsync();

        Assert.Equal(47, state.BatterySocPercent);
        Assert.Equal(EvChargerStatus.Unknown, state.EvChargerStatus);
    }

    [Fact]
    public async Task Still_stops_the_poll_when_it_is_cancelled_during_the_charger_read()
    {
        // A shutdown arriving while the charger is being read is not the charger being unreachable.
        using var http = new HttpClient(new NeverAnswersHandler()) { BaseAddress = new Uri("http://charger.test/") };
        var reader = new EnergyStateReader(
            new SolaxInverterDriver(new FakeModbusClient()), new HttpChargerDriver(http), NullLogger<EnergyStateReader>.Instance);
        using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(shutdown.Token));
    }

    // The charger's two raw readings, decoded by its driver (#348) and composed here.
    [Fact]
    public async Task Maps_the_charger_run_mode_to_its_status()
    {
        var evCharger = new FakeModbusClient();
        evCharger.SetHolding(EvChargerRegisterMap.RunMode.Address, 2);

        var state = await Reader(new FakeModbusClient(), evCharger).ReadAsync();

        Assert.Equal(EvChargerStatus.Charging, state.EvChargerStatus);
    }

    [Fact]
    public async Task Reads_the_charger_power_as_unsigned()
    {
        var evCharger = new FakeModbusClient();
        evCharger.SetHolding(EvChargerRegisterMap.ChargePowerTotal.Address, 7000);

        var state = await Reader(new FakeModbusClient(), evCharger).ReadAsync();

        Assert.Equal(7000, state.EvChargerPowerWatts);
    }

    /// <summary>A charger whose web server takes the connection and never answers.</summary>
    private sealed class NeverAnswersHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>
    /// The read path of an HTTP charger (go-e, Shelly TopAC), reduced to its one request: what matters is
    /// what the HttpClient throws, not what the reply would decode to.
    /// </summary>
    private sealed class HttpChargerDriver(HttpClient http) : IEvChargerDriver
    {
        public EvChargerCapabilities Capabilities { get; } = new(6, 16, 0, 3, false, false);

        public async Task<EvChargerReading> ReadAsync(CancellationToken cancellationToken = default)
        {
            using var response = await http.GetAsync("status", cancellationToken);
            return new EvChargerReading(EvChargerStatus.Available, PowerWatts: 0, CurrentAmps: null, Mode: null);
        }

        public Task<EvChargerSettings> ReadSettingsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetCurrentAsync(int activeAmps, int targetAmps, string reason, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetChargingAllowedAsync(bool allowed, string reason, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<EvChargerProbeResult> ProbeAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>Nothing at the address at all: every operation fails, the connect included.</summary>
    private sealed class UnreachableModbusClient : IModbusClient
    {
        public bool IsConnected => false;

        public Task ConnectAsync(CancellationToken cancellationToken = default) =>
            throw new IOException("no route to host");

        public Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort numberOfPoints, CancellationToken cancellationToken = default) =>
            throw new IOException("no route to host");

        public Task<ushort[]> ReadInputRegistersAsync(ushort startAddress, ushort numberOfPoints, CancellationToken cancellationToken = default) =>
            throw new IOException("no route to host");

        public Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken cancellationToken = default) =>
            throw new IOException("no route to host");

        public Task WriteMultipleRegistersAsync(ushort startAddress, ushort[] values, CancellationToken cancellationToken = default) =>
            throw new IOException("no route to host");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Connects, then refuses every read -- a charger that drops its Modbus server.</summary>
    private sealed class ConnectsThenFailsModbusClient : IModbusClient
    {
        public bool IsConnected { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort numberOfPoints, CancellationToken cancellationToken = default) =>
            throw new IOException("connection reset");

        public Task<ushort[]> ReadInputRegistersAsync(ushort startAddress, ushort numberOfPoints, CancellationToken cancellationToken = default) =>
            throw new IOException("connection reset");

        public Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task WriteMultipleRegistersAsync(ushort startAddress, ushort[] values, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A <see cref="FakeModbusClient"/> whose reachability can be flipped mid-test.</summary>
    private sealed class FlakyModbusClient : FakeModbusClient
    {
        public bool Reachable { get; set; } = true;

        public override Task ConnectAsync(CancellationToken cancellationToken = default) =>
            Reachable ? base.ConnectAsync(cancellationToken) : throw new IOException("no route to host");
    }
}
