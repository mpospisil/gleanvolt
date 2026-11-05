using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Forecasting;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// Issue #300: the home battery's capacity ships unset. A plan built on no capacity is not a plan, so
/// the day plan says it is unavailable, and why, rather than dividing by zero or borrowing a figure.
/// </summary>
public class DayPlanWithoutBatteryCapacityTests
{
    private static readonly DateTimeOffset Now = new(new DateTime(2026, 9, 26, 10, 0, 0, DateTimeKind.Local));

    private static DayPlanProvider Provider(ForecastChargeOptions options) =>
        new(
            new SunnyDay(Now),
            Options.Create(options),
            Options.Create(new ChargeControlOptions()),
            new ChargePowerConverter(230, 3),
            NullLogger<DayPlanProvider>.Instance,
            timeProvider: new FixedTimeProvider(Now));

    private static EnergyState Reading() => new(Now, 50, 0, 3000, 500, EvChargerStatus.Available, 0);

    [Fact]
    public void ShipsUnset()
    {
        Assert.Null(new ForecastChargeOptions().BatteryCapacityKWh);
        Assert.Null(new ForecastChargeOptions().BatteryCapacityWh);
        Assert.Null(new ForecastChargeOptions { BatteryCapacityKWh = 0 }.BatteryCapacityWh);
        Assert.Equal(9000, new ForecastChargeOptions { BatteryCapacityKWh = 9 }.BatteryCapacityWh);
    }

    [Fact]
    public void WithoutIt_ThePlanIsUnavailableAndSaysWhy()
    {
        var plan = Provider(new ForecastChargeOptions()).Update(Reading(), loanedTodayWh: 0);

        Assert.False(plan.IsUsable);
        Assert.Contains("ChargeControl:Forecast:BatteryCapacityKWh", plan.Reason);
    }

    [Fact]
    public void WithIt_TheSameDayPlans()
    {
        var plan = Provider(new ForecastChargeOptions { BatteryCapacityKWh = 9 }).Update(Reading(), loanedTodayWh: 0);

        Assert.True(plan.IsUsable, plan.Reason);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    private sealed class SunnyDay : ISolarForecastService
    {
        private readonly SolarForecast _forecast;

        public SunnyDay(DateTimeOffset now)
        {
            var periods = new List<SolarForecastPeriod>();
            for (var end = now.AddMinutes(30); end <= now.AddHours(8); end = end.AddMinutes(30))
            {
                periods.Add(new SolarForecastPeriod(end, TimeSpan.FromMinutes(30), 6000));
            }

            _forecast = new SolarForecast(now, periods);
        }

        public SolarForecast? GetForecastForToday() => _forecast;

        public SolarForecast? GetForecast(DateTimeOffset from, DateTimeOffset to) => _forecast.ForPeriod(from, to);

        public SolarForecast? GetDayForecast(DateOnly localDate) => _forecast;
    }
}
