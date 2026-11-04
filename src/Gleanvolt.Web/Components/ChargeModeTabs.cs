using Gleanvolt.Core.Enums;
using Gleanvolt.Web.Resources;

namespace Gleanvolt.Web.Components;

/// <summary>
/// The charge modes as the web UI names them: the Charging plan page's tabs, and the overview's mode
/// strip that links into them (#255). One table, so a mode cannot be called one thing on one page and
/// another on the next.
/// </summary>
public static class ChargeModeTabs
{
    /// <summary>
    /// One mode: its tab's URL slug, its label, and what it does in one sentence. The label and the
    /// summary are read in the viewer's language each time (#290), which is why they are properties
    /// rather than values captured when the table was built.
    /// </summary>
    public sealed record Entry(string Slug, ChargeControlMode Mode)
    {
        public string Label => Label(Mode);

        public string Summary => Mode switch
        {
            ChargeControlMode.Solar => SharedText.ModeSolarSummary,
            ChargeControlMode.Forecasted => SharedText.ModeForecastedSummary,
            ChargeControlMode.SolarGrid => SharedText.ModeSolarGridSummary,
            ChargeControlMode.FastNoBattery => SharedText.ModeFastSummary,
            ChargeControlMode.Targeted => SharedText.ModeTargetedSummary,
            _ => string.Empty,
        };
    }

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
        new(SolarSlug, ChargeControlMode.Solar),
        new(ForecastedSlug, ChargeControlMode.Forecasted),
        new(SolarGridSlug, ChargeControlMode.SolarGrid),
        new(FastSlug, ChargeControlMode.FastNoBattery),
        new(TargetedSlug, ChargeControlMode.Targeted),
    ];

    /// <summary>The entry for <paramref name="mode"/>, or null for Off, which has no tab.</summary>
    public static Entry? For(ChargeControlMode mode) => All.FirstOrDefault(entry => entry.Mode == mode);

    /// <summary>The mode's name as the UI shows it, in the viewer's language.</summary>
    public static string Label(ChargeControlMode mode) => mode switch
    {
        ChargeControlMode.Off => SharedText.ModeOff,
        ChargeControlMode.Solar => SharedText.ModeSolar,
        ChargeControlMode.Forecasted => SharedText.ModeForecasted,
        ChargeControlMode.SolarGrid => SharedText.ModeSolarGrid,
        ChargeControlMode.FastNoBattery => SharedText.ModeFast,
        ChargeControlMode.Targeted => SharedText.ModeTargeted,
        _ => mode.ToString(),
    };
}
