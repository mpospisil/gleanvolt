using System.Diagnostics;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Infrastructure.Modbus;

namespace Gleanvolt.Discovery.Tests;

/// <summary>
/// The scan against a fake Modbus server on loopback, so what is tested is what goes over a socket:
/// the real <see cref="ModbusTcpClient"/>, real exception replies, real timeouts. Fakes of the client
/// itself only where the point is the scanner's own control flow (cancellation, the host timeout).
/// </summary>
public class DeviceScannerTests
{
    private const string Loopback = "127.0.0.1";

    // Short, so a test of "no reply" does not take the default two seconds per unit id.
    private static DiscoveryOptions Options(int port, params byte[] unitIds) => new()
    {
        Ports = [port],
        WebPorts = [],
        UnitIds = unitIds.Length > 0 ? unitIds : [1],
        ConnectTimeout = TimeSpan.FromMilliseconds(500),
        RequestTimeout = TimeSpan.FromMilliseconds(300),
        HostTimeout = TimeSpan.FromSeconds(20),
        MinRequestInterval = TimeSpan.FromMilliseconds(20),
    };

    private static Task<DiscoveryReport> ScanAsync(DiscoveryOptions options, CancellationToken cancellationToken = default) =>
        new DeviceScanner(options).ScanAsync(ScanPlan.ForHosts([Loopback]), cancellationToken: cancellationToken);

    [Fact]
    public async Task An_inverter_is_named_from_its_serial_and_confirmed_by_its_battery_register()
    {
        await using var server = new FakeModbusServer();
        server.Units[1] = FakeUnit.Inverter("10KXXXXXXXXXXX", soc: 54);

        var report = await ScanAsync(Options(server.Port));

        var device = Assert.Single(report.Devices);
        Assert.Equal(DiscoveryFinding.SolaxDevice, device.Finding);
        Assert.Equal(PvDeviceRole.Inverter, device.Role);
        Assert.Equal("X3-HYB-G4 PRO", device.Model);
        Assert.Equal(SupportTier.Verified, device.Tier);
        Assert.Equal((byte)1, device.UnitId);
        Assert.True(device.RoleConfirmed);
        Assert.Contains("54 %", device.Note);
    }

    [Fact]
    public async Task A_charger_is_found_by_its_own_serial_registers()
    {
        await using var server = new FakeModbusServer();
        server.Units[1] = FakeUnit.Charger("5030BXXXXXXXXX", runMode: 2);

        var device = Assert.Single((await ScanAsync(Options(server.Port))).Devices);

        Assert.Equal(PvDeviceRole.Charger, device.Role);
        Assert.Equal("X3-HAC 11 kW", device.Model);
        Assert.True(device.RoleConfirmed);
    }

    [Fact]
    public async Task A_serial_that_disagrees_with_the_role_check_is_reported_unconfirmed()
    {
        await using var server = new FakeModbusServer();
        server.Units[1] = FakeUnit.Inverter("10KXXXXXXXXXXX", soc: 4660);

        var device = Assert.Single((await ScanAsync(Options(server.Port))).Devices);

        Assert.False(device.RoleConfirmed);
        Assert.Contains("no state of charge", device.Note);
    }

    [Fact]
    public async Task A_device_with_no_SolaX_serial_answers_Modbus_but_is_not_a_SolaX()
    {
        await using var server = new FakeModbusServer();
        server.Units[1] = new FakeUnit().WithInput(0x1C, 7); // nothing at either serial address

        var device = Assert.Single((await ScanAsync(Options(server.Port))).Devices);

        Assert.Equal(DiscoveryFinding.NotSolax, device.Finding);
        Assert.Equal(SupportTier.NotSolax, device.Tier);
    }

