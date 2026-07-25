using Microsoft.Extensions.Logging.Abstractions;
using Solax.Core.Enums;

namespace Solax.Worker.Tests;

public class ChargeControlModeSelectorTests
{
    private static ChargeControlModeSelector Create(ChargeControlMode initial) =>
        new(initial, NullLogger<ChargeControlModeSelector>.Instance);

    [Theory]
    [InlineData(ChargeControlMode.Off)]
    [InlineData(ChargeControlMode.Solar)]
    [InlineData(ChargeControlMode.Force)]
    public void SeedsFromTheInitialMode(ChargeControlMode initial) =>
        Assert.Equal(initial, Create(initial).Mode);

    [Fact]
    public void Set_ChangesTheMode()
    {
        var selector = Create(ChargeControlMode.Off);

        selector.Set(ChargeControlMode.Force, "test");

        Assert.Equal(ChargeControlMode.Force, selector.Mode);
    }

    [Fact]
    public void Set_RaisesChanged_OnlyOnActualChange()
    {
        var selector = Create(ChargeControlMode.Off);
        var changes = new List<ChargeControlMode>();
        selector.Changed += changes.Add;

        selector.Set(ChargeControlMode.Solar, "test");  // change
        selector.Set(ChargeControlMode.Solar, "test");  // no change
        selector.Set(ChargeControlMode.Off, "test");    // change

        Assert.Equal([ChargeControlMode.Solar, ChargeControlMode.Off], changes);
    }
}
