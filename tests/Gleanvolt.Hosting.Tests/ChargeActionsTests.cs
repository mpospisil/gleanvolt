using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// Issue #89: the one way controlled charging starts and stops. The order matters more than anything
/// else here — the charger is put into Fast <em>before</em> the mode moves — because a mode selected
/// over a charger that refused the write is exactly the silent failure this shape exists to remove.
/// </summary>
public class ChargeActionsTests
{
    private readonly FakeEvChargerControl _charger = new() { CurrentSettings = new(EvChargerMode.Green, 6) };

    private readonly ChargeControlModeSelector _mode =
        new(ChargeControlMode.Off, NullLogger<ChargeControlModeSelector>.Instance);

    /// <summary>The reference install's ceiling: 16 A on three phases, about 11 kW.</summary>
    private const int MaxAmps = 16;

    private ChargeActions Actions() => new(
        _charger,
        _mode,
        new FastChargingController(MaxAmps, TimeSpan.FromMinutes(2)),
        NullLogger<ChargeActions>.Instance);

    [Theory]
    [InlineData(ChargeControlMode.Solar)]
    [InlineData(ChargeControlMode.Forecasted)]
    [InlineData(ChargeControlMode.FastNoBattery)]
    [InlineData(ChargeControlMode.Targeted)]
    public async Task StartAsync_WritesFastAndThenSelectsTheMode(ChargeControlMode mode)
    {
        var result = await Actions().StartAsync(mode, "Web UI");

        Assert.True(result.Succeeded);
        Assert.Equal(EvChargerMode.Fast, Assert.Single(_charger.ModeWrites).Mode);
        Assert.Equal(mode, _mode.Mode);
    }

    [Fact]
    public async Task StartAsync_WritesFastBeforeTheModeMoves()
    {
        // Recorded from the selector's own event, so this is the real ordering rather than a claim
        // about it: by the time the mode changes, the write has already happened.
        EvChargerMode? writtenWhenTheModeMoved = null;
        _mode.Changed += _ => writtenWhenTheModeMoved =
            _charger.ModeWrites.Count == 0 ? null : _charger.ModeWrites[^1].Mode;

        await Actions().StartAsync(ChargeControlMode.Solar, "Web UI");

        Assert.Equal(EvChargerMode.Fast, writtenWhenTheModeMoved);
    }

    [Fact]
    public async Task StartAsync_ANamedSourceReachesTheChargerLog()
    {
        await Actions().StartAsync(ChargeControlMode.Solar, "Home Assistant");

        Assert.Contains("Home Assistant", Assert.Single(_charger.ModeWrites).Reason);
    }

    [Fact]
    public async Task StartAsync_AFailedWriteLeavesTheModeAloneAndSaysWhy()
    {
        _charger.ModeWriteFailure = "the charger did not answer";
        _mode.Set(ChargeControlMode.Solar, "test setup");

        var result = await Actions().StartAsync(ChargeControlMode.FastNoBattery, "Web UI");

        Assert.False(result.Succeeded);
        Assert.Contains("did not accept Fast", result.Message);
        Assert.Contains("the charger did not answer", result.Message);
        Assert.Equal(ChargeControlMode.Solar, _mode.Mode); // untouched, not dropped to Off
    }