    [Fact]
    public async Task An_open_port_that_never_replies_asks_whether_something_else_holds_the_connection()
    {
        await using var server = new FakeModbusServer { Silent = true };

        var device = Assert.Single((await ScanAsync(Options(server.Port, 1, 2))).Devices);

        Assert.Equal(DiscoveryFinding.NoModbusReply, device.Finding);
        Assert.Null(device.UnitId);
        Assert.Contains("Is Gleanvolt or Home Assistant connected to it right now?", device.Note);
    }

    [Fact]
    public async Task A_gateway_exception_means_the_unit_id_is_not_there_and_the_scan_moves_on()
    {
        await using var server = new FakeModbusServer { UnknownUnitException = 0x0B };
        server.Units[3] = FakeUnit.Charger("C311XXXXXXXXXX");

        var report = await ScanAsync(Options(server.Port, 1, 0, 2, 3, 4));

        var device = Assert.Single(report.Devices);
        Assert.Equal((byte)3, device.UnitId);
        Assert.Equal("X3-EVC 11 kW", device.Model);

        // Stopped at the first unit id that answered: nothing was asked of unit 4.
        Assert.DoesNotContain(server.Requests, request => request.UnitId == 4);
    }

    [Fact]
    public async Task A_gateway_with_nothing_behind_it_is_not_reported_as_a_device()
    {
        await using var server = new FakeModbusServer { UnknownUnitException = 0x0B };

        var device = Assert.Single((await ScanAsync(Options(server.Port, 1, 2))).Devices);

        Assert.Equal(DiscoveryFinding.NoUnitAnswered, device.Finding);
        Assert.Null(device.Tier);
    }

    [Fact]
    public async Task Two_devices_behind_one_bridge_are_two_results_when_every_unit_id_is_tried()
    {
        await using var server = new FakeModbusServer { UnknownUnitException = 0x0B };
        server.Units[1] = FakeUnit.Inverter("H34XXXXXXXXXXX");
        server.Units[3] = FakeUnit.Charger("5020BXXXXXXXXX");

        var report = await ScanAsync(Options(server.Port, 1, 2, 3) with { AllUnitIds = true });

        Assert.Collection(
            report.Devices,
            inverter => Assert.Equal((PvDeviceRole.Inverter, (byte?)1), (inverter.Role!.Value, inverter.UnitId)),
            charger => Assert.Equal((PvDeviceRole.Charger, (byte?)3), (charger.Role!.Value, charger.UnitId)));
    }

    [Fact]
    public async Task A_device_that_ignores_the_unit_id_is_reported_once()
    {
        await using var server = new FakeModbusServer();
        var unit = FakeUnit.Inverter("10KXXXXXXXXXXX");
        server.Units[1] = unit;
        server.Units[2] = unit;

        var report = await ScanAsync(Options(server.Port, 1, 2) with { AllUnitIds = true });

        Assert.Single(report.Devices);
    }

    [Fact]
    public async Task The_scan_holds_one_connection_at_a_time_and_closes_every_one()
    {
        await using var server = new FakeModbusServer { UnknownUnitException = 0x0B };
        server.Units[2] = FakeUnit.Inverter("10KXXXXXXXXXXX");

        await ScanAsync(Options(server.Port, 1, 0, 2) with { AllUnitIds = true });

        // The port probe, then one connection per unit id; never two open together.
        Assert.True(server.Connections >= 2);
        Assert.Equal(1, server.MaxOpenConnections);
        await WaitUntilAsync(() => server.OpenConnections == 0);
    }

    [Fact]
    public async Task Requests_to_one_host_are_spaced_across_unit_ids()
    {
        await using var server = new FakeModbusServer { UnknownUnitException = 0x0B };
        server.Units[2] = FakeUnit.Inverter("10KXXXXXXXXXXX");
        var interval = TimeSpan.FromMilliseconds(150);

        await ScanAsync(Options(server.Port, 1, 0, 2) with { MinRequestInterval = interval });

        var times = server.Requests.Select(request => request.Timestamp).ToList();
        Assert.True(times.Count >= 4);

        foreach (var (earlier, later) in times.Zip(times.Skip(1)))
        {
            // A little slack for the timer's resolution; back to back would be well under a millisecond.
            Assert.True(Stopwatch.GetElapsedTime(earlier, later) >= interval - TimeSpan.FromMilliseconds(20));
        }
    }

