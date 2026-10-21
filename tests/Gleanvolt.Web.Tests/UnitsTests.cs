using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The one formatter for every unit the web UI shows (#263): where it switches scale, how it
/// rounds, and that a missing value and a negative one each read one way everywhere.
/// </summary>
public class UnitsTests
{
    [Theory]
    [InlineData(0, "0 W")]
    [InlineData(850, "850 W")]
    [InlineData(999.4, "999 W")]
    [InlineData(999.6, "1.0 kW")] // decided on the rounded figure: never "1000 W"
    [InlineData(1000, "1.0 kW")]
    [InlineData(4140, "4.1 kW")]
    [InlineData(4150, "4.2 kW")] // away from zero, as a person rounds
    [InlineData(10_449, "10.4 kW")]
    [InlineData(-850, "-850 W")]
    [InlineData(-3100, "-3.1 kW")]
    [InlineData(-0.4, "0 W")] // a rounded zero has no direction
    public void Power_scales_from_watts_to_kilowatts_at_a_thousand(double watts, string expected) =>
        Assert.Equal(expected, Units.Power(watts));

    [Theory]
    [InlineData(1200, "+1.2 kW")]
    [InlineData(-1200, "-1.2 kW")]
    [InlineData(0.3, "0 W")]
    [InlineData(-40, "-40 W")]
    public void Signed_power_marks_the_positive_direction_too(double watts, string expected) =>
        Assert.Equal(expected, Units.Power(watts, signed: true));

    [Theory]
    [InlineData(850, "850 Wh")]
    [InlineData(1234, "1.2 kWh")]
    [InlineData(12_345, "12.3 kWh")]
    [InlineData(-1500, "-1.5 kWh")]
    public void Energy_scales_from_watt_hours_to_kilowatt_hours_at_a_thousand(double wattHours, string expected) =>
        Assert.Equal(expected, Units.Energy(wattHours));

    [Fact]
    public void Energy_can_be_signed() => Assert.Equal("+2.5 kWh", Units.Energy(2500, signed: true));

    [Fact]
    public void Every_quantity_reads_a_missing_value_as_a_dash_never_a_zero()
    {
        Assert.All(
            new[]
            {
                Units.Power(null), Units.Watts(null), Units.Kw(null), Units.PeakPower(null),
                Units.Energy(null), Units.Kwh(null), Units.Capacity(null), Units.Duration(null),
                Units.Percent(null), Units.Fraction(null), Units.Current(null), Units.Temperature(null),
                Units.Distance(null), Units.Angle(null),
            },
            text => Assert.Equal(Units.Missing, text));
    }

    [Theory]
    [InlineData(850, 1, true, "0.9 kWh")]
    [InlineData(12_345, 2, true, "12.35 kWh")]
    [InlineData(12_345, 2, false, "12.35")]
    [InlineData(1_234_567, 1, false, "1,234.6")]
    public void Kwh_stays_in_kilowatt_hours_for_a_column_that_must_not_mix_units(
        double wattHours, int decimals, bool unit, string expected) =>
        Assert.Equal(expected, Units.Kwh(wattHours, decimals, unit));

    [Fact]
    public void Fixed_units_take_their_own_precision()
    {
        Assert.Equal("1,234 W", Units.Watts(1234.4));
        Assert.Equal("1,234", Units.Watts(1234.4, unit: false));
        Assert.Equal("4.14 kW", Units.Kw(4140, 2));
        Assert.Equal("0.0 kW", Units.Kw(-20)); // no "-0.0"
    }

    [Theory]
    [InlineData(8400, "8.4 kWp")]
    [InlineData(10_000, "10 kWp")]
    [InlineData(9_125, "9.13 kWp")]
    public void Peak_power_is_shown_as_specified(double watts, string expected) =>
        Assert.Equal(expected, Units.PeakPower(watts));

    [Theory]
    [InlineData(77_000, "77 kWh")]
    [InlineData(52_500, "52.5 kWh")]
    public void A_capacity_is_not_padded(double wattHours, string expected) =>
        Assert.Equal(expected, Units.Capacity(wattHours));

    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(-30, "0 s")]
    [InlineData(45, "45 s")]
    [InlineData(59.6, "1 min")]
    [InlineData(90, "1 min 30 s")]
    [InlineData(300, "5 min")]
    [InlineData(12 * 60 + 29, "12 min")]
    [InlineData(59 * 60 + 40, "1 h")]
    [InlineData(65 * 60, "1 h 05 min")]
    [InlineData(12 * 3600, "12 h")]
    [InlineData(47 * 3600 + 59 * 60, "47 h 59 min")]
    [InlineData(48 * 3600, "2 days")]
    [InlineData(84 * 3600, "3.5 days")]
    public void Duration_uses_the_two_largest_units_that_say_something(double seconds, string expected) =>
        Assert.Equal(expected, Units.Duration(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(-5, "just now")]
    [InlineData(30, "under a minute")]
    [InlineData(25 * 60, "25 min")]
    [InlineData(3.5 * 3600, "3.5 h")]
    [InlineData(50 * 3600, "2.1 days")]
    public void Age_is_coarse(double seconds, string expected) =>
        Assert.Equal(expected, Units.Age(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Percent_and_fraction_read_the_same()
    {
        Assert.Equal("64%", Units.Percent(64.4));
        Assert.Equal("64.4%", Units.Percent(64.4, 1));
        Assert.Equal("92%", Units.Fraction(0.92));
    }

    [Fact]
    public void Other_quantities_have_one_rendering_each()
    {
        Assert.Equal("16 A", Units.Current(16));
        Assert.Equal("6.5 A", Units.Current(6.5));
        Assert.Equal("21.4 °C", Units.Temperature(21.35));
        Assert.Equal("312 km", Units.Distance(312_400));
        Assert.Equal("172°", Units.Angle(172));
        Assert.Equal("48 h", Units.Hours(TimeSpan.FromHours(48)));
    }

    [Fact]
    public void Input_converts_back_to_the_base_unit()
    {
        Assert.Equal(20_000, Units.WattHoursFromKwh(20));
        Assert.Equal(20, Units.KwhFromWattHours(20_000));
        Assert.Null(Units.WattHoursFromKwh((double?)null));
        Assert.Equal(8_400, Units.WattsFromKw(8.4)!.Value, 6);
        Assert.Equal(312_000, Units.MetresFromKm(312));
    }
}
