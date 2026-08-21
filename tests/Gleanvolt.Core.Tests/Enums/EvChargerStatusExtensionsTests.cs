using Gleanvolt.Core.Enums;

namespace Gleanvolt.Core.Tests.Enums;

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
    [InlineData(EvChargerStatus.Available, true)]        // the charger says so: no car
    [InlineData(EvChargerStatus.Unavailable, true)]
    [InlineData(EvChargerStatus.Faulted, true)]
    [InlineData(EvChargerStatus.Charging, false)]
    [InlineData(EvChargerStatus.ChargePaused, false)]
    [InlineData(EvChargerStatus.Unknown, false)]         // a failed read is not a fact about the car
    public void IsCarKnownDisconnected(EvChargerStatus status, bool expected) =>
        Assert.Equal(expected, status.IsCarKnownDisconnected());

    [Theory]
    [InlineData(EvChargerStatus.Unknown, false)]
    [InlineData(EvChargerStatus.Available, true)]
    [InlineData(EvChargerStatus.Charging, true)]
    public void IsConnectionKnown(EvChargerStatus status, bool expected) =>
        Assert.Equal(expected, status.IsConnectionKnown());

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
