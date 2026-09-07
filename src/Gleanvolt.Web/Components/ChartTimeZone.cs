namespace Gleanvolt.Web.Components;

/// <summary>
/// The site's zone, in the one form a browser can read it.
///
/// <para>Shared by the day chart (#173) and the session chart (#175) because both hand it to
/// <c>uPlot.tzDate</c>, and a chart labelled in a different zone from the figures beside it is worse
/// than no chart at all.</para>
/// </summary>
internal static class ChartTimeZone
{
    /// <summary>
    /// The zone as the browser can read it. On Linux — the Pi, the container — <c>Id</c> is already
    /// IANA and this is a no-op; on Windows it is something like "Central Europe Standard Time",
    /// which <c>Intl</c> rejects outright. Where no mapping exists the id is passed through and the
    /// chart falls back to the browser's own zone rather than refusing to draw.
    /// </summary>
    public static string IanaId(TimeZoneInfo zone) =>
        zone.HasIanaId || !TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? zone.Id : iana;
}
