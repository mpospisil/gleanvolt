using System.Globalization;
using Gleanvolt.Web.Resources;

namespace Gleanvolt.Web.Components;

/// <summary>
/// The words and formats a chart needs in the viewer's language (#290), handed to charts.js as the
/// last argument of every render call. The charts draw on a canvas, outside anything Razor renders,
/// so their text has to travel with the data.
/// </summary>
/// <param name="Locale">The culture's name, for <c>toLocaleString</c>; never empty.</param>
/// <param name="Series">Each series' legend name, by the key charts.js asks for.</param>
/// <param name="DateNames">Month and day names in uPlot's shape (Sunday first, as it counts).</param>
/// <param name="DayMonth">A uPlot template for a tick on a new day: "{D} {MMM}".</param>
/// <param name="LegendTime">A uPlot template for the time in the legend under the cursor.</param>
internal sealed record ChartText(
    string Locale,
    IReadOnlyDictionary<string, string> Series,
    IReadOnlyDictionary<string, string[]> DateNames,
    string DayMonth,
    string LegendTime)
{
    /// <summary>The viewer's, read when called: the culture is the circuit's, so build it per render.</summary>
    public static ChartText Current()
    {
        var culture = CultureInfo.CurrentCulture;
        var dates = culture.DateTimeFormat;

        return new(
            string.IsNullOrEmpty(culture.Name) ? "en" : culture.Name,
            new Dictionary<string, string>
            {
                ["solar"] = HistoryText.SeriesSolar,
                ["forecast"] = HistoryText.SeriesForecast,
                ["grid"] = HistoryText.SeriesGrid,
                ["battery"] = HistoryText.SeriesBattery,
                ["ev"] = HistoryText.SeriesEv,
                ["house"] = HistoryText.SeriesHouse,
                ["batterySoc"] = HistoryText.SeriesBatterySoc,
                ["surplus"] = HistoryText.SeriesSurplus,
                ["batteryLoan"] = HistoryText.SeriesBatteryLoan,
                ["socFloor"] = HistoryText.SeriesSocFloor,
                ["carSoc"] = HistoryText.SeriesCarSoc,
                ["expected"] = HistoryText.SeriesExpected,
                ["low"] = HistoryText.SeriesLow,
                ["high"] = HistoryText.SeriesHigh,
                ["forecastSurplus"] = HistoryText.SeriesForecastSurplus,
                ["requiredFloor"] = HistoryText.SeriesRequiredFloor,
            },
            new Dictionary<string, string[]>
            {
                // Genitive where the language has one: "4. listopadu", not "4. listopad".
                ["MMMM"] = dates.MonthGenitiveNames[..12],
                ["MMM"] = dates.AbbreviatedMonthGenitiveNames[..12],
                ["WWWW"] = dates.DayNames,
                ["WWW"] = dates.AbbreviatedDayNames,
            },
            SharedText.ChartDayMonthTemplate,
            SharedText.ChartLegendTimeTemplate);
    }
}
