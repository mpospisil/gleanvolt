using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

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

    private ChargeActions Actions() => new(_charger, _mode, NullLogger<ChargeActions>.Instance);

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
}
