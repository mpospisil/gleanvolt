using Solax.Core.Enums;

namespace Solax.Core.Tests.Enums;

public class EvChargerStatusExtensionsTests
{
    [Theory]
    [InlineData(EvChargerStatus.Preparing, true)]
    [InlineData(EvChargerStatus.Charging, true)]
    [InlineData(EvChargerStatus.ChargePaused, true)]
    [InlineData(EvChargerStatus.SuspendedEv, true)]
    [InlineData(EvChargerStatus.SuspendedEvse, true)]
    [InlineData(EvChargerStatus.Finishing, true)]
    [InlineData(EvChargerStatus.Available, false)]
    [InlineData(EvChargerStatus.Unavailable, false)]
    [InlineData(EvChargerStatus.Faulted, false)]
    [InlineData(EvChargerStatus.Unknown, false)]
    public void IsCarConnected(EvChargerStatus status, bool expected) =>
        Assert.Equal(expected, status.IsCarConnected());
}