    [Fact]
    public async Task The_scan_never_writes()
    {
        await using var server = new FakeModbusServer { UnknownUnitException = 0x0B };
        server.Units[1] = FakeUnit.Inverter("10KXXXXXXXXXXX");
        server.Units[2] = FakeUnit.Charger("5030BXXXXXXXXX");

        await ScanAsync(Options(server.Port, 1, 2) with { AllUnitIds = true });

        Assert.Equal(0, server.Writes);
        Assert.All(server.Requests, request => Assert.Contains(request.Function, new byte[] { 0x03, 0x04 }));
    }

    [Fact]
    public async Task Every_client_the_scan_speaks_through_is_read_only_whatever_the_factory_returns()
    {
        var inner = new FakeClient();
        var scanner = new DeviceScanner(clientFactory: _ => inner);

        await using var client = scanner.OpenClient(Loopback, 502, 1);
        await client.WriteSingleRegisterAsync(0x628, 1600);
        await client.WriteMultipleRegistersAsync(0x7C, [1, 1]);

        Assert.IsType<ReadOnlyModbusClient>(client);
        Assert.Equal(0, inner.Writes);
    }

    [Fact]
    public async Task A_closed_port_costs_one_connect_and_no_Modbus_at_all()
    {
        var made = 0;
        var scanner = new DeviceScanner(
            new DiscoveryOptions { Ports = [502, 8899], WebPorts = [] },
            new FakePortProbe(),
            _ => { made++; return new FakeClient(); });

        var report = await scanner.ScanAsync(ScanPlan.ForHosts(["192.168.1.9"]));

        Assert.Empty(report.Devices);
        Assert.Equal(0, made);
    }

    [Fact]
    public async Task A_refused_Modbus_connection_is_not_retried_for_every_unit_id()
    {
        var made = 0;
        var scanner = new DeviceScanner(
            new DiscoveryOptions { Ports = [502], WebPorts = [], MinRequestInterval = TimeSpan.Zero },
            new FakePortProbe(("192.168.1.9", 502)),
            _ => { made++; return new FakeClient { RefuseConnect = true }; });

        var device = Assert.Single((await scanner.ScanAsync(ScanPlan.ForHosts(["192.168.1.9"]))).Devices);

        Assert.Equal(1, made);
        Assert.Equal(DiscoveryFinding.NoModbusReply, device.Finding);
        Assert.Contains("Is Gleanvolt or Home Assistant connected", device.Note);
    }

    [Fact]
    public async Task Cancelling_returns_what_was_found_so_far()
    {
        using var stop = new CancellationTokenSource();
        var scanner = new DeviceScanner(
            new DiscoveryOptions { Ports = [502], WebPorts = [], MinRequestInterval = TimeSpan.Zero, MaxConcurrentHosts = 2 },
            new FakePortProbe(("192.168.1.10", 502), ("192.168.1.11", 502)),
            device => device.Host == "192.168.1.10"
                ? new FakeClient { Serial = "10KXXXXXXXXXXX", Input = 50 }
                : new FakeClient { Hang = true });

        var report = await scanner.ScanAsync(
            ScanPlan.ForHosts(["192.168.1.10", "192.168.1.11"]),
            _ => stop.CancelAfter(TimeSpan.FromMilliseconds(100)),
            stop.Token);

        Assert.True(report.Cancelled);
        var device = Assert.Single(report.Devices);
        Assert.Equal("192.168.1.10", device.Host);
    }

