using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Enums;
using Gleanvolt.Infrastructure.RegisterMaps;

namespace Gleanvolt.Infrastructure.Tests;

public class BatteryDischargeControlTests
{
    private static readonly ushort BlockAddress = InverterRegisterMap.PowerControlBlockStart.Address;
    private static readonly DateTimeOffset T0 = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    private static BatteryDischargeControl Create(
        FakeModbusClient client, bool dryRun = false, int durationSeconds = 60, double thresholdWatts = 100) =>
        new(client, NullLogger<BatteryDischargeControl>.Instance, dryRun,
            TimeSpan.FromSeconds(durationSeconds), thresholdWatts);

    // The registers written in one block, in order, so payload encoding can be asserted directly.
    private static ushort[] WrittenBlock(FakeModbusClient client) =>
        [.. client.Writes.Select(w => w.Value)];

    [Fact]
    public async Task Hold_WritesTheWholeBlockAtTheControlAddress()
    {
        var client = new FakeModbusClient();

        var state = await Create(client).ApplyAsync(hold: true, activePowerTargetWatts: -2000, T0);

        Assert.True(state.Held);
        Assert.True(state.Wrote);
        Assert.Equal(PowerControlPayload.RegisterCount, client.Writes.Count);
        Assert.Equal(BlockAddress, client.Writes[0].Address);
        Assert.Equal(
            (ushort)(BlockAddress + PowerControlPayload.RegisterCount - 1), client.Writes[^1].Address);
    }

    [Fact]
    public async Task Hold_EncodesModeSetTypeActivePowerAndDuration()
    {
        var client = new FakeModbusClient();

        await Create(client, durationSeconds: 60).ApplyAsync(hold: true, activePowerTargetWatts: -2000, T0);

        var block = WrittenBlock(client);
        Assert.Equal((ushort)InverterPowerControlMode.EnabledPowerControl, block[0]);
        Assert.Equal((ushort)InverterPowerControlSetType.Set, block[1]);
        // active_power = -2000 as int32, low word first: 0xFFFFF830 -> [0xF830, 0xFFFF].
        Assert.Equal(0xF830, block[2]);
        Assert.Equal(0xFFFF, block[3]);
        Assert.Equal(new ushort[] { 0, 0 }, block[4..6]);   // reactive_power
        Assert.Equal(60, block[6]);                         // duration, seconds
        // target_soc / target_energy / target_charge_discharge_power / timeout are all unused here.
        Assert.Equal(new ushort[] { 0, 0, 0, 0, 0, 0 }, block[7..13]);
    }

    [Fact]
    public async Task Release_WritesTheDisabledBlock()
    {
        var client = new FakeModbusClient();
        var control = Create(client);
        await control.ApplyAsync(hold: true, activePowerTargetWatts: -2000, T0);
        client.Writes.Clear();

        var state = await control.ApplyAsync(hold: false, activePowerTargetWatts: 0, T0.AddSeconds(5));

        Assert.False(state.Held);
        Assert.True(state.Wrote);
        var block = WrittenBlock(client);
        Assert.Equal((ushort)InverterPowerControlMode.Disabled, block[0]);
        Assert.Equal(0, block[6]); // duration zeroed: the release takes effect now, it doesn't run out
    }

    [Fact]
    public async Task SteadyState_WritesNothing()
    {
        var client = new FakeModbusClient();
        var control = Create(client, durationSeconds: 60, thresholdWatts: 100);
        await control.ApplyAsync(hold: true, activePowerTargetWatts: -2000, T0);
        client.Writes.Clear();

        // 5s later (one poll), target drifted 40W — below the threshold, and nowhere near renewal.
        var state = await control.ApplyAsync(hold: true, activePowerTargetWatts: -2040, T0.AddSeconds(5));

        Assert.True(state.Held);
        Assert.False(state.Wrote);
        Assert.Empty(client.Writes);
    }

    [Fact]
    public async Task AlreadyReleased_WritesNothing()
    {
        var client = new FakeModbusClient();

        var state = await Create(client).ApplyAsync(hold: false, activePowerTargetWatts: 0, T0);

        Assert.False(state.Held);
        Assert.False(state.Wrote);
        Assert.Empty(client.Writes);
    }

    [Fact]
    public async Task TargetMovingBeyondTheThreshold_Rewrites()
    {
        var client = new FakeModbusClient();
        var control = Create(client, thresholdWatts: 100);
        await control.ApplyAsync(hold: true, activePowerTargetWatts: -2000, T0);
        client.Writes.Clear();

        await control.ApplyAsync(hold: true, activePowerTargetWatts: -2100, T0.AddSeconds(5));

        var block = WrittenBlock(client);
        Assert.Equal(0xF7CC, block[2]); // -2100 low word
    }

    [Fact]
    public async Task ArmedCommand_IsRenewedAtHalfTheDuration_SoItNeverLapses()
    {
        var client = new FakeModbusClient();
        var control = Create(client, durationSeconds: 60);
        await control.ApplyAsync(hold: true, activePowerTargetWatts: -2000, T0);
        client.Writes.Clear();

        // 29s in: still comfortably armed, nothing to do.
        Assert.False((await control.ApplyAsync(hold: true, -2000, T0.AddSeconds(29))).Wrote);
        Assert.Empty(client.Writes);

        // 30s in (half of 60s): renew, well before the inverter would drop the command.
        Assert.True((await control.ApplyAsync(hold: true, -2000, T0.AddSeconds(30))).Wrote);
        Assert.Equal(PowerControlPayload.RegisterCount, client.Writes.Count);
    }

    [Fact]
    public async Task DryRun_WritesNothingButStillTracksTheArmedState()
    {
        var client = new FakeModbusClient();
        var control = Create(client, dryRun: true);

        var armed = await control.ApplyAsync(hold: true, activePowerTargetWatts: -2000, T0);
        Assert.True(armed.Held);
        Assert.Equal(-2000, armed.ActivePowerTargetWatts);

        // Change-detection still behaves like a real run, so the dry-run log reads the same.
        var steady = await control.ApplyAsync(hold: true, activePowerTargetWatts: -2010, T0.AddSeconds(5));
        Assert.False(steady.Wrote);

        Assert.Empty(client.Writes);
    }

    [Fact]
    public async Task DurationBeyondTheHardwareMaximum_IsClamped()
    {
        var client = new FakeModbusClient();

        await Create(client, durationSeconds: 40_000).ApplyAsync(hold: true, activePowerTargetWatts: -1000, T0);

        Assert.Equal(PowerControlPayload.MaxDurationSeconds, WrittenBlock(client)[6]);
    }

    [Fact]
    public async Task FailedWrite_IsNotRecordedAsArmed_SoTheNextPollRetries()
    {
        var client = new ThrowingModbusClient();
        var control = Create(client);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => control.ApplyAsync(hold: true, activePowerTargetWatts: -2000, T0));

        // The retry writes again rather than reporting a hold that was never armed.
        client.Throw = false;
        var state = await control.ApplyAsync(hold: true, activePowerTargetWatts: -2000, T0.AddSeconds(5));
        Assert.True(state.Wrote);
        Assert.True(state.Held);
    }

    private sealed class ThrowingModbusClient : FakeModbusClient
    {
        public bool Throw { get; set; } = true;

        public override Task WriteMultipleRegistersAsync(ushort startAddress, ushort[] values, CancellationToken cancellationToken = default) =>
            Throw
                ? Task.FromException(new IOException("modbus timeout"))
                : base.WriteMultipleRegistersAsync(startAddress, values, cancellationToken);
    }
}
