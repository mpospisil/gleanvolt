using Gleanvolt.Providers.Client.SolarForecast;
using Gleanvolt.Providers.Contracts.SolarForecast;

namespace Gleanvolt.Plugin.SolarForecastSample;

/// <summary>
/// The same curve every time: a bell from sunrise to sunset, peaking at solar noon at
/// <see cref="PeakShare"/> of the installed kWp, in 30-minute periods from local midnight today for
/// <see cref="Days"/> days. No weather, no randomness: only the day's length moves it.
///
/// <para>Scaled to the kWp in the welcome, so it passes Gleanvolt's peak check on any installation. The
/// sunrise and sunset come from the site's latitude and longitude, accurate to some minutes, which is
/// plenty for a fake; without coordinates the day runs 06:00 to 18:00 by the zone's standard time.</para>
/// </summary>
public sealed class SampleForecast(TimeProvider time) : ISolarForecastSource
{
    /// <summary>Each period's length.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(30);

    /// <summary>How many days, today included.</summary>
    public const int Days = 7;

    /// <summary>The peak as a share of the installed kWp: a clear day, with the usual losses.</summary>
    public const double PeakShare = 0.7;

    /// <summary>The kWp assumed when Gleanvolt does not know the installation's.</summary>
    public const double DefaultKwp = 5;

    public Task<SolarForecastData> GetForecastAsync(PvSite site, CancellationToken cancellationToken) =>
        Task.FromResult(Build(site, time.GetUtcNow()));

    /// <summary>The curve for the days from <paramref name="now"/>'s local midnight.</summary>
    public SolarForecastData Build(PvSite site, DateTimeOffset now)
    {
        var zone = Zone(site.TimeZone);
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var midnight = new DateTimeOffset(localNow.Date, zone.GetUtcOffset(localNow.Date)).ToUniversalTime();
        var peakWatts = (site.InstalledKwp is > 0 and var kwp ? kwp : DefaultKwp) * 1000 * PeakShare;
        var count = (int)(TimeSpan.FromDays(Days) / Period);
        var periods = new List<ForecastPeriod>(count);

        for (var i = 1; i <= count; i++)
        {
            var end = midnight + Period * i;
            var power = Math.Round(PowerAt(end - Period / 2, site, zone, peakWatts));

            periods.Add(new ForecastPeriod(end, Period, power, Math.Round(power * 0.6), Math.Round(power * 1.1)));
        }

        return new SolarForecastData(now, null, periods);
    }

    // sin² over the day: zero at sunrise and sunset, the peak at solar noon.
    private static double PowerAt(DateTimeOffset instant, PvSite site, TimeZoneInfo zone, double peakWatts)
    {
        var (noonUtcHours, halfDayHours) = SolarDay(instant, site, zone);
        if (halfDayHours <= 0)
        {
            return 0;
        }

        // Hours from the solar noon nearest this instant, so a site far from Greenwich wraps correctly.
        var hours = instant.UtcDateTime.TimeOfDay.TotalHours - noonUtcHours;
        hours -= 24 * Math.Round(hours / 24);

        var x = (hours + halfDayHours) / (2 * halfDayHours);
        return x is > 0 and < 1 ? peakWatts * Math.Pow(Math.Sin(Math.PI * x), 2) : 0;
    }

    // Solar noon in UTC hours, and half the day's length in hours.
    private static (double NoonUtcHours, double HalfDayHours) SolarDay(DateTimeOffset instant, PvSite site, TimeZoneInfo zone)
    {
        if (site.Latitude is not { } latitude || site.Longitude is not { } longitude)
        {
            return (12 - zone.BaseUtcOffset.TotalHours, 6);
        }

        var dayOfYear = instant.UtcDateTime.DayOfYear;
        var declination = 23.44 * Math.PI / 180 * Math.Sin(2 * Math.PI * (284 + dayOfYear) / 365);
        var cosHourAngle = Math.Clamp(-Math.Tan(latitude * Math.PI / 180) * Math.Tan(declination), -1, 1);
        var hourAngleDegrees = Math.Acos(cosHourAngle) * 180 / Math.PI;

        return (12 - longitude / 15, hourAngleDegrees / 15);
    }

    private static TimeZoneInfo Zone(string id)
    {
        try
        {
            return string.IsNullOrWhiteSpace(id) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
    }
}
