using Gleanvolt.Core.Enums;

namespace Gleanvolt.Core.Tests.Enums;

public class EvChargerStatusMappingTests
{
    [Theory]
    [InlineData((ushort)0, EvChargerStatus.Available)]
    [InlineData((ushort)2, EvChargerStatus.Charging)]
    [InlineData((ushort)4, EvChargerStatus.Faulted)]
    [InlineData((ushort)13, EvChargerStatus.Stopping)]
    [InlineData((ushort)14, EvChargerStatus.Unknown)]
    [InlineData((ushort)99, EvChargerStatus.Unknown)]
    public void FromRaw_MapsKnownAndUnknownCodes(ushort raw, EvChargerStatus expected)
    {
        Assert.Equal(expected, EvChargerStatusMapping.FromRaw(raw));
    }
}
