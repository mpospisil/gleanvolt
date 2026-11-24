using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Enums;
using Gleanvolt.Devices;
using Gleanvolt.Devices.Solax;
using Gleanvolt.Modbus;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// The charger is optional hardware; the inverter is not. These pin that asymmetry: a charger that is
/// switched off, absent, or whose plugin isn't connected must not cost us the inverter telemetry, which
/// is the whole point of the poll.
/// </summary>
public class EnergyStateReaderTests
{
    private static EnergyStateReader Reader(IModbusClient inverter, IEvChargerDriver charger) =>
        new(new SolaxInverterDriver(inverter), charger, NullLogger<EnergyStateReader>.Instance);

    [Fact]
    public async Task Reports_inverter_telemetry_when_the_charger_cannot_be_read()
    {
        var inverter = new FakeModbusClient();
        inverter.SetHolding(InverterRegisterMap.BatteryCapacity.Address, 47);
        inverter.SetHolding(InverterRegisterMap.Powerdc1.Address, 1200);

        var state = await Reader(inverter, FakeCharger.Unreachable()).ReadAsync();

        // The inverter half survives intact -- the regression this guard exists for.
        Assert.Equal(47, state.BatterySocPercent);
        Assert.Equal(1200, state.SolarPowerWatts);
    }

    [Fact]
    public async Task Reports_the_charger_as_unknown_when_it_cannot_be_read()
    {
        var state = await Reader(new FakeModbusClient(), FakeCharger.Unreachable()).ReadAsync();

        Assert.Equal(EvChargerStatus.Unknown, state.EvChargerStatus);
        Assert.Equal(0, state.EvChargerPowerWatts);
        Assert.Null(state.ChargeMode);
        Assert.Null(state.ChargeCurrentAmps);
        Assert.NotNull(state.EvChargerReadError);
    }

    [Fact]
    public async Task Still_fails_the_poll_when_the_inverter_is_unreachable()
    {
        // The other half of the asymmetry: no inverter means no telemetry worth reporting, so this
        // must keep throwing rather than quietly returning a state full of zeroes.
        var reader = Reader(new UnreachableModbusClient(), new FakeCharger());

        await Assert.ThrowsAnyAsync<Exception>(() => reader.ReadAsync());
    }

    [Fact]
    public async Task Reads_the_charger_normally_once_it_can_be_read_again()
    {
        var charger = FakeCharger.Unreachable();
        var reader = Reader(new FakeModbusClient(), charger);

        var duringOutage = await reader.ReadAsync();
        Assert.Equal(EvChargerStatus.Unknown, duringOutage.EvChargerStatus);

        charger.Failure = null;
        charger.Reading = new EvChargerReading(EvChargerStatus.Charging, 7000, 10, null);

        var afterRecovery = await reader.ReadAsync();

        Assert.Equal(EvChargerStatus.Charging, afterRecovery.EvChargerStatus);
        Assert.Equal(7000, afterRecovery.EvChargerPowerWatts);
        Assert.Equal(10, afterRecovery.ChargeCurrentAmps);
        Assert.Null(afterRecovery.EvChargerReadError);
    }

    [Fact]
    public async Task Logs_a_charger_that_is_not_connected_yet_as_one_line_without_a_stack_trace()
    {
        // A plugin attaches seconds after start-up, so the first poll often finds none: that is a state,
        // and a warning with a stack trace on every start only alarms whoever reads the log.
        var logger = new CapturingLogger();
        var reader = new EnergyStateReader(
            new SolaxInverterDriver(new FakeModbusClient()),
            FakeCharger.Unreachable(() => new ChargerNotConnectedException("No charger plugin is connected to Gleanvolt.")),
            logger);

        var state = await reader.ReadAsync();

        Assert.Equal(EvChargerStatus.Unknown, state.EvChargerStatus);
        var entry = Assert.Single(logger.Entries, e => e.Message.Contains("not connected yet"));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Logs_an_unreadable_charger_as_a_warning_with_its_exception()
    {
        var logger = new CapturingLogger();
        var reader = new EnergyStateReader(
            new SolaxInverterDriver(new FakeModbusClient()), FakeCharger.Unreachable(), logger);

        await reader.ReadAsync();

        var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.IsType<IOException>(entry.Exception);
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

    private sealed class CapturingLogger : ILogger<EnergyStateReader>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
