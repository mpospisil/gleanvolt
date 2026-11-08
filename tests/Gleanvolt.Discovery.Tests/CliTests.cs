using Gleanvolt.Core.Models;
using Gleanvolt.Discovery.Cli;

namespace Gleanvolt.Discovery.Tests;

public class CliTests
{
    [Fact]
    public async Task Discover_json_has_the_reports_shape_on_stdout_and_progress_on_stderr()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        // Nothing open anywhere: the scan ends at once with an empty report.
        var exit = await DiscoverCommand.RunAsync(
            ["--host", "192.168.1.10", "--json"],
            "gleanvolt discover",
            output,
            error,
            CancellationToken.None,
            options => new DeviceScanner(options, portProbe: new ClosedPorts()));

        Assert.Equal(0, exit);
        Assert.Contains("Scanning 1 host(s)", error.ToString(), StringComparison.Ordinal);

        using var report = System.Text.Json.JsonDocument.Parse(output.ToString());
        var root = report.RootElement;
        Assert.Equal(System.Text.Json.JsonValueKind.Array, root.GetProperty("devices").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, root.GetProperty("networks").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, root.GetProperty("warnings").ValueKind);
        Assert.False(root.GetProperty("cancelled").GetBoolean());
    }

    [Fact]
    public async Task Help_names_the_tool_as_it_was_started()
    {
        var output = new StringWriter();

        Assert.Equal(0, await DiscoverCommand.RunAsync(["--help"], "gleanvolt discover", output, TextWriter.Null, CancellationToken.None));
        Assert.Contains("Usage: gleanvolt discover [options]", output.ToString(), StringComparison.Ordinal);
    }

    private sealed class ClosedPorts : IPortProbe
    {
        public Task<bool> IsOpenAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private static DiscoveredDevice Device(PvDeviceRole role, string host, string model, SupportTier tier, byte unitId = 1) =>
        new(host, 502, unitId, DiscoveryFinding.SolaxDevice, role, model, "family", "SERIAL00000000", tier, true, "ok");

    [Fact]
    public void The_options_parse()
    {
        var (arguments, error) = CliArguments.Parse(
        [
            "--subnet", "192.168.1.0/24", "--subnet", "192.168.2.0/24", "--ports", "502,8899",
            "--unit-ids", "1,3", "--all-unit-ids", "--json", "--request-timeout", "500",
        ]);

        Assert.Null(error);
        Assert.NotNull(arguments);
        Assert.Equal(2, arguments.Subnets.Count);
        Assert.Equal([502, 8899], arguments.Options.Ports);
        Assert.Equal([(byte)1, (byte)3], arguments.Options.UnitIds);
        Assert.True(arguments.Options.AllUnitIds);
        Assert.True(arguments.Json);
        Assert.Equal(TimeSpan.FromMilliseconds(500), arguments.Options.RequestTimeout);
    }

    [Theory]
    [InlineData("--subnet", "192.168.1.0")]
    [InlineData("--ports", "502,70000")]
    [InlineData("--unit-ids", "1,256")]
    [InlineData("--connect-timeout", "0")]
    [InlineData("--frobnicate")]
    [InlineData("--host")]
    public void A_bad_option_is_an_error_not_a_scan(params string[] args)
    {
        var (arguments, error) = CliArguments.Parse(args);

        Assert.Null(arguments);
        Assert.NotNull(error);
    }

    [Fact]
    public void The_progress_line_names_the_role_model_and_serial()
    {
        Assert.Equal(
            "  found 192.168.2.6:502 charger, X3-HAC 11 kW, serial SERIAL00000000",
            ReportWriter.FoundLine(Device(PvDeviceRole.Charger, "192.168.2.6", "X3-HAC 11 kW", SupportTier.Verified)));
    }

    [Fact]
    public void A_progress_line_with_nothing_identified_says_what_answered()
    {
        Assert.Equal(
            "  found 192.168.2.30:8899 Open, no Modbus reply",
            ReportWriter.FoundLine(new DiscoveredDevice("192.168.2.30", 8899, null, DiscoveryFinding.NoModbusReply,
                null, null, null, null, null, null, "note")));
    }

    [Fact]
    public void The_configuration_to_paste_uses_the_environment_variable_names()
    {
        var lines = DiscoveryConfiguration.EnvironmentLines(
        [
            Device(PvDeviceRole.Inverter, "192.168.2.10", "X3-HYB-G4 PRO", SupportTier.Verified),
            Device(PvDeviceRole.Charger, "192.168.2.6", "X3-HAC 11 kW", SupportTier.Verified, unitId: 3),
        ]);

        Assert.Equal(
        [
            "Pv__Inverter__Model=SolaX X3-HYB-G4 PRO",
            "Pv__Inverter__Host=192.168.2.10",
            "Pv__Inverter__Port=502",
            "Pv__Inverter__UnitId=1",
            "Pv__Chargers__0__Model=SolaX X3-HAC 11 kW",
            "Pv__Chargers__0__Host=192.168.2.6",
            "Pv__Chargers__0__Port=502",
            "Pv__Chargers__0__UnitId=3",
        ], lines);
    }

    [Fact]
    public void A_huawei_behind_an_emma_brings_its_driver_and_topology_into_the_configuration()
    {
        var lines = DiscoveryConfiguration.EnvironmentLines(
        [
            new DiscoveredDevice("192.168.2.20", 502, 1, DiscoveryFinding.HuaweiDevice, PvDeviceRole.Inverter,
                "SUN2000-10K-MAP0", "SUN2000 hybrid", null, SupportTier.Expected, true, "note", Driver: "huawei", Topology: "emma"),
        ]);

        Assert.Equal(
        [
            "Pv__Inverter__Driver=huawei",
            "Pv__Inverter__Topology=emma",
            "Pv__Inverter__Model=Huawei SUN2000-10K-MAP0",
            "Pv__Inverter__Host=192.168.2.20",
            "Pv__Inverter__Port=502",
            "Pv__Inverter__UnitId=1",
        ], lines);
    }

    [Fact]
    public void A_device_Gleanvolt_cannot_drive_is_not_offered_as_configuration()
    {
        var lines = DiscoveryConfiguration.EnvironmentLines(
            [Device(PvDeviceRole.Inverter, "192.168.2.10", "X1-AC G3", SupportTier.NotSupported)]);

        Assert.Empty(lines);
    }

    [Fact]
    public void The_table_lists_every_result_and_the_configuration_under_it()
    {
        var report = new DiscoveryReport(
            ["192.168.2.0/24"],
            254,
            [
                Device(PvDeviceRole.Inverter, "192.168.2.10", "X3-HYB-G4 PRO", SupportTier.Verified),
                new DiscoveredDevice("192.168.2.30", 8899, null, DiscoveryFinding.NoModbusReply, null, null, null, null, null, null,
                    "Port open, no Modbus reply."),
            ],
            [],
            Cancelled: false);
        var output = new StringWriter();

        ReportWriter.WriteTable(report, output);

        var text = output.ToString();
        Assert.Contains("192.168.2.10", text);
        Assert.Contains("Verified", text);
        Assert.Contains("Open, no Modbus reply", text);
        Assert.Contains("Pv__Inverter__Host=192.168.2.10", text);
    }

    [Fact]
    public void The_json_is_the_library_records_with_enums_by_name()
    {
        var report = new DiscoveryReport(
            ["192.168.2.10"], 1, [Device(PvDeviceRole.Inverter, "192.168.2.10", "X3-HYB-G4 PRO", SupportTier.Verified)], [], false);

        var json = ReportWriter.ToJson(report);

        Assert.Contains("\"finding\": \"SolaxDevice\"", json);
        Assert.Contains("\"role\": \"Inverter\"", json);
        Assert.Contains("\"unitId\": 1", json);
    }
}
