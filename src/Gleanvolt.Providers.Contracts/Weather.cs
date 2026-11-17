namespace Gleanvolt.Providers.Contracts.Weather;

/// <summary>The weather kind: its contract version, and where its hub is (#424).</summary>
public static class WeatherContract
{
    /// <summary>The kind's id.</summary>
    public const string Kind = "weather";

    /// <summary>Changes only when an older provider would be misread.</summary>
    public const int Major = 1;

    /// <summary>Grows with every addition an older provider can ignore.</summary>
    public const int Minor = 0;

    /// <summary>The hub's path on Gleanvolt's web port.</summary>
    public const string HubPath = "/providers/weather";
}

/// <summary>
/// The weather hub: a provider publishes the conditions at the site <b>now</b>, not a forecast. Gleanvolt
/// records them on a charging session when it opens and when it closes, and nothing in charge control
/// reads them. It asks (<see cref="IWeatherClient.RefreshRequested"/>) when it wants a reading and keeps
/// the newest one it was sent.
/// </summary>
public interface IWeatherHub : IProviderHub<WeatherWelcome>
{
    /// <summary>
    /// Offers a reading. It is held when it passes every check; on any problem nothing changes and the
    /// problems come back.
    /// </summary>
    Task<PublishResult> Publish(WeatherData data);
}

/// <summary>What the weather hub may send its provider.</summary>
public interface IWeatherClient : IProviderClient
{
    /// <summary>
    /// Gleanvolt wants the current conditions: a charging session is opening or closing, or somebody
    /// asked the API. Answer with <see cref="IWeatherHub.Publish"/>, quoting <see cref="RefreshRequest.Id"/>
    /// in <see cref="WeatherData.InReplyTo"/>, within 15 seconds. A provider that read the weather a few
    /// minutes ago may answer with that reading.
    /// </summary>
    Task RefreshRequested(RefreshRequest request);

    /// <summary>
    /// The site moved while the session was attached. Readings are for <paramref name="site"/> from now
    /// on. Gleanvolt applies a new location at its next start today, so a provider sees the new site in
    /// the welcome after it reconnects; this is here so a later Gleanvolt can say it sooner.
    /// </summary>
    Task SiteChanged(WeatherSite site);
}

/// <summary>An accepted weather hello: where the site is, so the location is not configured twice.</summary>
public sealed record WeatherWelcome : Welcome
{
    /// <summary>The site as Gleanvolt knows it.</summary>
    public WeatherSite Site { get; init; } = new(null, null, string.Empty);
}

/// <summary>
/// Where the site is. Null coordinates when the owner has not entered them: say so with
/// <see cref="ProviderStatusCodes.NotSetUp"/> rather than guess a place.
/// </summary>
/// <param name="Latitude">Decimal degrees, north positive.</param>
/// <param name="Longitude">Decimal degrees, east positive.</param>
/// <param name="TimeZone">The installation's IANA time zone, e.g. "Europe/Prague".</param>
public sealed record WeatherSite(double? Latitude, double? Longitude, string TimeZone);

/// <summary>
/// The conditions at the site at one moment, in SI units. Everything but <see cref="ObservedAt"/> is
/// optional, and absent means unknown, never zero; a reading needs at least a temperature or a condition.
/// </summary>
/// <param name="ObservedAt">
/// When the <b>weather service</b> observed it, with its offset: not when the provider sent it. No more
/// than 5 minutes ahead of Gleanvolt's clock, no older than 3 hours, and not older than the reading
/// Gleanvolt holds. The same time as that one is accepted.
/// </param>
/// <param name="InReplyTo">The request this answers, or null when sent unprompted.</param>
/// <param name="TemperatureCelsius">Air temperature, −90 to 60 °C.</param>
/// <param name="PressurePascals">Atmospheric pressure at the site, 85 000 to 110 000 Pa (hPa × 100).</param>
/// <param name="HumidityPercent">Relative humidity, 0 to 100 %.</param>
/// <param name="CloudsPercent">Cloud cover, 0 to 100 %: the figure that explains a solar session.</param>
/// <param name="VisibilityMetres">Visibility, 0 to 100 km, in metres.</param>
/// <param name="Condition">A short group in English, e.g. "Clear", "Clouds", "Rain", "Snow". At most 100 characters.</param>
/// <param name="ConditionDescription">Its longer form in English, e.g. "light rain". At most 200 characters.</param>
/// <param name="Sunrise">Sunrise on the site's day, or null (a polar night has none).</param>
/// <param name="Sunset">Sunset on the site's day, or null. After <paramref name="Sunrise"/> when both are given.</param>
public sealed record WeatherData(
    DateTimeOffset ObservedAt,
    RequestId? InReplyTo = null,
    double? TemperatureCelsius = null,
    double? PressurePascals = null,
    double? HumidityPercent = null,
    double? CloudsPercent = null,
    double? VisibilityMetres = null,
    string? Condition = null,
    string? ConditionDescription = null,
    DateTimeOffset? Sunrise = null,
    DateTimeOffset? Sunset = null);
