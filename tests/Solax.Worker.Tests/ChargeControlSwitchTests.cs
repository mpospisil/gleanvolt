using Microsoft.Extensions.Logging.Abstractions;

namespace Solax.Worker.Tests;

public class ChargeControlSwitchTests
{
    private static ChargeControlSwitch Create(bool initial) =>
        new(initial, NullLogger<ChargeControlSwitch>.Instance);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SeedsFromTheInitialValue(bool initial) =>
        Assert.Equal(initial, Create(initial).IsEnabled);

    [Fact]
    public void Set_TogglesTheState()
    {
        var sw = Create(false);

        sw.Set(true, "test");

        Assert.True(sw.IsEnabled);
    }

    [Fact]
    public void Set_RaisesChanged_OnlyOnActualChange()
    {
        var sw = Create(false);
        var changes = new List<bool>();
        sw.Changed += changes.Add;

        sw.Set(true, "test");   // change -> event
        sw.Set(true, "test");   // no change -> no event
        sw.Set(false, "test");  // change -> event

        Assert.Equal([true, false], changes);
    }
}
