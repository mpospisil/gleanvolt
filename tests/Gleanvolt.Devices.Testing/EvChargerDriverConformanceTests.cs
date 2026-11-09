namespace Gleanvolt.Devices.Testing;

/// <summary>
/// What every EV charger driver reached over Modbus is held to (issue #350), as
/// <see cref="InverterDriverConformanceTests{TFactory}"/> is for inverters: the shared tests of
/// <see cref="ChargerConformanceTests{TFactory, TRecording}"/> on register dumps, and the snapshot's. A driver's
/// test project inherits this, names its factory and supplies recordings of a real charger in three states.
/// </summary>
/// <typeparam name="TFactory">The driver's factory.</typeparam>
public abstract class EvChargerDriverConformanceTests<TFactory> : ChargerConformanceTests<TFactory, RegisterDump>
    where TFactory : IEvChargerDriverFactory
{
    /// <summary>A driver from <see cref="ChargerConformanceTests{TFactory, TRecording}.CreateFactory"/> on <paramref name="client"/>.</summary>
    protected IEvChargerDriver CreateDriver(IModbusClient client, bool dryRun = false) =>
        CreateFactory().Create(EvChargerConnection.Over(client), Options with { DryRun = dryRun });

    /// <summary>Every recording of this family.</summary>
    protected IEnumerable<RegisterDump> Dumps => Recordings;

    protected override IChargerReplay Replay(RegisterDump recording) => new ModbusChargerReplay(new ReplayModbusClient(recording));

    protected override string Describe(RegisterDump recording) => DumpAssertions.Describe(recording);

    protected override bool IsRefusal(Exception exception) => exception is ModbusDeviceException;

    private RegisterSnapshotSpec Snapshot =>
        CreateFactory().Snapshot ?? throw new InvalidOperationException("A charger reached over Modbus names its snapshot ranges.");

    [Fact]
    public void The_family_is_reached_over_modbus_and_names_its_snapshot()
    {
        var factory = CreateFactory();

        Assert.Equal(EvChargerTransport.Modbus, factory.Transport);
        Assert.NotNull(factory.Snapshot);
        Assert.Null(factory.HttpSnapshot);
    }

    [Fact]
    public async Task The_snapshot_covers_every_register_the_driver_reads()
    {
        foreach (var dump in Dumps)
        {
            var client = new ReplayModbusClient(dump);
            var driver = CreateDriver(client);

            await driver.ReadAsync();
            await driver.ReadSettingsAsync();
            await driver.ProbeAsync();

            DumpAssertions.Covers(Snapshot, client.Reads);
        }
    }

    [Fact]
    public async Task A_snapshot_replays_the_driver_and_keeps_only_the_serial_prefix()
    {
        var factory = CreateFactory();
        var snapshot = await RegisterSnapshot.TakeAsync(
            new ReplayModbusClient(Charging), Snapshot, new RegisterDump { Driver = factory.Id });

        var original = CreateDriver(new ReplayModbusClient(Charging));
        var replayed = CreateDriver(new ReplayModbusClient(snapshot));

        Assert.Equal(await original.ReadAsync(), await replayed.ReadAsync());
        Assert.Equal(await original.ReadSettingsAsync(), await replayed.ReadSettingsAsync());
        DumpAssertions.SerialRedacted(Snapshot, snapshot);
    }

    [Fact]
    public void The_recordings_keep_only_the_serial_prefix()
    {
        foreach (var dump in Dumps.Append(OtherFamily))
        {
            DumpAssertions.SerialRedacted(Snapshot, dump);
        }
    }

    /// <summary>Loads a dump from a fixture file.</summary>
    protected static RegisterDump Load(string path) => RegisterDump.FromJson(File.ReadAllText(path));
}
