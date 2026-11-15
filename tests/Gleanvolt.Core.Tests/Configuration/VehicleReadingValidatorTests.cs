using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Tests.Configuration;

/// <summary>What an external car provider may publish (#408): one test per rule.</summary>
public class VehicleReadingValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    private static VehicleState Reading(
        DateTimeOffset? capturedAt = null,
        double? soc = 64,
        double? range = null,
        TimeSpan? remaining = null,
        VehicleChargeState charge = VehicleChargeState.Unknown,
        VehiclePlugState plug = VehiclePlugState.Unknown) =>
        new(capturedAt ?? Now, soc, range, remaining, charge, plug, "provider");

    private static string Only(VehicleState reading, VehicleState? held = null)
    {
        var problems = VehicleReadingValidator.Check(reading, held, Now);
        Assert.Single(problems);
        return problems.Phrases[0]!.Key;
    }

    [Fact]
    public void A_complete_reading_passes()
    {
        var reading = Reading(soc: 64, range: 280_000, remaining: TimeSpan.FromHours(2),
            charge: VehicleChargeState.Charging, plug: VehiclePlugState.Connected);

        Assert.Empty(VehicleReadingValidator.Check(reading, null, Now));
    }

    [Fact]
    public void A_reading_with_one_field_passes() =>
        Assert.Empty(VehicleReadingValidator.Check(Reading(soc: null, plug: VehiclePlugState.Disconnected), null, Now));

    [Fact]
    public void A_capture_time_is_required() =>
        Assert.Equal("Vehicle.NoCaptureTime", Only(Reading(capturedAt: DateTimeOffset.MinValue)));

    [Fact]
    public void Up_to_five_minutes_ahead_is_clock_drift()
    {
        Assert.Empty(VehicleReadingValidator.Check(Reading(capturedAt: Now.AddMinutes(5)), null, Now));
        Assert.Equal("Vehicle.Future", Only(Reading(capturedAt: Now.AddMinutes(5).AddSeconds(1))));
    }

    [Fact]
    public void Older_than_thirty_days_is_refused()
    {
        Assert.Empty(VehicleReadingValidator.Check(Reading(capturedAt: Now.AddDays(-30)), null, Now));
        Assert.Equal("Vehicle.TooOld", Only(Reading(capturedAt: Now.AddDays(-30).AddSeconds(-1))));
    }

    [Fact]
    public void Older_than_the_held_reading_is_refused()
    {
        var held = Reading(capturedAt: Now.AddMinutes(-10));

        Assert.Equal("Vehicle.OlderThanHeld", Only(Reading(capturedAt: Now.AddMinutes(-11)), held));
    }

    [Fact]
    public void The_same_capture_time_as_the_held_reading_is_accepted()
    {
        // A car that has not reported since is a valid answer to Ask the car.
        var held = Reading(capturedAt: Now.AddMinutes(-10));

        Assert.Empty(VehicleReadingValidator.Check(Reading(capturedAt: Now.AddMinutes(-10)), held, Now));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(100.1)]
    [InlineData(double.NaN)]
    public void SOC_is_zero_to_a_hundred(double soc) =>
        Assert.Equal("Vehicle.Soc", Only(Reading(soc: soc)));

    [Theory]
    [InlineData(-1)]
    [InlineData(2_000_001)]
    [InlineData(double.PositiveInfinity)]
    public void Range_is_zero_to_two_thousand_kilometres(double metres) =>
        Assert.Equal("Vehicle.Range", Only(Reading(range: metres)));

    [Theory]
    [InlineData(-1)]
    [InlineData(48 * 60 + 1)]
    public void Time_to_full_is_zero_to_forty_eight_hours(int minutes) =>
        Assert.Equal("Vehicle.TimeRemaining", Only(Reading(remaining: TimeSpan.FromMinutes(minutes))));

    [Fact]
    public void An_unknown_charge_state_is_refused() =>
        Assert.Equal("Vehicle.UnknownChargeState", Only(Reading(charge: (VehicleChargeState)17)));

    [Fact]
    public void An_unknown_plug_state_is_refused() =>
        Assert.Equal("Vehicle.UnknownPlugState", Only(Reading(plug: (VehiclePlugState)17)));

    [Fact]
    public void A_reading_that_says_nothing_is_refused() =>
        Assert.Equal("Vehicle.Empty", Only(Reading(soc: null)));
}
