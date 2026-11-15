using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The line under the Overview's forecast table (#388): where the forecast came from and when, and a
/// warning when an external provider sent it and has gone.
/// </summary>
public class ForecastOriginNarrativeTests
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    // Noon in Prague.
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 11, 0, 0, TimeSpan.Zero);

    private static SolarForecastSnapshot Held(string source, string name, DateTimeOffset receivedAt) =>
        new(new SolarForecast(receivedAt, []), new SolarForecastOrigin(source, name), receivedAt);

    [Fact]
    public void NothingHeldIsNothingToSay() =>
        Assert.Null(ForecastOriginNarrative.Describe(null, ExternalProviderState.Off, Now, Prague));

    [Fact]
    public void SolcastTodayByItsTime() =>
        Assert.Equal(
            ("Forecast from Solcast, received 09:30.", false),
            ForecastOriginNarrative.Describe(Held("solcast", "Solcast", Now.AddHours(-2.5)), ExternalProviderState.Off, Now, Prague));

    [Fact]
    public void AnOlderOneWithItsDay() =>
        Assert.Equal(
            ("Forecast from Solcast, received Fri 18:00.", false),
            ForecastOriginNarrative.Describe(Held("solcast", "Solcast", Now.AddDays(-2).AddHours(6)), null, Now, Prague));

    [Fact]
    public void AConnectedProviderByItsName() =>
        Assert.Equal(
            ("Forecast from Solar forecast sample, received 11:00.", false),
            ForecastOriginNarrative.Describe(Held("external", "Solar forecast sample", Now.AddHours(-1)), ExternalProviderState.Connected, Now, Prague));

    [Theory]
    [InlineData(ExternalProviderState.Disconnected)]
    [InlineData(ExternalProviderState.Waiting)]
    public void ADisconnectedProvidersLastForecastIsAWarning(ExternalProviderState state) =>
        Assert.Equal(
            ("The external forecast provider Solar forecast sample is disconnected. This forecast is its last, received 11:00.", true),
            ForecastOriginNarrative.Describe(Held("external", "Solar forecast sample", Now.AddHours(-1)), state, Now, Prague));

    [Fact]
    public void SolcastsForecastIsNotStaleBecauseNoProviderIsConnected() =>
        Assert.False(ForecastOriginNarrative.Describe(Held("solcast", "Solcast", Now), ExternalProviderState.Disconnected, Now, Prague)!.Value.Stale);
}
