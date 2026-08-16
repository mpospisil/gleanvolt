using Microsoft.Extensions.Logging.Abstractions;

namespace Solax.Hosting.Tests;

public class BatteryHoldSelectorTests
{
    private static BatteryHoldSelector Create(bool initial) =>
        new(initial, NullLogger<BatteryHoldSelector>.Instance);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SeedsFromTheInitialValue(bool initial) =>
        Assert.Equal(initial, Create(initial).Hold);

    [Fact]
    public void Set_ChangesTheHold()
    {
        var selector = Create(false);

        selector.Set(true, "test");

        Assert.True(selector.Hold);
    }

    [Fact]
    public void Set_RaisesChanged_OnlyOnActualChange()
    {
        var selector = Create(false);
        var changes = new List<bool>();
        selector.Changed += changes.Add;

        selector.Set(true, "test");   // change
        selector.Set(true, "test");   // no change
        selector.Set(false, "test");  // change

        Assert.Equal([true, false], changes);
    }
}
