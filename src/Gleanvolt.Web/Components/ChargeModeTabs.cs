using Gleanvolt.Core.Enums;

namespace Gleanvolt.Web.Components;

/// <summary>
/// The charge modes as the web UI names them: the Charging plan page's tabs, and the overview's mode
/// strip that links into them (#255). One table, so a mode cannot be called one thing on one page and
/// another on the next.
/// </summary>
public static class ChargeModeTabs
{
    /// <summary>One mode: its tab's URL slug, its label, and what it does in one sentence.</summary>
    public sealed record Entry(string Slug, string Label, ChargeControlMode Mode, string Summary);

    public const string SolarSlug = "solar";
    public const string ForecastedSlug = "forecasted";
    public const string SolarGridSlug = "solar-grid";
    public const string FastSlug = "fast";
    public const string TargetedSlug = "targeted";

    /// <summary>
    /// In the order they are worth reaching for: the two everyday solar modes first, then the one that
    /// follows the sun but lets the grid help, then the two that buy from the grid when they have to.
    /// The summaries are each tab's own opening paragraph, cut to its first claim.
    /// </summary>
    public static IReadOnlyList<Entry> All { get; } =
    [
        new(SolarSlug, "Solar", ChargeControlMode.Solar,
            "Charges from the sun the house isn't using, once the home battery is full."),
        new(ForecastedSlug, "Forecasted", ChargeControlMode.Forecasted,
            "Charges from surplus like Solar, but the forecast decides how much of today's sun the car may have, so the home battery still reaches 100% by the evening."),
        new(SolarGridSlug, "Solar + grid", ChargeControlMode.SolarGrid,
            "Follows the sun, and lets the grid top the charger up to its minimum when the surplus alone is too small."),
        new(FastSlug, "Fast (no battery)", ChargeControlMode.FastNoBattery,
            "Charges as fast as the installation allows, from the sun and the grid, with the home battery kept out of it."),
        new(TargetedSlug, "Targeted", ChargeControlMode.Targeted,
            "Delivers a set amount by a set time: forecast surplus first, the rest bought from the grid as late as it can."),
    ];

    /// <summary>The entry for <paramref name="mode"/>, or null for Off, which has no tab.</summary>
    public static Entry? For(ChargeControlMode mode) => All.FirstOrDefault(entry => entry.Mode == mode);

    /// <summary>The mode's name as the UI shows it; "Off" for Off.</summary>
    public static string Label(ChargeControlMode mode) => For(mode)?.Label ?? mode.ToString();
}
