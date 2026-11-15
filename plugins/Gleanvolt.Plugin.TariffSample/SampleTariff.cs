using Gleanvolt.Providers.Client.Tariff;
using Gleanvolt.Providers.Contracts.Tariff;

namespace Gleanvolt.Plugin.TariffSample;

/// <summary>
/// The same pattern every time: low tariff 22:00–06:00 and 13:00–15:00, high otherwise, for today and
/// tomorrow in the installation's time zone (from the welcome), so the windows always cover the moment
/// they are sent. Prices are fixed, in the installation's currency, when <see cref="WithPrices"/> is on.
/// </summary>
public sealed class SampleTariff(TimeProvider time) : ITariffSource
{
    private static readonly (TimeOnly Start, TimeOnly End)[] LowPeriods =
    [
        (new TimeOnly(0, 0), new TimeOnly(6, 0)),
        (new TimeOnly(13, 0), new TimeOnly(15, 0)),
        (new TimeOnly(22, 0), TimeOnly.MaxValue),
    ];

    /// <summary>Whether windows carry their own price: 2.10 low, 4.80 high.</summary>
    public bool WithPrices { get; set; }

    public Task<TariffData> GetTariffAsync(TariffWelcome site, CancellationToken cancellationToken) =>
        Task.FromResult(Build(site, time.GetUtcNow()));

    /// <summary>The pattern for today and tomorrow around <paramref name="now"/>.</summary>
    public TariffData Build(TariffWelcome site, DateTimeOffset now)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(site.TimeZone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var windows = new List<TariffWindow>();

        foreach (var day in new[] { today, today.AddDays(1) })
        {
            var cursor = Utc(day, TimeOnly.MinValue, zone);

            foreach (var (start, end) in LowPeriods)
            {
                var lowStart = Utc(day, start, zone);
                var lowEnd = end == TimeOnly.MaxValue ? Utc(day.AddDays(1), TimeOnly.MinValue, zone) : Utc(day, end, zone);

                if (lowStart > cursor)
                {
                    windows.Add(Window(cursor, lowStart, TariffRate.High));
                }

                windows.Add(Window(lowStart, lowEnd, TariffRate.Low));
                cursor = lowEnd;
            }
        }

        // 22:00–24:00 and the next 00:00–06:00 are one low window; Gleanvolt would join them too.
        return new TariffData(now, null, site.Currency, Join(windows));
    }

    private TariffWindow Window(DateTimeOffset start, DateTimeOffset end, TariffRate rate) =>
        new(start, end, rate, WithPrices ? (rate == TariffRate.Low ? 2.10m : 4.80m) : null);

    private static List<TariffWindow> Join(List<TariffWindow> windows)
    {
        var joined = new List<TariffWindow>();

        foreach (var window in windows)
        {
            if (joined.Count > 0 && joined[^1].Rate == window.Rate && joined[^1].PricePerKwh == window.PricePerKwh)
            {
                joined[^1] = joined[^1] with { End = window.End };
            }
            else
            {
                joined.Add(window);
            }
        }

        return joined;
    }

    private static DateTimeOffset Utc(DateOnly day, TimeOnly time, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(time);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
