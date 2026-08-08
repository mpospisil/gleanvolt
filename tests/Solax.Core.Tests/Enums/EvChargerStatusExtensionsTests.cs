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

    [Theory]
    [InlineData(EvChargerStatus.SuspendedEv, true)]      // the car stopped it -- typically its target SOC
    [InlineData(EvChargerStatus.Finishing, true)]        // the session is closing
    [InlineData(EvChargerStatus.Charging, false)]
    [InlineData(EvChargerStatus.Preparing, false)]
    [InlineData(EvChargerStatus.ChargePaused, false)]    // our own pause write, not the car finishing
    [InlineData(EvChargerStatus.SuspendedEvse, false)]   // likewise the charger's doing, not the car's
    [InlineData(EvChargerStatus.Available, false)]
    [InlineData(EvChargerStatus.Faulted, false)]
    public void IsChargeWindingDown(EvChargerStatus status, bool expected) =>
        Assert.Equal(expected, status.IsChargeWindingDown());
}
