using Gleanvolt.Devices.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Gleanvolt.Devices.Huawei.Tests;

/// <summary>The SUN2000 driver's decoding and its handling of a busy SDongle (issue #320).</summary>
public class HuaweiSun2000DriverTests
{
    private static RegisterDump Night =>
        RegisterDump.FromJson(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "huawei-night-importing.json")));

    private static ushort[] Int32(int value) => [(ushort)((uint)value >> 16), (ushort)value];

    private static ushort[] Battery(ushort socTenths, int watts) => [socTenths, 0, 2, 4500, 0, .. Int32(watts)];

    [Fact]
    public void Decodes_high_word_first_and_turns_the_meter_to_import_positive()
    {
        var reading = HuaweiSun2000Driver.Decode(Int32(70_123), Int32(-2_500), Battery(955, -4_321));

        Assert.Equal(70_123, reading.PvWatts);
        Assert.Equal(2_500, reading.GridWatts);
        Assert.Equal(-4_321, reading.BatteryWatts);
        Assert.Equal(95.5, reading.BatterySocPercent, precision: 6);
        Assert.Null(reading.InverterOutputPerPhase);
    }

    [Fact]
    public void A_negative_input_power_at_night_is_no_production()
    {
        Assert.Equal(0, HuaweiSun2000Driver.Decode(Int32(-12), Int32(0), Battery(500, 0)).PvWatts);
    }

    [Fact]
    public void Reads_a_model_name_up_to_its_padding()
    {
        ushort[] registers = [('S' << 8) | 'U', ('N' << 8) | '2', ('0' << 8) | '0', ('0' << 8) | '-', ('8' << 8) | 'K', 0, 0];

        Assert.Equal("SUN2000-8K", HuaweiSun2000Driver.DecodeString(registers));
    }

    [Fact]
    public async Task A_busy_answer_is_waited_out_and_repeated()
    {
        var time = new FakeTimeProvider();
        var client = new BusyClient(new ReplayModbusClient(Night), busyAnswers: 2);
        var driver = new HuaweiSun2000Driver(client, time: time);

        var read = driver.ReadAsync();
        for (var step = 0; step < 10 && !read.IsCompleted; step++)
        {
            await Task.Yield();
            time.Advance(TimeSpan.FromSeconds(1));
        }

        var reading = await read;
        Assert.True(reading.GridWatts > 0);
        Assert.Equal(0, client.BusyLeft);
    }

    [Fact]
    public async Task A_device_that_stays_busy_fails_the_poll()
    {
        var time = new FakeTimeProvider();
        var client = new BusyClient(new ReplayModbusClient(Night), busyAnswers: int.MaxValue);
        var driver = new HuaweiSun2000Driver(client, time: time);

        var read = driver.ReadAsync();
        for (var step = 0; step < 10 && !read.IsCompleted; step++)
        {
            await Task.Yield();
            time.Advance(TimeSpan.FromSeconds(1));
        }

        var error = await Assert.ThrowsAsync<ModbusDeviceException>(() => read);
        Assert.True(error.IsBusy);
    }

    [Fact]
    public async Task Another_refusal_is_not_retried()
    {
        var client = new ReplayModbusClient(new RegisterDump());

        await Assert.ThrowsAsync<ModbusDeviceException>(() => new HuaweiSun2000Driver(client).ReadAsync());
        Assert.Single(client.Reads);
    }

    // Answers "busy" to the first few reads, then passes them through.
    private sealed class BusyClient(IModbusClient inner, int busyAnswers) : IModbusClient
    {
        public int BusyLeft { get; private set; } = busyAnswers;

        public bool IsConnected => inner.IsConnected;

        public Task ConnectAsync(CancellationToken cancellationToken = default) => inner.ConnectAsync(cancellationToken);

        public Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort numberOfPoints, CancellationToken cancellationToken = default)
        {
            if (BusyLeft > 0)
            {
                BusyLeft--;
                return Task.FromException<ushort[]>(new ModbusDeviceException(ModbusDeviceException.ServerDeviceBusy, "Busy."));
            }

            return inner.ReadHoldingRegistersAsync(startAddress, numberOfPoints, cancellationToken);
        }

        public Task<ushort[]> ReadInputRegistersAsync(ushort startAddress, ushort numberOfPoints, CancellationToken cancellationToken = default) =>
            inner.ReadInputRegistersAsync(startAddress, numberOfPoints, cancellationToken);

        public Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken cancellationToken = default) =>
            inner.WriteSingleRegisterAsync(address, value, cancellationToken);

        public Task WriteMultipleRegistersAsync(ushort startAddress, ushort[] values, CancellationToken cancellationToken = default) =>
            inner.WriteMultipleRegistersAsync(startAddress, values, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
