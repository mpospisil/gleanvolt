using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Tests.Models;

public class SolarForecastTests
{
    private static readonly DateTimeOffset RetrievedAt = new(2026, 7, 21, 6, 0, 0, TimeSpan.Zero);

    // A period ending at `end` (UTC) covering the 30 minutes before it.
    private static SolarForecastPeriod Period(DateTimeOffset end, double watts) =>
        new(end, TimeSpan.FromMinutes(30), watts);

    private static SolarForecast ForecastWith(params SolarForecastPeriod[] periods) =>
        new(RetrievedAt, periods);

    [Fact]
    public void ExpectedPowerWattsAt_ReturnsPowerOfCoveringPeriod()
    {
        var forecast = ForecastWith(
            Period(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero), 3000),
            Period(new DateTimeOffset(2026, 7, 21, 12, 30, 0, TimeSpan.Zero), 3500));

        // 12:15 falls in the (11:30, 12:00]... no -- it falls in (12:00, 12:30] -> 3500.
        var at = new DateTimeOffset(2026, 7, 21, 12, 15, 0, TimeSpan.Zero);

        Assert.Equal(3500, forecast.ExpectedPowerWattsAt(at));
    }

    [Fact]
    public void ExpectedPowerWattsAt_OnPeriodEndBoundary_BelongsToThatPeriod()
    {
        var forecast = ForecastWith(
            Period(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero), 3000),
            Period(new DateTimeOffset(2026, 7, 21, 12, 30, 0, TimeSpan.Zero), 3500));

        // The window is half-open (PeriodStart, PeriodEnd], so 12:00 exactly belongs to the first.
        var at = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(3000, forecast.ExpectedPowerWattsAt(at));
    }

    [Fact]
    public void ExpectedPowerWattsAt_OutsideHorizon_ReturnsNull()
    {
        var forecast = ForecastWith(
            Period(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero), 3000));

        var at = new DateTimeOffset(2026, 7, 21, 20, 0, 0, TimeSpan.Zero);

        Assert.Null(forecast.ExpectedPowerWattsAt(at));
    }

    [Fact]
    public void ExpectedEnergyWattHours_SumsPowerOverPeriodDurations()
    {
        // Two 30-minute periods at 3000W and 1000W -> 1500Wh + 500Wh = 2000Wh.
        var forecast = ForecastWith(
            Period(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero), 3000),
            Period(new DateTimeOffset(2026, 7, 21, 12, 30, 0, TimeSpan.Zero), 1000));

        Assert.Equal(2000, forecast.ExpectedEnergyWattHours, precision: 6);
    }

    [Fact]
    public void PeakPowerWatts_ReturnsHighestPeriod()
    {
        var forecast = ForecastWith(
            Period(new DateTimeOffset(2026, 7, 21, 11, 0, 0, TimeSpan.Zero), 1200),
            Period(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero), 4200),
            Period(new DateTimeOffset(2026, 7, 21, 13, 0, 0, TimeSpan.Zero), 3800));

        Assert.Equal(4200, forecast.PeakPowerWatts);
    }

    [Fact]
    public void PeakPowerWatts_EmptyForecast_IsZero()
    {
        Assert.Equal(0, ForecastWith().PeakPowerWatts);
    }

    [Fact]
    public void ForDate_KeepsOnlyPeriodsOnThatLocalDate()
    {
        // UTC+2 zone: a period ending 21:30 UTC is 23:30 local (still the 21st), while one ending
        // 22:30 UTC is 00:30 local on the 22nd and must be excluded from the 21st.
        var plusTwo = TimeZoneInfo.CreateCustomTimeZone("t+2", TimeSpan.FromHours(2), "t+2", "t+2");
        var forecast = ForecastWith(
            Period(new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.Zero), 3000),
            Period(new DateTimeOffset(2026, 7, 21, 21, 30, 0, TimeSpan.Zero), 200),
            Period(new DateTimeOffset(2026, 7, 21, 22, 30, 0, TimeSpan.Zero), 100));

        var today = forecast.ForDate(new DateOnly(2026, 7, 21), plusTwo);

        Assert.Equal(2, today.Periods.Count);
        Assert.All(today.Periods, p => Assert.True(p.EstimatedPowerWatts >= 200));
    }

    [Fact]
    public void ForPeriod_KeepsPeriodsOverlappingTheWindow()
    {
        var forecast = ForecastWith(
            Period(new DateTimeOffset(2026, 7, 21, 11, 0, 0, TimeSpan.Zero), 1000),
            Period(new DateTimeOffset(2026, 7, 21, 11, 30, 0, TimeSpan.Zero), 2000),
            Period(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero), 3000),
            Period(new DateTimeOffset(2026, 7, 21, 12, 30, 0, TimeSpan.Zero), 4000));

        // Window (11:15, 12:00]: overlaps the 11:30 period (11:00-11:30]? it ends 11:30 > 11:15,
        // starts 11:00 < 12:00 -> included), the 12:00 period, but not the 12:30 one.
        var window = forecast.ForPeriod(
            new DateTimeOffset(2026, 7, 21, 11, 15, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(new[] { 2000.0, 3000.0 }, window.Periods.Select(p => p.EstimatedPowerWatts));
    }

    [Fact]
    public void ForPeriod_ToBeforeFrom_Throws()
    {
        var forecast = ForecastWith(
            Period(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero), 3000));

        Assert.Throws<ArgumentException>(() => forecast.ForPeriod(
            new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 21, 11, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void ConfidenceBands_SelectTheRequestedEstimate()
    {
        var period = new SolarForecastPeriod(
            new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero), TimeSpan.FromMinutes(30),
            EstimatedPowerWatts: 5000, EstimatedPowerWattsP10: 3000, EstimatedPowerWattsP90: 7000);

        Assert.Equal(3000, period.PowerWatts(ForecastConfidence.P10));
        Assert.Equal(5000, period.PowerWatts(ForecastConfidence.P50));
        Assert.Equal(7000, period.PowerWatts(ForecastConfidence.P90));
        Assert.Equal(1500, period.EnergyWattHoursAt(ForecastConfidence.P10));
    }

    [Fact]
    public void AMissingBandFallsBackToTheMedian()
    {
        // A provider (or a partial response) without the percentile fields must not read as darkness --
        // that would starve the car for the whole day.
        var period = new SolarForecastPeriod(
            new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero), TimeSpan.FromMinutes(30), 5000);

        Assert.Equal(5000, period.PowerWatts(ForecastConfidence.P10));
        Assert.Equal(5000, period.PowerWatts(ForecastConfidence.P90));
    }

    [Fact]
    public void EnergyAtAConfidenceBand_SumsThePeriods()
    {
        var forecast = new SolarForecast(
            DateTimeOffset.UtcNow,
            [
                new SolarForecastPeriod(new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero), TimeSpan.FromMinutes(30), 4000, 2000),
                new SolarForecastPeriod(new DateTimeOffset(2026, 7, 27, 12, 30, 0, TimeSpan.Zero), TimeSpan.FromMinutes(30), 6000, 3000),
            ]);

        Assert.Equal(5000, forecast.ExpectedEnergyWattHours);
        Assert.Equal(2500, forecast.EnergyWattHoursAt(ForecastConfidence.P10));
    }

    [Fact]
    public void NextPeriodStartAbove_FindsFirstLight()
    {
        var dawn = new DateTimeOffset(2026, 7, 27, 5, 0, 0, TimeSpan.Zero);
        var forecast = new SolarForecast(
            dawn,
            [
                new SolarForecastPeriod(dawn.AddMinutes(30), TimeSpan.FromMinutes(30), 5),
                new SolarForecastPeriod(dawn.AddMinutes(60), TimeSpan.FromMinutes(30), 400),
            ]);

        Assert.Equal(dawn.AddMinutes(30), forecast.NextPeriodStartAbove(dawn, thresholdWatts: 100));
        Assert.Null(forecast.NextPeriodStartAbove(dawn, thresholdWatts: 10_000));
    }
}

