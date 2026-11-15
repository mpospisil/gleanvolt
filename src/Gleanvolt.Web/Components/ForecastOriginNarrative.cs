using System.Globalization;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Resources;

namespace Gleanvolt.Web.Components;

/// <summary>
/// The line under the Overview's forecast table (#388): where the forecast came from and when —
/// <i>"Forecast from Solcast, received 14:05."</i> — or, when an external provider sent it and has since
/// disconnected, that the forecast is that provider's last. Pure, like <see cref="TariffNarrative"/>.
/// </summary>
public static class ForecastOriginNarrative
{
    /// <summary>The line and whether it is a warning, or null when no forecast is held.</summary>
    /// <param name="forecast">What the store holds.</param>
    /// <param name="externalState">The solar forecast provider's session, or null in a host without one.</param>
    /// <param name="now">The instant the line is about.</param>
    /// <param name="zone">The zone times are shown in: the site's.</param>
    public static (string Text, bool Stale)? Describe(
        SolarForecastSnapshot? forecast, ExternalProviderState? externalState, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        if (forecast is null)
        {
            return null;
        }

        var when = When(forecast.ReceivedAt, now, zone);

        return forecast.Origin.SourceId == SolarForecastSourceIds.External
            && externalState is ExternalProviderState.Disconnected or ExternalProviderState.Waiting
                ? (Localized.Format(DashboardText.ForecastFromDisconnected, forecast.Origin.Name, when), true)
                : (Localized.Format(DashboardText.ForecastFrom, forecast.Origin.Name, when), false);
    }

    // Today's by its time alone, an older one with the day: a forecast days old is worth knowing is.
    private static string When(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;

        return local.ToString(local.Date == today ? "HH:mm" : SharedText.DayTimePattern, CultureInfo.CurrentCulture);
    }
}