    [Fact]
    public async Task A_host_that_runs_out_of_time_is_a_warning_not_a_failed_scan()
    {
        var scanner = new DeviceScanner(
            new DiscoveryOptions { Ports = [502], WebPorts = [], HostTimeout = TimeSpan.FromMilliseconds(200) },
            new FakePortProbe(("192.168.1.10", 502)),
            _ => new FakeClient { Hang = true });

        var report = await scanner.ScanAsync(ScanPlan.ForHosts(["192.168.1.10"]));

        Assert.False(report.Cancelled);
        Assert.Contains(report.Warnings, warning => warning.Contains("ran out of time", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("<title>SolaX Pocket WiFi</title>", true)]
    [InlineData("<html>chargeweb SolaX</html>", false)]
    [InlineData("<title>Router login</title>", false)]
    public async Task A_web_page_on_port_80_is_reported_only_when_it_is_a_SolaX_dongle(string page, bool reported)
    {
        var scanner = new DeviceScanner(
            new DiscoveryOptions { Ports = [80] },
            new FakePortProbe(("192.168.1.20", 80)),
            _ => throw new InvalidOperationException("A web port is not spoken Modbus to."),
            new FakeWebProbe(page));

        var report = await scanner.ScanAsync(ScanPlan.ForHosts(["192.168.1.20"]));

        Assert.Equal(reported, report.Devices.Any(device => device.Finding == DiscoveryFinding.SolaxDongle));
    }

    [Fact]
    public async Task Results_are_ordered_by_address_numerically()
    {
        var scanner = new DeviceScanner(
            new DiscoveryOptions { Ports = [502], WebPorts = [], MinRequestInterval = TimeSpan.Zero },
            new FakePortProbe(("192.168.1.10", 502), ("192.168.1.9", 502)),
            _ => new FakeClient { Serial = "10KXXXXXXXXXXX", Input = 50 });

        var report = await scanner.ScanAsync(ScanPlan.ForHosts(["192.168.1.10", "192.168.1.9"]));

        Assert.Equal(["192.168.1.9", "192.168.1.10"], report.Devices.Select(device => device.Host));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();

        while (!condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(5), "Timed out waiting for the connections to close.");
            await Task.Delay(20);
        }
    }

    private sealed class FakePortProbe(params (string Host, int Port)[] open) : IPortProbe
    {
        public Task<bool> IsOpenAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(open.Contains((host, port)));
    }

    private sealed class FakeWebProbe(string page) : IWebProbe
    {
        public Task<string?> ReadAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(page);
    }

    /// <summary>A device that answers its serial at every holding address and one value at every input register.</summary>
    private sealed class FakeClient : IModbusClient
    {
        public string? Serial { get; init; }

        public ushort Input { get; init; }

        public bool RefuseConnect { get; init; }

        public bool Hang { get; init; }

        public int Writes { get; private set; }

        public bool IsConnected => !RefuseConnect;

        public Task ConnectAsync(CancellationToken cancellationToken = default) =>
            RefuseConnect ? Task.FromException(new TimeoutException("Timed out connecting.")) : Task.CompletedTask;

        public async Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort numberOfPoints, CancellationToken cancellationToken = default)
        {
            await HangAsync(cancellationToken);
            var text = (Serial ?? string.Empty).PadRight(numberOfPoints * 2, '\0');
            return [.. Enumerable.Range(0, numberOfPoints).Select(i => (ushort)((text[i * 2] << 8) | text[i * 2 + 1]))];
        }

        public async Task<ushort[]> ReadInputRegistersAsync(ushort startAddress, ushort numberOfPoints, CancellationToken cancellationToken = default)
        {
            await HangAsync(cancellationToken);
            return [.. Enumerable.Repeat(Input, numberOfPoints)];
        }

        public Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken cancellationToken = default)
        {
            Writes++;
            return Task.CompletedTask;
        }

        public Task WriteMultipleRegistersAsync(ushort startAddress, ushort[] values, CancellationToken cancellationToken = default)
        {
            Writes++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private Task HangAsync(CancellationToken cancellationToken) =>
            Hang ? Task.Delay(Timeout.Infinite, cancellationToken) : Task.CompletedTask;
    }
}
