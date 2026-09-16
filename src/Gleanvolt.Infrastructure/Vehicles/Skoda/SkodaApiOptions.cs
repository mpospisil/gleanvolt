namespace Gleanvolt.Infrastructure.Vehicles.Skoda;

/// <summary>
/// What the MyŠkoda Public API feed needs (issue #193). Bound from <c>Vehicle:Skoda</c>.
///
/// <para><b>No API key here, on purpose.</b> The key is pasted on the Vehicle portal page and kept at
/// <see cref="KeyPath"/>, and nowhere else. Keys expire, so renewing one is a recurring owner action;
/// one place to do it beats a <c>.env</c> edit plus a restart, and a key in two places is two places
/// for it to go stale.</para>
///
/// <para><b>The feed is chosen by this section, never by <c>Ev:Vehicles[].Make</c>.</b> Writing
/// <c>Škoda</c> there selects nothing; enabling this does.</para>
/// </summary>
public sealed class SkodaApiOptions
{
    public const string SectionName = "Vehicle:Skoda";

    /// <summary>Whether to use this source at all. Off by default, like everything that leaves the LAN.</summary>
    public bool Enabled { get; init; }

    /// <summary>Which car. Required: every call is per-VIN, and a key is bound to the VINs it names.</summary>
    public string Vin { get; init; } = string.Empty;

    /// <summary>
    /// Where the API is. A hedge rather than an abstraction: it points at Škoda's test environment
    /// (<c>https://public.test-api.connect.skoda-auto.cz</c>, same spec) without a separate switch, and
    /// lets an owner try another VW Group brand if one ever publishes the same API.
    /// </summary>
    public string BaseUrl { get; init; } = "https://public.api.connect.skoda-auto.cz";

    /// <summary>What the readings are labelled with — the dashboard's <i>via</i> and <c>/vehicle-feeds</c>.</summary>
    public string SourceId { get; init; } = "skoda";

    /// <summary>
    /// How often to ask while a charge is running: twelve of the hour's twenty requests, which is a
    /// charge curve and still leaves room for a person pressing <i>Ask the car</i>.
    /// </summary>
    public TimeSpan ChargingPollInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often to ask between charges: four an hour. Not zero, unlike volkswagen.de — a key has no
    /// session to spend, an idle read costs one request of the quota and nothing else, and the state of
    /// charge known before a charge starts is what a target in percent is planned from.
    /// </summary>
    public TimeSpan IdlePollInterval { get; init; } = TimeSpan.FromMinutes(15);

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Where the pasted key is kept between restarts. <b>Bearer-equivalent</b> — the same key starts and
    /// stops charging — so it is written owner-only and never logged or rendered.
    /// </summary>
    public string KeyPath { get; init; } = "data/skoda-api-key.json";

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(Vin);
}
