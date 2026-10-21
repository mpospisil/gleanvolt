using System.Globalization;

namespace Gleanvolt.Web.Components;

/// <summary>
/// Every unit the web UI shows or reads back, in one place (issue #263).
///
/// <para>Below the UI each quantity has one base unit, named in its suffix: power in W
/// (<c>…Watts</c>), energy in Wh (<c>…Wh</c>), durations as <see cref="TimeSpan"/>, percentages
/// 0–100 (<c>…Percent</c>), fractions 0–1, temperature in °C, distance in metres, current in A.
/// This class is the only place that scales one for a person to read, so no component divides by
/// a thousand or appends a unit itself, and two pages never round the same figure differently.</para>
///
/// <para>The rules: power and energy scale themselves, below 1000 in W/Wh as whole numbers and
/// from there in kW/kWh to one decimal. The switch is decided on the rounded figure, so 999.6 W
/// reads "1.0 kW", never "1000 W". A missing value is always <see cref="Missing"/>, never a zero:
/// absent and zero are different facts. A rounded zero never carries a sign. Numbers are formatted
/// by <see cref="CultureInfo.CurrentCulture"/>, which <see cref="CultureConfiguration"/> pins to
/// invariant, so a locale switch later is one change there.</para>
///
/// <para>Where a table states the unit in its header, the fixed-unit forms (<see cref="Kwh"/>,
/// <see cref="Kw"/>, <see cref="Watts"/>) take <c>unit: false</c> and a precision of the table's
/// own, so a column never mixes Wh with kWh.</para>
/// </summary>
internal static class Units
{
    /// <summary>What a value that is not there reads as, everywhere.</summary>
    public const string Missing = "—";

    private const double Kilo = 1000;

    // ---- Power (base: W) ----

    /// <summary>
    /// A power, auto-scaled: "850 W", "4.1 kW". <paramref name="signed"/> puts a "+" on a positive
    /// figure, for the flows whose direction is the point (grid import/export, battery charge/discharge).
    /// </summary>
    public static string Power(double? watts, bool signed = false) =>
        watts is { } value ? Scaled(value, "W", "kW", signed) : Missing;

    /// <summary>A power always in whole watts: "1,234 W".</summary>
    public static string Watts(double? watts, bool unit = true) =>
        watts is { } value ? Fixed(value, 0, unit ? "W" : null) : Missing;

    /// <summary>A power always in kilowatts: "4.1 kW".</summary>
    public static string Kw(double? watts, int decimals = 1, bool unit = true) =>
        watts is { } value ? Fixed(value / Kilo, decimals, unit ? "kW" : null) : Missing;

    /// <summary>
    /// A PV array's peak DC power, in kWp to as many decimals as it was given (up to two): an
    /// array is sized in kWp, and "8.4 kWp" is the figure on its datasheet.
    /// </summary>
    public static string PeakPower(double? watts) =>
        watts is { } value ? $"{Trimmed(value / Kilo)} kWp" : Missing;

    /// <summary>
    /// A power rating, in kW to as many decimals as it was given (up to two): "8 kW", "4.6 kW". A
    /// rating is a specification, not a measurement, so it is not padded to a fixed precision.
    /// </summary>
    public static string Rating(double? watts) =>
        watts is { } value ? $"{Trimmed(value / Kilo)} kW" : Missing;

    // ---- Energy (base: Wh) ----

    /// <summary>An energy, auto-scaled: "850 Wh", "12.3 kWh".</summary>
    public static string Energy(double? wattHours, bool signed = false) =>
        wattHours is { } value ? Scaled(value, "Wh", "kWh", signed) : Missing;

    /// <summary>An energy always in kilowatt-hours: "12.3 kWh", or "12.34" in a kWh column.</summary>
    public static string Kwh(double? wattHours, int decimals = 1, bool unit = true) =>
        wattHours is { } value ? Fixed(value / Kilo, decimals, unit ? "kWh" : null) : Missing;

    /// <summary>
    /// A battery's size, in kWh to as many decimals as it was given (up to two): "77 kWh", "52.5 kWh".
    /// A capacity is a specification, not a measurement, so it is not padded to a fixed precision.
    /// </summary>
    public static string Capacity(double? wattHours) =>
        wattHours is { } value ? $"{Trimmed(value / Kilo)} kWh" : Missing;

    // ---- Time (base: TimeSpan) ----

    /// <summary>
    /// A span, in the two largest units that say something: "45 s", "1 min 30 s", "12 min",
    /// "1 h 05 min", "2 h", "3 days". Seconds are dropped from ten minutes up and minutes from two
    /// days up, where they are noise. A negative span reads as zero.
    /// </summary>
    public static string Duration(TimeSpan? span)
    {
        if (span is not { } value)
        {
            return Missing;
        }

        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        var seconds = (long)Math.Round(value.TotalSeconds, MidpointRounding.AwayFromZero);

        if (seconds < 60)
        {
            return $"{seconds} s";
        }

        if (seconds < 10 * 60)
        {
            var (m, s) = (seconds / 60, seconds % 60);
            return s == 0 ? $"{m} min" : $"{m} min {s} s";
        }

        var minutes = (long)Math.Round(value.TotalMinutes, MidpointRounding.AwayFromZero);

        if (minutes < 60)
        {
            return $"{minutes} min";
        }

        if (minutes < 48 * 60)
        {
            var (h, m) = (minutes / 60, minutes % 60);
            return m == 0 ? $"{h} h" : $"{h} h {m:00} min";
        }

        return $"{Trimmed(value.TotalDays, "0.#")} days";
    }

