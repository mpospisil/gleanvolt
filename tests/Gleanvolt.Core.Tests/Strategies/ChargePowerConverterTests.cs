using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

public class ChargePowerConverterTests
{
    [Fact]
    public void SinglePhase_SixAmps_IsAbout1380W()
    {
        var converter = new ChargePowerConverter(nominalVoltage: 230, phases: 1);

        Assert.Equal(1380, converter.AmpsToWatts(6));
        Assert.Equal(6, converter.WattsToAmps(1380));
    }

    [Fact]
    public void ThreePhase_SixAmps_IsAbout4140W()
    {
        var converter = new ChargePowerConverter(nominalVoltage: 230, phases: 3);

        Assert.Equal(4140, converter.AmpsToWatts(6));
        Assert.Equal(6, converter.WattsToAmps(4140));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-230)]
    public void InvalidVoltage_Throws(double voltage) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChargePowerConverter(voltage, phases: 1));

    [Fact]
    public void InvalidPhases_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChargePowerConverter(nominalVoltage: 230, phases: 0));
}
