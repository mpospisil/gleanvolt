using Gleanvolt.Core.Models;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// A session's weather in one line (#424): an external provider may leave figures out, and a figure it
/// didn't report is left out of the line rather than shown as a dash or a zero.
/// </summary>
public class SessionWeatherLineTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EveryFigureReadsAsBefore() =>
        Assert.Equal("light rain, 14.1 °C, 88% cloud",
            SessionFormatting.FormatWeather(new WeatherObservation(At, 14.1, 100_900, 76, 88, 4_000, "Rain", "light rain")));

    [Fact]
    public void CloudsLeftOutAreLeftOutOfTheLine() =>
        Assert.Equal("3.5 °C",
            SessionFormatting.FormatWeather(new WeatherObservation(At, 3.5, null, 88, null, null, string.Empty, string.Empty)));

    [Fact]
    public void AConditionAloneIsTheLine() =>
        Assert.Equal("Snow",
            SessionFormatting.FormatWeather(new WeatherObservation(At, null, null, null, null, null, "Snow", string.Empty)));

    [Fact]
    public void ZeroCloudIsShownNotTakenForMissing() =>
        Assert.Equal("Clear, 20.0 °C, 0% cloud",
            SessionFormatting.FormatWeather(new WeatherObservation(At, 20, null, null, 0, null, "Clear", string.Empty)));
}
