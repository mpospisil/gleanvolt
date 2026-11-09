namespace Gleanvolt.Devices.Testing;

/// <summary>
/// What every EV charger driver is held to (issue #350), as <see cref="InverterDriverConformanceTests{TFactory}"/>
/// is for inverters. A driver's test project inherits this, names its factory and supplies recordings of a real
/// charger in three states; every test here then runs against them.
///
/// <para><b>The recordings are the evidence.</b> Each is a Settings → charger → "Save register snapshot" of a
/// real installation in the state its name says: a driver cannot fake "a charging charger reads Charging and
/// positive power", because the charger said so. A family without recordings stays in the "Expected" tier.</para>
///
/// <para><b>Modbus only, for now.</b> The recordings are register dumps. A family reached over HTTP (go-e,
/// #352) records responses instead, and brings the second dump format with it.</para>
/// </summary>
/// <typeparam name="TFactory">The driver's factory.</typeparam>
public abstract class EvChargerDriverConformanceTests<TFactory>
    where TFactory : IEvChargerDriverFactory
{
    // A volt or two above any European grid's nominal, so a reading in the wrong unit (tenths of a watt,
    // kilowatts) cannot pass for a plausible one.
    private const double MaxPhaseVolts = 250;

    /// <summary>The factory under test.</summary>
    protected abstract TFactory CreateFactory();

    /// <summary>What every driver under test is created with; dry-run is added where a test needs it.</summary>
    protected virtual EvChargerDriverOptions Options => new();

    /// <summary>A driver from <see cref="CreateFactory"/> with <see cref="Options"/>, on <paramref name="client"/>.</summary>
    protected IEvChargerDriver CreateDriver(IModbusClient client, bool dryRun = false) =>
        CreateFactory().Create(EvChargerConnection.Over(client), Options with { DryRun = dryRun });

    /// <summary>No car plugged in.</summary>
    protected abstract RegisterDump Idle { get; }

    /// <summary>A car plugged in and waiting: connected, nothing flowing.</summary>
    protected abstract RegisterDump PluggedIn { get; }

    /// <summary>Charging a car.</summary>
    protected abstract RegisterDump Charging { get; }

    /// <summary>
    /// A device of another family at the charger's address: what the probe must not mistake for a charger.
    /// For a family that shares an installation with an inverter of the same brand, that inverter: a swapped
    /// pair of addresses is the likeliest mistake there is.
    /// </summary>
    protected abstract RegisterDump OtherFamily { get; }

    /// <summary>Every recording of this family; the range, capability and probe checks run on each.</summary>
    protected virtual IEnumerable<RegisterDump> Dumps => [Idle, PluggedIn, Charging];

    /// <summary>How far from zero a charger that is not delivering may read: its own standby. Watts.</summary>
    protected virtual double IdleToleranceWatts => 50;

    private EvChargerCapabilities Capabilities => CreateFactory().Capabilities;

    private RegisterSnapshotSpec Snapshot =>
        CreateFactory().Snapshot ?? throw new InvalidOperationException("A charger reached over Modbus names its snapshot ranges.");

    [Fact]
    public async Task An_idle_charger_reads_available_and_no_power()
    {
        var reading = await ReadAsync(Idle);

        Assert.Equal(EvChargerStatus.Available, reading.Status);
        Assert.InRange(reading.PowerWatts, 0, IdleToleranceWatts);
    }

    [Fact]
    public async Task A_car_plugged_in_and_waiting_reads_as_plugged_in_with_no_power()
    {
        var reading = await ReadAsync(PluggedIn);

        // Whichever of the plugged-in states the family reports, it is none that says "no car", "delivering"
        // or "could not tell".
        Assert.DoesNotContain(reading.Status, new[]
        {
            EvChargerStatus.Unknown, EvChargerStatus.Available, EvChargerStatus.Charging,
            EvChargerStatus.Faulted, EvChargerStatus.Unavailable,
        });
        Assert.InRange(reading.PowerWatts, 0, IdleToleranceWatts);
    }

    [Fact]
    public async Task A_charging_charger_reads_charging_and_positive_power()
    {
        var reading = await ReadAsync(Charging);

        Assert.Equal(EvChargerStatus.Charging, reading.Status);
        Assert.True(reading.PowerWatts > IdleToleranceWatts, $"A charging charger must read positive power; read {reading.PowerWatts} W.");
    }

    [Fact]
    public async Task Every_reading_is_in_range()
    {
        var capabilities = Capabilities;
        var maxWatts = capabilities.MaxCurrentAmps * capabilities.Phases * MaxPhaseVolts;

        foreach (var dump in Dumps)
        {
            var driver = CreateDriver(new ReplayModbusClient(dump));
            var reading = await driver.ReadAsync();
            var settings = await driver.ReadSettingsAsync();

            Assert.NotEqual(EvChargerStatus.Unknown, reading.Status);
            Assert.True(double.IsFinite(reading.PowerWatts), DumpAssertions.Describe(dump) + ": power is not finite.");
            Assert.InRange(reading.PowerWatts, 0, maxWatts);
            if (reading.CurrentAmps is { } amps)
            {
                Assert.InRange(amps, 0, capabilities.MaxCurrentAmps);
            }

            Assert.InRange(settings.ChargeCurrentAmps, 0, capabilities.MaxCurrentAmps);

            if (!capabilities.HasUseMode)
            {
                Assert.Null(reading.Mode);
            }
        }
    }

    [Fact]
    public void Capabilities_are_consistent()
    {
        var factory = CreateFactory();
        var capabilities = factory.Capabilities;

        Assert.Equal(capabilities, CreateDriver(new ReplayModbusClient(Idle)).Capabilities);
        Assert.InRange(capabilities.MinCurrentAmps, 6, capabilities.MaxCurrentAmps);  // IEC 61851's minimum
        Assert.InRange(capabilities.PauseCurrentAmps, 0, capabilities.MinCurrentAmps - 1);
        Assert.Contains(capabilities.Phases, new[] { 1, 3 });
        Assert.Equal(EvChargerTransport.Modbus, factory.Transport);
        Assert.NotNull(factory.Defaults);
        Assert.NotNull(factory.Snapshot);
    }

    [Fact]
    public async Task Reading_and_probing_write_nothing()
    {
        foreach (var dump in Dumps)
        {
            var client = new ReplayModbusClient(dump);
            var driver = CreateDriver(client);

            await driver.ReadAsync();
            await driver.ReadSettingsAsync();
            await driver.ProbeAsync();

            Assert.Empty(client.Writes);
        }
    }

    [Fact]
    public async Task The_setpoint_is_written_on_a_change_and_not_otherwise()
    {
        var capabilities = Capabilities;
        var client = new ReplayModbusClient(Charging);
        var driver = CreateDriver(client);

        await driver.SetCurrentAsync(capabilities.MinCurrentAmps, capabilities.MinCurrentAmps, "steady");
        Assert.Empty(client.Writes);

        await driver.SetCurrentAsync(capabilities.MinCurrentAmps, capabilities.MaxCurrentAmps, "up");
        Assert.NotEmpty(client.Writes);
    }

    [Fact]
    public async Task The_setpoint_stays_within_the_capabilities()
    {
        // Read back in dry-run, where the value the driver would have written stands in for the device's:
        // the one place a generic test can see what a driver wrote in the family's own encoding.
        var capabilities = Capabilities;
        var driver = CreateDriver(new ReplayModbusClient(Charging), dryRun: true);

        await driver.SetCurrentAsync(capabilities.MinCurrentAmps, capabilities.MaxCurrentAmps + 10, "too high");
        Assert.Equal(capabilities.MaxCurrentAmps, (await driver.ReadSettingsAsync()).ChargeCurrentAmps);

        await driver.SetCurrentAsync(capabilities.MaxCurrentAmps, -5, "below zero");
        Assert.Equal(0, (await driver.ReadSettingsAsync()).ChargeCurrentAmps);
    }

    [Fact]
    public async Task Start_and_stop_each_write_and_write_different_things()
    {
        // Unconditional by contract (#348): "stop" has to stop whatever the device already said, so the
        // caller writes on a transition and the driver writes whenever asked.
        var client = new ReplayModbusClient(Charging);
        var driver = CreateDriver(client);

        await driver.SetChargingAllowedAsync(true, "start");
        var start = client.Writes.ToList();
        await driver.SetChargingAllowedAsync(false, "stop");
        var stop = client.Writes.Skip(start.Count).ToList();

        Assert.NotEmpty(start);
        Assert.NotEmpty(stop);
        Assert.NotEqual(Describe(start), Describe(stop));
    }

    [Fact]
    public async Task Start_and_stop_read_back_and_leave_the_setpoint_alone()
    {
        var capabilities = Capabilities;
        var driver = CreateDriver(new ReplayModbusClient(PluggedIn), dryRun: true);
        var amps = capabilities.MinCurrentAmps;

        await driver.SetCurrentAsync(capabilities.MaxCurrentAmps, amps, "setpoint");

        await driver.SetChargingAllowedAsync(true, "start");
        var started = await driver.ReadSettingsAsync();
        Assert.True(started.ChargingAllowed, "After a start, charging must read as allowed.");
        Assert.Equal(amps, started.ChargeCurrentAmps);

        await driver.SetChargingAllowedAsync(false, "stop");
        var stopped = await driver.ReadSettingsAsync();
        Assert.False(stopped.ChargingAllowed, "After a stop, charging must read as not allowed.");
        Assert.Equal(amps, stopped.ChargeCurrentAmps);
    }

    [Fact]
    public async Task In_dry_run_nothing_is_written()
    {
        var capabilities = Capabilities;
        var client = new ReplayModbusClient(Charging);
        var driver = CreateDriver(client, dryRun: true);

        await driver.SetChargingAllowedAsync(true, "start");
        await driver.SetCurrentAsync(capabilities.MinCurrentAmps, capabilities.MaxCurrentAmps, "up");
        await driver.SetCurrentAsync(capabilities.MaxCurrentAmps, capabilities.PauseCurrentAmps, "pause");
        await driver.SetChargingAllowedAsync(false, "stop");

        Assert.Empty(client.Writes);
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
            Assert.False((await driver.ProbeAsync()).Recognised, DumpAssertions.Describe(OtherFamily) + " was mistaken for this family's charger.");
        }
        catch (ModbusDeviceException)
        {
        }
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

    private async Task<EvChargerReading> ReadAsync(RegisterDump dump) =>
        await CreateDriver(new ReplayModbusClient(dump)).ReadAsync();

    private static string Describe(IEnumerable<RegisterWrite> writes) =>
        string.Join("; ", writes.Select(write => $"{write.UnitId}:0x{write.Start:X4}={string.Join(',', write.Values)}"));
}
