using System.Globalization;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Web.Components;

/// <summary>
/// The overview's tariff line (issue #265): the rate now, its price, and when it changes —
/// <i>"Low tariff, 2.10 CZK/kWh, until 06:00"</i>. Pure and separate from the page, like
/// <see cref="OverviewNarrative"/>: the wording is the part worth testing.
/// </summary>
public static class TariffNarrative
{
    /// <summary>
    /// The line, or null when there is nothing to say: no tariff selected, which is not news on a page
    /// that is about the car. A selected tariff that cannot answer is news, and comes back as its reason.
    /// </summary>
    /// <param name="timeline">The schedule, or null for no restriction.</param>
    /// <param name="unavailable">Why a selected strategy has no schedule, or null.</param>
    /// <param name="currency">What prices are in.</param>
    /// <param name="now">The instant the line is about.</param>
    /// <param name="zone">The zone times are shown in — the site's, never the browser's.</param>
    public static string? Describe(
        TariffTimeline? timeline, string? unavailable, string currency, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        if (timeline?.WindowAt(now) is not { } window)
        {
            return unavailable;
        }

        var parts = new List<string> { window.Rate == TariffRate.Low ? "Low tariff" : "High tariff" };

        if (timeline.PriceAt(now) is { } price)
        {
            parts.Add(Units.PricePerKwh(price, currency));
        }

        var line = string.Join(", ", parts);

        return timeline.NextChangeAfter(now) is { } change ? $"{line}, until {When(change, now, zone)}" : line;
    }

    // Today's change by its time alone; a later one with the day, since "until 06:00" said on a
    // Saturday afternoon would be read as tomorrow morning when it may be Monday's.
    private static string When(DateTimeOffset change, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(change, zone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var day = DateOnly.FromDateTime(local.DateTime);
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);

        return day == today ? time
            : day == today.AddDays(1) ? $"tomorrow {time}"
            : $"{local.ToString("dddd", CultureInfo.InvariantCulture)} {time}";
    }
}
