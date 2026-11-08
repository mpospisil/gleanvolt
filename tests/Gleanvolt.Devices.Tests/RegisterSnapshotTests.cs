using Gleanvolt.Devices.Testing;

namespace Gleanvolt.Devices.Tests;

/// <summary>
/// The register snapshot and the replay that answers from it (issue #319): the file round-trips, a refused
/// address stays refused, the serial number leaves the house as a prefix only, and reads stay within what
/// one Modbus request may ask for.
/// </summary>
public class RegisterSnapshotTests
{
    private static RegisterDump Device(Dictionary<ushort, ushort>? holding = null, Dictionary<ushort, ushort>? input = null) => new()
    {
        Driver = "solax",
        Holding = holding ?? [],
        Input = input ?? [],
    };

    private static Dictionary<ushort, ushort> Serial(string text)
    {
        text = text.PadRight(14, '\0');
        return Enumerable.Range(0, 7).ToDictionary(i => (ushort)i, i => (ushort)((text[i * 2] << 8) | text[i * 2 + 1]));
    }

    private static string SerialOf(RegisterDump dump) =>
        new([.. Enumerable.Range(0, 7).SelectMany(i => new[] { (char)(dump.Holding[(ushort)i] >> 8), (char)(dump.Holding[(ushort)i] & 0xFF) })]);