    [Fact]
    public async Task StartAsync_OffIsACallerError() =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Actions().StartAsync(ChargeControlMode.Off, "Web UI"));

    [Fact]
    public async Task StopAsync_WritesStopAndReturnsTheModeToOff()
    {
        _mode.Set(ChargeControlMode.FastNoBattery, "test setup");

        var result = await Actions().StopAsync("Web UI");

        Assert.True(result.Succeeded);
        Assert.Equal(EvChargerMode.Stop, Assert.Single(_charger.ModeWrites).Mode);
        Assert.Equal(ChargeControlMode.Off, _mode.Mode);
    }

    [Fact]
    public async Task StopAsync_WritesStopEvenWhenTheControllerWasAlreadyOff()
    {
        // The button says stop charging, so it stops charging -- whether or not this service was the
        // thing that started it. Idempotent, and one Modbus write.
        var result = await Actions().StopAsync("Home Assistant");

        Assert.True(result.Succeeded);
        Assert.Equal(EvChargerMode.Stop, Assert.Single(_charger.ModeWrites).Mode);
        Assert.Equal(ChargeControlMode.Off, _mode.Mode);
    }

    [Fact]
    public async Task StopAsync_NeverTouchesTheCurrentSetpoint()
    {
        _mode.Set(ChargeControlMode.Solar, "test setup");

        await Actions().StopAsync("Web UI");

        Assert.Empty(_charger.CurrentWrites);
    }

    [Fact]
    public async Task StopAsync_AFailedWriteStillReleasesControl_AndSaysWhat()
    {
        // The asymmetry with StartAsync is deliberate: a controller left driving a charger it cannot
        // reach would go on commanding currents at a wallbox whose owner has just said stop.
        _charger.ModeWriteFailure = "the charger did not answer";
        _mode.Set(ChargeControlMode.FastNoBattery, "test setup");

        var result = await Actions().StopAsync("Web UI");

        Assert.False(result.Succeeded);
        Assert.Contains("did not accept Stop", result.Message);
        Assert.Equal(ChargeControlMode.Off, _mode.Mode);
    }

    [Fact]
    public void ChargeActionResult_SuccessCarriesNoMessage()
    {
        Assert.True(ChargeActionResult.Success.Succeeded);
        Assert.Null(ChargeActionResult.Success.Message);
    }

    // -- The setpoint at activation. The fast mode's current is a constant, so there is no reason to
    // leave the first poll to command it -- least of all after a previous charge left the charger at
    // the pause current, which is where this actually bites.

    [Fact]
    public async Task StartAsync_PutsTheChargerAtTheMaximumWhenFastIsStarted()
    {
        // Where a finished charge leaves it: in Fast, at PauseCurrentAmps.
        _charger.CurrentSettings = new(EvChargerMode.Fast, 0);

        var result = await Actions().StartAsync(ChargeControlMode.FastNoBattery, "Web UI");

        Assert.True(result.Succeeded);
        Assert.Equal(MaxAmps, Assert.Single(_charger.CurrentWrites).Target);
    }

    [Fact]
    public async Task StartAsync_WritesTheUseModeBeforeTheCurrent()
    {
        // The charger has to be ours before we command a current under it. If Fast is refused we stop,
        // and the setpoint is never touched -- asserted separately below.
        _charger.CurrentSettings = new(EvChargerMode.Green, 0);

        await Actions().StartAsync(ChargeControlMode.FastNoBattery, "Web UI");

        Assert.Single(_charger.ModeWrites);
        Assert.Single(_charger.CurrentWrites);
    }

    [Fact]
    public async Task StartAsync_HandsTheDevicesOwnCurrentOverSoAPointlessWriteCanBeSkipped()
    {
        // "If it is not set" is not decided here: EvChargerControl skips a write that would not move
        // the setpoint (EvChargerControlTests covers that). What this level owes it is the truth about
        // what is on the device, read rather than assumed -- get the `active` argument wrong and the
        // skip decision is made against a number nobody measured.
        _charger.CurrentSettings = new(EvChargerMode.Fast, MaxAmps);

        await Actions().StartAsync(ChargeControlMode.FastNoBattery, "Web UI");

        var write = Assert.Single(_charger.CurrentWrites);
        Assert.Equal(MaxAmps, write.Active);
        Assert.Equal(MaxAmps, write.Target);
    }

    [Theory]
    [InlineData(ChargeControlMode.Solar)]
    [InlineData(ChargeControlMode.Forecasted)]
    [InlineData(ChargeControlMode.Targeted)]
    public async Task StartAsync_DoesNotTouchTheCurrentForAnyOtherMode(ChargeControlMode mode)
    {
        // Every other mode computes its setpoint from surplus or from a plan. Writing the maximum ahead
        // of them would charge flat out for a poll interval -- the opposite of what they were started
        // to do.
        _charger.CurrentSettings = new(EvChargerMode.Fast, 0);

        await Actions().StartAsync(mode, "Web UI");

        Assert.Empty(_charger.CurrentWrites);
    }

    [Fact]
    public async Task StartAsync_DoesNotTouchTheCurrentWhenTheChargerRefusedFast()
    {
        _charger.CurrentSettings = new(EvChargerMode.Green, 0);
        _charger.ModeWriteFailure = "the charger did not answer";

        var result = await Actions().StartAsync(ChargeControlMode.FastNoBattery, "Web UI");

        Assert.False(result.Succeeded);
        Assert.Empty(_charger.CurrentWrites);
        Assert.Equal(ChargeControlMode.Off, _mode.Mode);
    }

    [Fact]
    public async Task StartAsync_StillStartsWhenTheCurrentWriteFails()
    {
        // The use-mode is already written and the control loop commands the same current seconds later.
        // Refusing the whole start here would turn a recoverable hiccup into a charge that never began.
        _charger.CurrentSettings = new(EvChargerMode.Fast, 0);
        _charger.CurrentWriteFailure = "the charger did not answer";

        var result = await Actions().StartAsync(ChargeControlMode.FastNoBattery, "Web UI");

        Assert.True(result.Succeeded);
        Assert.Equal(ChargeControlMode.FastNoBattery, _mode.Mode);
    }
}
