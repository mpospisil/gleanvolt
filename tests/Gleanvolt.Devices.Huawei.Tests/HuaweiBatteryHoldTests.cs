using Gleanvolt.Devices.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gleanvolt.Devices.Huawei.Tests;

/// <summary>
/// The SUN2000's battery hold (issue #323): the maximum discharging power is a lasting setting, so every
/// path that puts the original back -- release, a normal stop, the first poll after a crash -- is tested,
/// and so is every way the hold refuses to write.
/// </summary>
public class HuaweiBatteryHoldTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    // The inverter with its maximum discharging power reading the given watts.
    private static ReplayModbusClient Inverter(uint maxDischargeWatts) => new(new RegisterDump
    {
        Holding = new Dictionary<ushort, ushort>
        {
            [HuaweiRegisterMap.MaxDischargePower] = (ushort)(maxDischargeWatts >> 16),
            [HuaweiRegisterMap.MaxDischargePower + 1] = (ushort)maxDischargeWatts,
        },
    });

    private static HuaweiBatteryHold Hold(IModbusClient client, IDriverStateStore state, bool dryRun = false) =>
        new(client, state, NullLogger.Instance, dryRun);

    private static RegisterWrite MaxDischarge(uint watts) =>
        new(HuaweiRegisterMap.MaxDischargePower, [(ushort)(watts >> 16), (ushort)watts]);

    // RegisterWrite's values are a list, which a record compares by reference.
    private static void AssertWrites(ReplayModbusClient client, params RegisterWrite[] expected) =>
        Assert.Equal(
            expected.Select(write => (write.Start, string.Join(",", write.Values), write.UnitId)),
            client.Writes.Select(write => (write.Start, string.Join(",", write.Values), write.UnitId)));

    [Fact]
    public async Task Holding_stores_the_original_before_writing_zero_and_releasing_writes_it_back()
    {
        var client = Inverter(5_000);
        var state = new InMemoryDriverStateStore();
        var hold = Hold(client, state);

        var armed = await hold.ApplyAsync(hold: true, 0, T0);

        Assert.True(armed is { Held: true, Wrote: true });
        Assert.Equal("5000", state.Get(HuaweiBatteryHold.OriginalKey));
        AssertWrites(client, MaxDischarge(0));

        var released = await hold.ApplyAsync(hold: false, 0, T0.AddMinutes(5));

        Assert.True(released is { Held: false, Wrote: true });
        AssertWrites(client, MaxDischarge(0), MaxDischarge(5_000));
        Assert.Null(state.Get(HuaweiBatteryHold.OriginalKey));
    }

    [Fact]
    public async Task An_original_that_cannot_be_stored_means_no_write()
    {
        var client = Inverter(5_000);

        await Assert.ThrowsAsync<IOException>(() => Hold(client, new FailingStore()).ApplyAsync(hold: true, 0, T0));

        Assert.Empty(client.Writes);
    }

    [Fact]
    public async Task A_normal_stop_releases_a_hold()
    {
        var client = Inverter(5_000);
        var state = new InMemoryDriverStateStore();
        var hold = Hold(client, state);
        await hold.ApplyAsync(hold: true, 0, T0);

        await hold.ReleaseOnShutdownAsync();

        AssertWrites(client, MaxDischarge(0), MaxDischarge(5_000));
        Assert.Null(state.Get(HuaweiBatteryHold.OriginalKey));
    }

    [Fact]
    public async Task A_normal_stop_without_a_hold_writes_nothing()
    {
        var client = Inverter(5_000);

        await Hold(client, new InMemoryDriverStateStore()).ReleaseOnShutdownAsync();

        Assert.Empty(client.Writes);
        Assert.Empty(client.Reads);
    }

    [Fact]
    public async Task After_a_crash_the_first_poll_restores_the_original_when_no_hold_is_due()
    {
        // The last run wrote 0 and died: the register reads 0, the original is on disk.
        var client = Inverter(0);
        var state = new InMemoryDriverStateStore();
        state.Set(HuaweiBatteryHold.OriginalKey, "4200");

        var result = await Hold(client, state).ApplyAsync(hold: false, 0, T0);

        Assert.False(result.Held);
        AssertWrites(client, MaxDischarge(4_200));
        Assert.Null(state.Get(HuaweiBatteryHold.OriginalKey));
    }

    [Fact]
    public async Task A_normal_stop_before_the_first_poll_after_a_crash_restores_too()
    {
        var client = Inverter(0);
        var state = new InMemoryDriverStateStore();
        state.Set(HuaweiBatteryHold.OriginalKey, "4200");

        await Hold(client, state).ReleaseOnShutdownAsync();

        AssertWrites(client, MaxDischarge(4_200));
        Assert.Null(state.Get(HuaweiBatteryHold.OriginalKey));
    }

    [Fact]
    public async Task After_a_crash_a_hold_still_due_carries_on_without_a_write()
    {
        var client = Inverter(0);
        var state = new InMemoryDriverStateStore();
        state.Set(HuaweiBatteryHold.OriginalKey, "4200");
        var hold = Hold(client, state);

        var result = await hold.ApplyAsync(hold: true, 0, T0);

        Assert.True(result is { Held: true, Wrote: false });
        Assert.Empty(client.Writes);
        Assert.Equal("4200", state.Get(HuaweiBatteryHold.OriginalKey));

        await hold.ApplyAsync(hold: false, 0, T0.AddMinutes(5));
        AssertWrites(client, MaxDischarge(4_200));
    }

    [Fact]
    public async Task A_stored_original_the_inverter_no_longer_shows_is_forgotten_without_a_write()
    {
        // Put back by the app while Gleanvolt was down.
        var client = Inverter(4_200);
        var state = new InMemoryDriverStateStore();
        state.Set(HuaweiBatteryHold.OriginalKey, "4200");

        await Hold(client, state).ApplyAsync(hold: false, 0, T0);

        Assert.Empty(client.Writes);
        Assert.Null(state.Get(HuaweiBatteryHold.OriginalKey));
    }

    [Fact]
    public async Task Zero_is_never_stored_as_the_original_nor_written_back()
    {
        // Somebody else set 0, and Gleanvolt has nothing stored: it is theirs to undo.
        var client = Inverter(0);
        var state = new InMemoryDriverStateStore();
        var hold = Hold(client, state);

        var armed = await hold.ApplyAsync(hold: true, 0, T0);
        var released = await hold.ApplyAsync(hold: false, 0, T0.AddMinutes(5));

        Assert.True(armed is { Held: true, Wrote: false });
        Assert.True(released is { Held: false, Wrote: false });
        Assert.Empty(client.Writes);
        Assert.Null(state.Get(HuaweiBatteryHold.OriginalKey));
    }

    [Fact]
    public async Task A_steady_state_neither_reads_nor_writes()
    {
        var client = Inverter(5_000);
        var hold = Hold(client, new InMemoryDriverStateStore());

        await hold.ApplyAsync(hold: false, 0, T0);
        await hold.ApplyAsync(hold: false, 0, T0.AddSeconds(5));
        Assert.Empty(client.Reads);

        await hold.ApplyAsync(hold: true, 0, T0.AddSeconds(10));
        var reads = client.Reads.Count;
        await hold.ApplyAsync(hold: true, 1_000, T0.AddSeconds(15));

        Assert.Equal(reads, client.Reads.Count);
        Assert.Single(client.Writes);
    }

    [Fact]
    public async Task Arming_again_waits_out_the_minimum_interval_but_releasing_never_waits()
    {
        var client = Inverter(5_000);
        var hold = Hold(client, new InMemoryDriverStateStore());

        await hold.ApplyAsync(hold: true, 0, T0);
        var released = await hold.ApplyAsync(hold: false, 0, T0.AddSeconds(5));
        var early = await hold.ApplyAsync(hold: true, 0, T0.AddSeconds(10));
        var later = await hold.ApplyAsync(hold: true, 0, T0.AddSeconds(5) + HuaweiBatteryHold.MinArmInterval);

        Assert.True(released.Wrote);
        Assert.True(early is { Held: false, Wrote: false });
        Assert.True(later is { Held: true, Wrote: true });
        Assert.Equal(3, client.Writes.Count);
    }

    [Fact]
    public async Task In_dry_run_nothing_is_written_or_stored()
    {
        var client = Inverter(5_000);
        var state = new InMemoryDriverStateStore();
        var hold = Hold(client, state, dryRun: true);

        var armed = await hold.ApplyAsync(hold: true, 0, T0);
        await hold.ReleaseOnShutdownAsync();

        Assert.True(armed is { Held: true, Wrote: true });
        Assert.Empty(client.Writes);
        Assert.Null(state.Get(HuaweiBatteryHold.OriginalKey));
    }

    [Fact]
    public async Task Behind_an_emma_the_hold_writes_to_the_inverter_not_the_emma()
    {
        var client = Inverter(5_000);
        var driver = new HuaweiSun2000DriverFactory().Create(
            client, new InverterDriverOptions { Topology = HuaweiSun2000DriverFactory.EmmaTopology });

        await driver.BatteryHold!.ApplyAsync(hold: true, 0, T0);

        Assert.Null(Assert.Single(client.Writes).UnitId);
    }

    private sealed class FailingStore : IDriverStateStore
    {
        public string? Get(string key) => null;

        public void Set(string key, string? value) => throw new IOException("disk full");
    }
}