    [Fact]
    public void A_dump_round_trips_through_its_file()
    {
        var dump = new RegisterDump
        {
            Driver = "solax",
            Family = "SolaX X3-HYB-G4 PRO",
            GleanvoltVersion = "1.2.40",
            CapturedAt = new DateTimeOffset(2026, 11, 8, 21, 30, 0, TimeSpan.FromHours(1)),
            Note = "night, importing",
            Holding = new Dictionary<ushort, ushort> { [0x0000] = 0x3130 },
            Input = new Dictionary<ushort, ushort> { [0x001C] = 57, [0x0046] = 0xFCAE },
        };

        var json = dump.ToJson();
        var read = RegisterDump.FromJson(json);

        Assert.Contains("\"0x001C\": 57", json);
        Assert.Equal(dump.Driver, read.Driver);
        Assert.Equal(dump.Family, read.Family);
        Assert.Equal(dump.CapturedAt, read.CapturedAt);
        Assert.Equal(dump.Note, read.Note);
        Assert.Equal(dump.Holding, read.Holding);
        Assert.Equal(dump.Input, read.Input);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"format":"something-else","formatVersion":1}""")]
    [InlineData("""{"format":"gleanvolt-register-dump","formatVersion":2}""")]
    [InlineData("""{"format":"gleanvolt-register-dump","formatVersion":1,"input":{"0x0001":70000}}""")]
    [InlineData("""{"format":"gleanvolt-register-dump","formatVersion":1,"input":{"zero":1}}""")]
    [InlineData("not json")]
    public void A_file_that_is_not_a_dump_is_refused(string json)
    {
        Assert.Throws<FormatException>(() => RegisterDump.FromJson(json));
    }

    [Fact]
    public async Task The_replay_refuses_an_address_the_device_did_not_have_and_records_writes()
    {
        var client = new ReplayModbusClient(Device(input: new() { [0] = 1, [1] = 2 }));

        Assert.Equal(new ushort[] { 1, 2 }, await client.ReadInputRegistersAsync(0, 2));
        var refused = await Assert.ThrowsAsync<ModbusDeviceException>(() => client.ReadInputRegistersAsync(1, 2));
        Assert.True(refused.IsIllegalAddress);
        await Assert.ThrowsAsync<ModbusDeviceException>(() => client.ReadHoldingRegistersAsync(0, 1));

        await client.WriteMultipleRegistersAsync(0x7C, [1, 2, 3]);
        await client.WriteSingleRegisterAsync(0x10, 9);

        Assert.Equal([new RegisterWrite(0x7C, [1, 2, 3]), new RegisterWrite(0x10, [9])], client.Writes, RegisterWriteComparer.Instance);
        // A command is not read back: the device's register is what it was.
        Assert.Equal(new ushort[] { 1 }, await client.ReadInputRegistersAsync(0, 1));
    }

    [Fact]
    public async Task A_snapshot_reads_the_ranges_and_leaves_out_what_the_device_refuses()
    {
        var input = Enumerable.Range(0, 10).Where(address => address != 4).ToDictionary(address => (ushort)address, address => (ushort)(address * 10));
        var device = new ReplayModbusClient(Device(input: input));
        var spec = new RegisterSnapshotSpec([new RegisterRange(RegisterTable.Input, 0, 10)]);

        var dump = await RegisterSnapshot.TakeAsync(device, spec, new RegisterDump { Driver = "solax", Note = "test" });

        Assert.Equal(input, dump.Input);
        Assert.Equal("solax", dump.Driver);
        Assert.Equal("test", dump.Note);
        Assert.True(device.IsConnected);
        Assert.Empty(device.Writes);
    }

    [Fact]
    public async Task A_snapshot_never_asks_for_more_than_one_request_may_carry_and_reads_overlaps_once()
    {
        var input = Enumerable.Range(0, 300).ToDictionary(address => (ushort)address, address => (ushort)address);
        var device = new ReplayModbusClient(Device(input: input));
        var spec = new RegisterSnapshotSpec(
        [
            new RegisterRange(RegisterTable.Input, 0, 200),
            new RegisterRange(RegisterTable.Input, 150, 150),
        ]);

        var dump = await RegisterSnapshot.TakeAsync(device, spec, new RegisterDump());

        Assert.Equal(300, dump.Input.Count);
        Assert.All(device.Reads, read => Assert.InRange(read.Count, 1, RegisterSnapshot.MaxRegistersPerRead));
        Assert.Equal(300, device.Reads.Sum(read => read.Count));
    }

    [Fact]
    public async Task The_serial_number_keeps_only_its_prefix()
    {
        var device = new ReplayModbusClient(Device(holding: Serial("H34A12345678")));
        var serial = new RegisterRange(RegisterTable.Holding, 0, 7);
        var spec = new RegisterSnapshotSpec([serial], serial, SerialPrefixLength: 4);

        var dump = await RegisterSnapshot.TakeAsync(device, spec, new RegisterDump());

        Assert.Equal("H34A********\0\0", SerialOf(dump));
    }

    [Fact]
    public async Task An_odd_prefix_length_splits_a_register()
    {
        var device = new ReplayModbusClient(Device(holding: Serial("10K3456789ABCD")));
        var serial = new RegisterRange(RegisterTable.Holding, 0, 7);

        var dump = await RegisterSnapshot.TakeAsync(device, new RegisterSnapshotSpec([serial], serial, 3), new RegisterDump());

        Assert.Equal("10K***********", SerialOf(dump));
    }

    [Fact]
    public async Task Anything_but_a_device_refusal_fails_the_snapshot()
    {
        var spec = new RegisterSnapshotSpec([new RegisterRange(RegisterTable.Input, 0, 2)]);

        await Assert.ThrowsAsync<IOException>(() => RegisterSnapshot.TakeAsync(new DeadClient(), spec, new RegisterDump()));
    }

    [Fact]
    public async Task A_range_on_another_unit_is_read_on_that_unit_and_kept_apart()
    {
        // An inverter behind Huawei's EMMA (#322): the inverter on the client's unit, the EMMA on unit 0,
        // both at addresses that could overlap.
        var device = new ReplayModbusClient(Device(holding: new() { [100] = 1, [101] = 2 }) with
        {
            Units = new Dictionary<byte, RegisterDump> { [0] = Device(holding: new() { [100] = 7, [101] = 8 }) },
        });
        var spec = new RegisterSnapshotSpec(
        [
            new RegisterRange(RegisterTable.Holding, 100, 2),
            new RegisterRange(RegisterTable.Holding, 100, 2, UnitId: 0),
        ]);

        var dump = await RegisterSnapshot.TakeAsync(device, spec, new RegisterDump());

        Assert.Equal([1, 2], dump.Holding.Values);
        Assert.Equal([7, 8], dump.Units[0].Holding.Values);
        Assert.Equal([null, (byte?)0], device.Reads.Select(read => read.UnitId));
    }

    [Fact]
    public void Another_unit_survives_the_file()
    {
        var dump = Device(input: new() { [1] = 5 }) with
        {
            Units = new Dictionary<byte, RegisterDump> { [0] = Device(holding: new() { [31657] = 65535 }) },
        };

        var read = RegisterDump.FromJson(dump.ToJson());

        Assert.Equal(5, read.Input[1]);
        Assert.Equal(65535, read.Units[0].Holding[31657]);
        Assert.Empty(RegisterDump.FromJson(Device().ToJson()).Units);
        Assert.DoesNotContain("units", Device().ToJson());
    }

    [Fact]
    public async Task A_unit_the_recording_lacks_refuses_every_address()
    {
        var device = new ReplayModbusClient(Device(holding: new() { [0] = 1 }));

        var error = await Assert.ThrowsAsync<ModbusDeviceException>(() => device.ForUnit(5).ReadHoldingRegistersAsync(0, 1));

        Assert.True(error.IsIllegalAddress);
    }

    private sealed class RegisterWriteComparer : IEqualityComparer<RegisterWrite>
    {
        public static readonly RegisterWriteComparer Instance = new();

        public bool Equals(RegisterWrite? x, RegisterWrite? y) =>
            x is not null && y is not null && x.Start == y.Start && x.Values.SequenceEqual(y.Values);

        public int GetHashCode(RegisterWrite obj) => obj.Start;
    }

    private sealed class DeadClient : IModbusClient
    {
        public bool IsConnected => true;

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort numberOfPoints, CancellationToken cancellationToken = default) =>
            throw new IOException("connection reset");

        public Task<ushort[]> ReadInputRegistersAsync(ushort startAddress, ushort numberOfPoints, CancellationToken cancellationToken = default) =>
            throw new IOException("connection reset");

        public Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WriteMultipleRegistersAsync(ushort startAddress, ushort[] values, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
