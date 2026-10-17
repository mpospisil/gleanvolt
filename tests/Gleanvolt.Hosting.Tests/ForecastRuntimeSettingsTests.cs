using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Forecasting;

namespace Gleanvolt.Hosting.Tests;

public class ForecastRuntimeSettingsTests
{
    private static ForecastRuntimeSettings Settings(ForecastChargeOptions? options = null) =>
        new(Options.Create(options ?? new ForecastChargeOptions()), NullLogger<ForecastRuntimeSettings>.Instance);

    [Fact]
    public void SeedsFromConfiguration()
    {
        var settings = Settings(new ForecastChargeOptions
        {
            MinBatterySocFloorPercent = 55,
            FloorResumeMarginPercent = 7,
        });

        Assert.Equal(55, settings.MinBatterySocFloorPercent);
        Assert.Equal(7, settings.FloorResumeMarginPercent);
    }

    [Fact]
    public void ValuesAreSettableAtRuntime()
    {
        var settings = Settings();

        settings.SetMinBatterySocFloorPercent(70, "test");
        settings.SetFloorResumeMarginPercent(8, "test");

        Assert.Equal(70, settings.MinBatterySocFloorPercent);
        Assert.Equal(8, settings.FloorResumeMarginPercent);
    }

    [Fact]
    public void NonsenseValuesAreClampedRatherThanStored()
    {
        var settings = Settings();

        settings.SetMinBatterySocFloorPercent(140, "test");

        Assert.Equal(100, settings.MinBatterySocFloorPercent);
    }

    [Fact]
    public void TheResumeMarginMayBeZero()
    {
        // It used to be floored at the Forecasted hold's release margin; that hold is gone.
        var settings = Settings(new ForecastChargeOptions { FloorResumeMarginPercent = 0 });

        Assert.Equal(0, settings.FloorResumeMarginPercent);

        settings.SetFloorResumeMarginPercent(3, "test");

        Assert.Equal(3, settings.FloorResumeMarginPercent);
    }
}
