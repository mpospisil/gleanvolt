namespace Gleanvolt.Devices.Testing;

/// <summary>
/// What every inverter driver is held to, built-in or third-party (issue #319). A driver's test project
/// inherits this, names its factory and supplies recordings of real devices; every test here then runs
/// against them.
///
/// <para><b>The recordings are the evidence.</b> Each dump is a Settings → "Save register snapshot" of a
/// real installation in a known state: the sign checks cannot be faked by a driver, because the device
/// said which way the power flowed. A family without recordings has no business above the "Expected" tier.</para>
/// </summary>
/// <typeparam name="TFactory">The driver's factory.</typeparam>
public abstract class InverterDriverConformanceTests<TFactory>
    where TFactory : IInverterDriverFactory
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The factory under test.</summary>
    protected abstract TFactory CreateFactory();

    /// <summary>
    /// What every driver under test is created with: the defaults, unless the suite covers one topology of a
    /// family reached more than one way (#322). Dry-run is added where a test needs it.
    /// </summary>
    protected virtual InverterDriverOptions Options => new();

    /// <summary>A driver from <see cref="CreateFactory"/> with <see cref="Options"/>, on <paramref name="client"/>.</summary>
    protected IInverterDriver CreateDriver(IModbusClient client, bool dryRun = false) =>
        CreateFactory().Create(client, Options with { DryRun = dryRun });

    /// <summary>At night, battery idle, the house importing from the grid.</summary>
    protected abstract RegisterDump NightImporting { get; }

    /// <summary>The battery charging.</summary>
    protected abstract RegisterDump BatteryCharging { get; }

    /// <summary>A device of another family at the address: what the probe must not mistake for this one.</summary>
    protected abstract RegisterDump OtherFamily { get; }

    /// <summary>Every recording of this family; the range and capability checks run on each.</summary>
    protected virtual IEnumerable<RegisterDump> Dumps => [NightImporting, BatteryCharging];

    /// <summary>
    /// How far from zero "idle" and "no sun" may read: a battery at rest still trickles, and an inverter
    /// at night reports its own standby. Watts.
    /// </summary>
    protected virtual double IdleToleranceWatts => 100;

    [Fact]
    public async Task Night_import_reads_as_positive_grid_with_no_sun_and_the_battery_idle()
    {
        var reading = await ReadAsync(NightImporting);

        Assert.True(reading.GridWatts > 0, $"Importing at night must read as positive grid; read {reading.GridWatts} W.");
        Assert.InRange(reading.PvWatts, 0, IdleToleranceWatts);
        Assert.InRange(reading.BatteryWatts, -IdleToleranceWatts, IdleToleranceWatts);
    }

    [Fact]
    public async Task A_charging_battery_reads_as_positive_battery()
    {
        var reading = await ReadAsync(BatteryCharging);

        Assert.True(reading.BatteryWatts > IdleToleranceWatts, $"A charging battery must read as positive; read {reading.BatteryWatts} W.");
    }

    [Fact]
    public async Task Every_reading_is_in_range()
    {
        foreach (var dump in Dumps)
        {
            var reading = await ReadAsync(dump);

            Assert.InRange(reading.BatterySocPercent, 0, 100);
            Assert.True(double.IsFinite(reading.PvWatts), DumpAssertions.Describe(dump) + ": PV is not finite.");
            Assert.True(double.IsFinite(reading.GridWatts), DumpAssertions.Describe(dump) + ": grid is not finite.");
            Assert.True(double.IsFinite(reading.BatteryWatts), DumpAssertions.Describe(dump) + ": battery is not finite.");
            Assert.True(reading.PvWatts >= 0, DumpAssertions.Describe(dump) + ": PV cannot be negative.");

            if (reading.InverterOutputPerPhase is { } phases)
            {
                Assert.True(double.IsFinite(phases.L1) && double.IsFinite(phases.L2) && double.IsFinite(phases.L3), DumpAssertions.Describe(dump) + ": a phase is not finite.");
            }
        }
    }

    [Fact]
    public async Task Capabilities_match_behaviour()
    {
        var factory = CreateFactory();

        foreach (var dump in Dumps)
        {
            var driver = factory.Create(new ReplayModbusClient(dump), Options);
            var reading = await driver.ReadAsync();

            Assert.Equal(factory.Capabilities, driver.Capabilities);
            Assert.Equal(driver.Capabilities.HasBatteryHold, driver.BatteryHold is not null);
            Assert.Equal(driver.Capabilities.HasPhaseOutput, reading.InverterOutputPerPhase is not null);
        }
    }

    [Fact]
    public async Task Reading_and_probing_write_nothing()
    {
        foreach (var dump in Dumps)
        {
            var client = new ReplayModbusClient(dump);
            var driver = CreateDriver(client);

            await driver.ReadAsync();
            await driver.ProbeAsync();

            Assert.Empty(client.Writes);
        }
    }

    [Fact]
    public async Task In_dry_run_holding_and_releasing_write_nothing()
    {
        var client = new ReplayModbusClient(NightImporting);
        var driver = CreateDriver(client, dryRun: true);
        if (driver.BatteryHold is not { } hold)
        {
            return;
        }

        await hold.ApplyAsync(hold: true, activePowerTargetWatts: 0, T0);
        await hold.ApplyAsync(hold: false, activePowerTargetWatts: 0, T0.AddSeconds(5));

        Assert.Empty(client.Writes);
    }

    [Fact]
    public async Task The_hold_writes_to_arm_and_to_release_and_nothing_in_a_steady_state()
    {
        var client = new ReplayModbusClient(NightImporting);
        var driver = CreateDriver(client);
        if (driver.BatteryHold is not { } hold)
        {
            return;
        }

        var released = await hold.ApplyAsync(hold: false, activePowerTargetWatts: 0, T0);
        Assert.Empty(client.Writes);
        Assert.False(released.Wrote);

        var armed = await hold.ApplyAsync(hold: true, activePowerTargetWatts: 0, T0);
        Assert.True(armed.Held);
        Assert.True(armed.Wrote);
        var afterArm = client.Writes.Count;
        Assert.True(afterArm > 0, "Arming the hold must write.");

        var steady = await hold.ApplyAsync(hold: true, activePowerTargetWatts: 0, T0.AddSeconds(1));
        Assert.False(steady.Wrote);
        Assert.Equal(afterArm, client.Writes.Count);

        var release = await hold.ApplyAsync(hold: false, activePowerTargetWatts: 0, T0.AddSeconds(2));
        Assert.False(release.Held);
        Assert.True(release.Wrote);
        Assert.True(client.Writes.Count > afterArm, "Releasing a hold must write the release.");

        var afterRelease = client.Writes.Count;
        await hold.ApplyAsync(hold: false, activePowerTargetWatts: 0, T0.AddSeconds(3));
        Assert.Equal(afterRelease, client.Writes.Count);
    }

    [Fact]
    public async Task The_probe_recognises_this_family()
    {
        foreach (var dump in Dumps)
        {
            var probe = await CreateDriver(new ReplayModbusClient(dump)).ProbeAsync();

            Assert.True(probe.Recognised, DumpAssertions.Describe(dump) + " was not recognised by its own driver's probe.");
        }
    }

    [Fact]
    public async Task The_probe_does_not_recognise_another_family()
    {
        var driver = CreateDriver(new ReplayModbusClient(OtherFamily));

        // Either answer is a refusal: a reading this family would not report, or a device that keeps
        // nothing at the address the family is known by.
        try
        {
            Assert.False((await driver.ProbeAsync()).Recognised, DumpAssertions.Describe(OtherFamily) + " was mistaken for this family.");
        }
        catch (ModbusDeviceException)
        {
        }
    }

    [Fact]
    public async Task The_snapshot_covers_every_register_the_driver_reads()
    {
        var factory = CreateFactory();
        var client = new ReplayModbusClient(NightImporting);
        var driver = factory.Create(client, Options);

        await driver.ReadAsync();
        await driver.ProbeAsync();

        DumpAssertions.Covers(factory.SnapshotFor(Options), client.Reads);
    }

    [Fact]
    public async Task A_snapshot_replays_the_driver_and_keeps_only_the_serial_prefix()
    {
        var factory = CreateFactory();
        var snapshot = await RegisterSnapshot.TakeAsync(
            new ReplayModbusClient(NightImporting), factory.SnapshotFor(Options), new RegisterDump { Driver = factory.Id });

        var original = await ReadAsync(NightImporting);
        var replayed = await factory.Create(new ReplayModbusClient(snapshot), Options).ReadAsync();

        Assert.Equal(original, replayed);
        DumpAssertions.SerialRedacted(factory.SnapshotFor(Options), snapshot);
    }

    [Fact]
    public void The_recordings_keep_only_the_serial_prefix()
    {
        // The fixtures are committed: a serial number in one is an installation's identity in a public
        // repository.
        foreach (var dump in Dumps.Append(OtherFamily))
        {
            DumpAssertions.SerialRedacted(CreateFactory().SnapshotFor(Options), dump);
        }
    }

    /// <summary>Loads a dump from a fixture file.</summary>
    protected static RegisterDump Load(string path) => RegisterDump.FromJson(File.ReadAllText(path));

    private async Task<InverterReading> ReadAsync(RegisterDump dump) =>
        await CreateDriver(new ReplayModbusClient(dump)).ReadAsync();
}
