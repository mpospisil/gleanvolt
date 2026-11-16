namespace Gleanvolt.Providers.Contracts.SolarForecast;

/// <summary>The solar forecast kind's contract version, and where its hub is (#388).</summary>
public static class SolarForecastContract
{
    /// <summary>The kind's id, also the token's kind.</summary>
    public const string Kind = "solar-forecast";

    /// <summary>Changes only when an older provider would be misread.</summary>
    public const int Major = 1;

    /// <summary>Grows with every addition an older provider can ignore.</summary>
    public const int Minor = 2;

    /// <summary>The hub's path on Gleanvolt's web port.</summary>
    public const string HubPath = "/providers/solar-forecast";
}

/// <summary>
/// The solar forecast hub: a provider publishes the expected PV power. Gleanvolt keeps the last accepted
/// forecast and plans on it, connected or not, until it is replaced.
/// </summary>
public interface ISolarForecastHub : IProviderHub<SolarForecastWelcome>
{
    /// <summary>
    /// Replaces the forecast Gleanvolt holds, after it passes every check; on any problem nothing
    /// changes and the problems come back.
    /// </summary>
    Task<PublishResult> Publish(SolarForecastData data);
}

/// <summary>What the solar forecast hub may send its provider.</summary>
public interface ISolarForecastClient : IProviderClient
{
    /// <summary>
    /// Gleanvolt would like a fresh forecast. Answer with <see cref="ISolarForecastHub.Publish"/>,
    /// quoting <see cref="RefreshRequest.Id"/> in <see cref="SolarForecastData.InReplyTo"/>.
    /// </summary>
    Task RefreshRequested(RefreshRequest request);

    /// <summary>
    /// The installation changed while the session was attached. The forecast is for
    /// <paramref name="site"/> from now on; publishing a new one is up to the provider.
    /// </summary>
    Task SiteChanged(PvSite site);
}

/// <summary>An accepted solar forecast hello: the installation to forecast for, so it is not configured twice.</summary>
public sealed record SolarForecastWelcome : Welcome
{
    /// <summary>The installation as Gleanvolt knows it.</summary>
    public PvSite Site { get; init; } = new(null, null, null, null, null, string.Empty);
}

/// <summary>
/// The PV installation as Gleanvolt knows it. Anything the owner has not entered is null: a provider
/// that needs it should say so through <see cref="IProviderHub{TWelcome}.ReportStatus"/>.
/// </summary>
/// <param name="Latitude">Decimal degrees, north positive.</param>
/// <param name="Longitude">Decimal degrees, east positive.</param>
/// <param name="InstalledKwp">The array's peak DC power, in kWp.</param>
/// <param name="AzimuthDegrees">The compass bearing the array faces: 0 north, 90 east, 180 south, 270 west.</param>
/// <param name="TiltDegrees">From horizontal: 0 flat, 90 vertical.</param>
/// <param name="TimeZone">The installation's IANA time zone, e.g. "Europe/Prague".</param>
public sealed record PvSite(
    double? Latitude,
    double? Longitude,
    double? InstalledKwp,
    double? AzimuthDegrees,
    double? TiltDegrees,
    string TimeZone);

/// <summary>
/// The expected average PV power over one stretch, half-open at the start: from <c>End - Length</c>,
/// exclusive, up to and including <see cref="End"/>. Stamped by its end, as Solcast stamps its periods.
/// </summary>
/// <param name="End">In UTC.</param>
/// <param name="Length">From 5 to 60 minutes, e.g. "00:30:00" on the wire.</param>
/// <param name="PowerWatts">The median (P50) estimate of the average power, in W.</param>
/// <param name="PowerWattsP10">The 10th percentile, in W, or null. At most <paramref name="PowerWatts"/>.</param>
/// <param name="PowerWattsP90">The 90th percentile, in W, or null. At least <paramref name="PowerWatts"/>.</param>
public sealed record ForecastPeriod(
    DateTimeOffset End,
    TimeSpan Length,
    double PowerWatts,
    double? PowerWattsP10 = null,
    double? PowerWattsP90 = null);

/// <summary>A solar forecast.</summary>
/// <param name="IssuedAt">When the provider made it. Gleanvolt judges the forecast's age by it.</param>
/// <param name="InReplyTo">The request this answers, or null when sent unprompted.</param>
/// <param name="Periods">
/// In time order, none overlapping the one before, reaching at most 14 days ahead. Gaps are allowed: a
/// provider that leaves out the night may.
/// </param>
public sealed record SolarForecastData(
    DateTimeOffset IssuedAt,
    RequestId? InReplyTo,
    IReadOnlyList<ForecastPeriod> Periods);
