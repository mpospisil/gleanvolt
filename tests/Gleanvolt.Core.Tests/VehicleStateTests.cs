using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Tests;

public class VehicleStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AgeAt_MeasuresFromTheCarsCaptureTime()
    {
        var state = new VehicleState(Now.AddHours(-3.5), SocPercent: 28);

        Assert.Equal(TimeSpan.FromHours(3.5), state.AgeAt(Now));
    }

    [Fact]
    public void AgeAt_IsNegativeWhenTheSourcesClockRunsAhead()
    {
        var state = new VehicleState(Now.AddMinutes(5));

        Assert.True(state.AgeAt(Now) < TimeSpan.Zero);
    }

    [Fact]
    public void IsStaleAt_TreatsAClockAheadOfOursAsFreshRatherThanAnError()
    {
        var state = new VehicleState(Now.AddMinutes(5));

        Assert.False(state.IsStaleAt(Now, TimeSpan.FromHours(12)));
    }

    [Theory]
    [InlineData(3.5, false)]  // hours old and parked: accurate, not stale
    [InlineData(11.9, false)]
    [InlineData(12.1, true)]  // past the guard: the feed is suspect
    [InlineData(48, true)]
    public void IsStaleAt_ComparesTheAgeToMaxAge(double ageHours, bool expected)
    {
        var state = new VehicleState(Now.AddHours(-ageHours));

        Assert.Equal(expected, state.IsStaleAt(Now, TimeSpan.FromHours(12)));
    }

    [Fact]
    public void EverythingButTheCaptureTimeIsOptional()
    {
        // The contract that makes a second car free: a source reporting only a timestamp is valid, and
        // absent means null or Unknown -- never zero, which would read as a flat battery.
        var state = new VehicleState(Now);

        Assert.Null(state.SocPercent);
        Assert.Null(state.SourceId);
        Assert.Equal(VehicleChargeState.Unknown, state.ChargeState);
        Assert.Equal(VehiclePlugState.Unknown, state.PlugState);
    }
}