    /// <summary>
    /// How old a reading is, coarse on purpose: "under a minute", "12 min", "3.5 h", "2.0 days".
    /// For sources that are routinely hours old, where minutes of precision would claim an accuracy
    /// they do not have. A negative age (the source's clock running ahead of ours) reads "just now".
    /// </summary>
    public static string Age(TimeSpan age) => age switch
    {
        _ when age < TimeSpan.Zero => "just now",
        _ when age < TimeSpan.FromMinutes(1) => "under a minute",
        _ when age < TimeSpan.FromHours(1) => $"{Number(age.TotalMinutes, 0)} min",
        _ when age < TimeSpan.FromDays(1) => $"{Number(age.TotalHours, 1)} h",
        _ => $"{Number(age.TotalDays, 1)} days",
    };

    /// <summary>A span in whole hours, for limits configured in hours: "48 h".</summary>
    public static string Hours(TimeSpan span) => $"{Number(span.TotalHours, 0)} h";

    // ---- Ratios (base: percent 0–100, or a fraction 0–1 where the name says so) ----

    /// <summary>A percentage given as 0–100: "64%".</summary>
    public static string Percent(double? percent, int decimals = 0) =>
        percent is { } value ? $"{Number(value, decimals)}%" : Missing;

    /// <summary>A fraction given as 0–1, shown as a percentage: 0.92 reads "92%".</summary>
    public static string Fraction(double? fraction, int decimals = 0) =>
        Percent(fraction * 100, decimals);

    // ---- Money (base: Tariff:Currency) ----

    /// <summary>
    /// A grid price per kWh, to the hundredth: "2.10 CZK/kWh". The currency is the installation's
    /// configured code, so it is passed rather than assumed.
    /// </summary>
    public static string PricePerKwh(decimal? price, string currency) =>
        price is { } value
            ? $"{Math.Round(value, 2, MidpointRounding.AwayFromZero).ToString("N2", CultureInfo.CurrentCulture)} {currency}/kWh"
            : Missing;

    /// <summary>The unit a price is typed in, for a field's label: "CZK/kWh".</summary>
    public static string PriceUnit(string currency) => $"{currency}/kWh";

    // ---- Everything else ----

    /// <summary>A current, to the amp's tenth where it has one: "16 A", "6.5 A".</summary>
    public static string Current(double? amps) =>
        amps is { } value ? $"{Trimmed(value, "0.#")} A" : Missing;

    /// <summary>A temperature, to a tenth of a degree: "21.4 °C".</summary>
    public static string Temperature(double? celsius) =>
        celsius is { } value ? $"{Number(value, 1)} °C" : Missing;

    /// <summary>
    /// A distance in whole kilometres: "312 km". Only a car's range and odometer are shown, and
    /// both are the car's own estimates, so a decimal would be borrowed confidence.
    /// </summary>
    public static string Distance(double? metres) =>
        metres is { } value ? $"{Number(value / Kilo, 0)} km" : Missing;

    /// <summary>An angle, to a tenth of a degree where it has one: "172°".</summary>
    public static string Angle(double? degrees) =>
        degrees is { } value ? $"{Trimmed(value, "0.#")}°" : Missing;

    // ---- Reading the owner's input back (display unit to base unit) ----

    /// <summary>Kilowatt-hours as typed into a form, in the base unit.</summary>
    public static double WattHoursFromKwh(double kwh) => kwh * Kilo;

    /// <inheritdoc cref="WattHoursFromKwh(double)"/>
    public static double? WattHoursFromKwh(double? kwh) => kwh * Kilo;

    /// <summary>A base-unit energy as a form shows it for editing, in kilowatt-hours.</summary>
    public static double KwhFromWattHours(double wattHours) => wattHours / Kilo;

    // ---- The formatting itself ----

    private static string Scaled(double value, string unit, string kiloUnit, bool signed)
    {
        var whole = Round(value, 0);

        return Math.Abs(whole) < Kilo
            ? $"{Sign(whole, signed)}{Number(whole, 0)} {unit}"
            : Fixed(value / Kilo, 1, kiloUnit, signed);
    }

    private static string Fixed(double value, int decimals, string? unit, bool signed = false)
    {
        var rounded = Round(value, decimals);
        var text = $"{Sign(rounded, signed)}{Number(rounded, decimals)}";

        return unit is null ? text : $"{text} {unit}";
    }

    private static string Sign(double rounded, bool signed) => signed && rounded > 0 ? "+" : "";

    // Rounded away from zero, as a person rounds, and with a rounded zero's sign dropped: "-0.0 kW"
    // would claim a direction for a flow too small to have one.
    private static double Round(double value, int decimals)
    {
        var rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        return rounded == 0 ? 0 : rounded;
    }

    private static string Number(double value, int decimals) =>
        Round(value, decimals).ToString($"N{decimals}", CultureInfo.CurrentCulture);

    private static string Trimmed(double value, string format = "#,0.##")
    {
        var rounded = Round(value, format.Length - format.IndexOf('.') - 1);
        return rounded.ToString(format, CultureInfo.CurrentCulture);
    }
}
