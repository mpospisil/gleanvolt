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
            DailyEvTargetKWh = 12,
            SessionEnergyTargetKWh = 30,
            MinBatterySocFloorPercent = 55,
        });

        Assert.Equal(12_000, settings.DailyEvTargetWh);
        Assert.Equal(30_000, settings.SessionEnergyTargetWh);
        Assert.Equal(55, settings.MinBatterySocFloorPercent);
    }

    [Fact]
    public void ValuesAreSettableAtRuntime()
    {
        var settings = Settings();

        settings.SetDailyEvTargetWh(20_000, "test");
        settings.SetSessionEnergyTargetWh(25_000, "test");
        settings.SetMinBatterySocFloorPercent(70, "test");

        Assert.Equal(20_000, settings.DailyEvTargetWh);
        Assert.Equal(25_000, settings.SessionEnergyTargetWh);
        Assert.Equal(70, settings.MinBatterySocFloorPercent);
    }

    [Fact]
    public void NonsenseValuesAreClampedRatherThanStored()
    {
        var settings = Settings();

        settings.SetDailyEvTargetWh(-5000, "test");
        settings.SetMinBatterySocFloorPercent(140, "test");

        Assert.Equal(0, settings.DailyEvTargetWh);
        Assert.Equal(100, settings.MinBatterySocFloorPercent);
    }
}
