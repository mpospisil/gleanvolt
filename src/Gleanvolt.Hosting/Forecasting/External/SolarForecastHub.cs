using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Hosting.Tariff.External;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.SolarForecast;
using Gleanvolt.Providers.Server;
using CorePeriod = Gleanvolt.Core.Models.SolarForecastPeriod;

namespace Gleanvolt.Hosting.Forecasting.External;

/// <summary>
/// The solar forecast's hub (#388): a provider publishes periods here, and once they pass
/// <see cref="SolarForecastValidator"/> they become the forecast in <see cref="SolarForecastStore"/>,
/// which the planner and every page read. Nothing waits for the provider.
///
/// <para>On while <c>External</c> is the selected forecast source: the selection is the switch.</para>
/// </summary>
public sealed class SolarForecastHub(
    ExternalProviderRegistry registry,
    ProviderHostInfo host,
    SolarForecastStore store,
    PvSystemInfo site,
    TimeProvider time)
    : ProviderHub<ISolarForecastClient, SolarForecastWelcome>(registry, host), ISolarForecastHub
{
    /// <summary>The kind, as registered.</summary>
    public static ProviderKind Kind { get; } = new(
        SolarForecastContract.Kind,
        "Solar forecast",
        SolarForecastContract.Major,
        SolarForecastContract.Minor,
        SolarForecastContract.HubPath,
        services => services.GetRequiredService<SolarForecastSources>().Active?.Id == SolarForecastSourceIds.External);

    /// <summary>The site as a provider is told it.</summary>
    public static PvSite Site(PvSystemInfo site, TimeZoneInfo zone) => new(
        site.Latitude,
        site.Longitude,
        site.PeakPowerWatts is { } watts ? watts / 1000 : null,
        site.AzimuthDegrees,
        site.TiltDegrees,
        TariffHub.IanaId(zone));

    protected override SolarForecastWelcome CreateWelcome() => new() { Site = Site(site, time.LocalTimeZone) };

    public Task<PublishResult> Publish(SolarForecastData data)
    {
        RequireAttached();

        var now = time.GetUtcNow();
        var periods = (data?.Periods ?? [])
            .Where(period => period is not null)
            .Select(period => new CorePeriod(period.End, period.Length, period.PowerWatts, period.PowerWattsP10, period.PowerWattsP90))
            .ToList();

        var problems = SolarForecastValidator.Check(periods, site.PeakPowerWatts, now);

        if (problems.Count > 0)
        {
            Sessions.DataRefused(data?.InReplyTo, problems, problems.Phrases);
            return Task.FromResult(PublishResult.Refused([.. problems]));
        }

        // Its age is the provider's to say, as Solcast's is the fetch's: a forecast it made hours ago
        // is that old, whenever it arrives. One dated ahead of this clock is taken as made now.
        var issuedAt = data!.IssuedAt > now ? now : data.IssuedAt.ToUniversalTime();
        var name = Sessions.Status.ProviderName ?? Sessions.Status.ProviderId ?? "?";

        store.Publish(
            new SolarForecast(issuedAt, [.. periods.Select(period => period with { PeriodEnd = period.PeriodEnd.ToUniversalTime() })]),
            new SolarForecastOrigin(SolarForecastSourceIds.External, name));
        Sessions.DataAccepted(data.InReplyTo);
        return Task.FromResult(PublishResult.Ok);
    }
}
