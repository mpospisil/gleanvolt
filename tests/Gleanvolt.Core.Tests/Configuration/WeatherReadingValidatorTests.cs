using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Tests.Configuration;

/// <summary>What an external weather provider may publish (#424): one test per rule.</summary>
public class WeatherReadingValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    private static WeatherReading Reading(
        DateTimeOffset? observedAt = null,
        double? temperature = 4.5,
        double? pressure = 101_300,
        double? humidity = 80,
        double? clouds = 40,
        double? visibility = 10_000,
        string condition = "Clouds",
        string description = "scattered clouds",
        DateTimeOffset? sunrise = null,
        DateTimeOffset? sunset = null) =>
        new(new WeatherObservation(observedAt ?? Now.AddMinutes(-12), temperature, pressure, humidity, clouds, visibility, condition, description),
            sunrise, sunset);

    private static string Only(WeatherReading reading, WeatherReading? held = null)
    {
        var problems = WeatherReadingValidator.Check(reading, held, Now);
        Assert.Single(problems);
        return problems.Phrases[0]!.Key;
    }

    [Fact]
    public void A_complete_reading_passes() =>
        Assert.Empty(WeatherReadingValidator.Check(Reading(sunrise: Now.AddHours(-2), sunset: Now.AddHours(7)), null, Now));

    [Fact]
    public void A_temperature_alone_passes() =>
        Assert.Empty(WeatherReadingValidator.Check(
            Reading(pressure: null, humidity: null, clouds: null, visibility: null, condition: "", description: ""), null, Now));

    [Fact]
    public void A_condition_alone_passes() =>
        Assert.Empty(WeatherReadingValidator.Check(
            Reading(temperature: null, pressure: null, humidity: null, clouds: null, visibility: null, description: ""), null, Now));

    [Fact]
    public void A_reading_that_says_nothing_is_refused() =>
        Assert.Equal("Weather.Empty", Only(Reading(temperature: null, condition: " ", description: "")));

    [Fact]
    public void No_observation_time_is_refused() =>
        Assert.Equal("Weather.NoObservedAt", Only(Reading(observedAt: default(DateTimeOffset))));

    [Fact]
    public void An_observation_from_the_future_is_refused() =>
        Assert.Equal("Weather.Future", Only(Reading(observedAt: Now.AddMinutes(6))));

    [Fact]
    public void A_little_clock_drift_is_accepted() =>
        Assert.Empty(WeatherReadingValidator.Check(Reading(observedAt: Now.AddMinutes(4)), null, Now));

    [Fact]
    public void An_observation_older_than_three_hours_is_refused() =>
        Assert.Equal("Weather.TooOld", Only(Reading(observedAt: Now.AddHours(-3).AddMinutes(-1))));

    [Fact]
    public void An_observation_older_than_the_one_held_is_refused() =>
        Assert.Equal("Weather.OlderThanHeld", Only(Reading(observedAt: Now.AddMinutes(-30)), held: Reading(observedAt: Now.AddMinutes(-20))));

    [Fact]
    public void The_same_observation_time_as_the_one_held_is_accepted()
    {
        var held = Reading(observedAt: Now.AddMinutes(-20));

        Assert.Empty(WeatherReadingValidator.Check(Reading(observedAt: Now.AddMinutes(-20)), held, Now));
    }

    [Theory]
    [InlineData(-91)]
    [InlineData(61)]
    [InlineData(double.NaN)]
    public void A_temperature_out_of_range_is_refused(double celsius) =>
        Assert.Equal("Weather.Temperature", Only(Reading(temperature: celsius)));

    [Fact]
    public void A_temperature_in_fahrenheit_is_refused() =>
        Assert.Equal("Weather.Temperature", Only(Reading(temperature: 95)));

    [Theory]
    [InlineData(1013)] // hPa, not Pa
    [InlineData(120_000)]
    public void A_pressure_out_of_range_is_refused(double pascals) =>
        Assert.Equal("Weather.Pressure", Only(Reading(pressure: pascals)));

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Humidity_out_of_range_is_refused(double percent) =>
        Assert.Equal("Weather.Humidity", Only(Reading(humidity: percent)));

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Cloud_cover_out_of_range_is_refused(double percent) =>
        Assert.Equal("Weather.Clouds", Only(Reading(clouds: percent)));

    [Theory]
    [InlineData(-1)]
    [InlineData(100_001)]
    public void Visibility_out_of_range_is_refused(double metres) =>
        Assert.Equal("Weather.Visibility", Only(Reading(visibility: metres)));

    [Fact]
    public void A_condition_too_long_is_refused() =>
        Assert.Equal("Weather.ConditionTooLong", Only(Reading(condition: new string('x', 101))));

    [Fact]
    public void A_description_too_long_is_refused() =>
        Assert.Equal("Weather.DescriptionTooLong", Only(Reading(description: new string('x', 201))));

    [Fact]
    public void A_sunset_before_sunrise_is_refused() =>
        Assert.Equal("Weather.SunsetBeforeSunrise", Only(Reading(sunrise: Now.AddHours(7), sunset: Now.AddHours(-2))));

    [Fact]
    public void A_sunrise_without_a_sunset_is_accepted() =>
        Assert.Empty(WeatherReadingValidator.Check(Reading(sunrise: Now.AddHours(-2)), null, Now));

    [Fact]
    public void Every_problem_is_reported_not_just_the_first()
    {
        var problems = WeatherReadingValidator.Check(Reading(temperature: 95, clouds: 140), null, Now);

        Assert.Equal(["Weather.Temperature", "Weather.Clouds"], problems.Phrases.Select(phrase => phrase!.Key));
    }
}
