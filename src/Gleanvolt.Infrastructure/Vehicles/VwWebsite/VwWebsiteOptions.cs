namespace Gleanvolt.Infrastructure.Vehicles.VwWebsite;

/// <summary>
/// What the volkswagen.de client needs (issue #170). Bound from <c>Vehicle:Website</c>.
///
/// <para>The live source, as distinct from the EU Data Act portal's batch delivery. With this
/// configured the portal is not used at all (#212): one car, one feed.</para>
/// </summary>
public sealed class VwWebsiteOptions
{
    public const string SectionName = "Vehicle:Website";

    /// <summary>Whether to use this source at all. Off by default, like everything that leaves the LAN.</summary>
    public bool Enabled { get; init; }

    public string PortalBaseUrl { get; init; } = "https://www.volkswagen.de";

    public string IdentityBaseUrl { get; init; } = "https://identity.vwgroup.io";

    /// <summary>The brand account's email. The same one the Data Act portal takes.</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>
    /// Its password. Not a read-only credential — the same account unlocks and locates the car —
    /// which is why it lives in <c>.env</c> and is never logged or rendered.
    /// </summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>Which car. Required: the routing lookup and every data call are per-VIN.</summary>
    public string Vin { get; init; } = string.Empty;

    /// <summary>
    /// How often to ask while a charging session is open.
    ///
    /// <para>The car reports to VW on its own schedule, so a shorter interval buys resolution up to a
    /// point and then only costs requests. Five minutes across a multi-hour charge is a curve.</para>
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often to ask between charges (#212). This is the car's only feed, so the state of charge a
    /// plan starts from -- after a drive, before any charge -- has to come from here. A parked car's
    /// SOC barely moves, so a quarter of an hour is plenty.
    /// </summary>
    public TimeSpan IdlePollInterval { get; init; } = TimeSpan.FromMinutes(15);

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The floor between credential re-logins. A broken session must never hammer VW's login: that is
    /// how an account gets locked, and the failure it would be recovering from is usually not one a
    /// retry fixes.
    /// </summary>
    public TimeSpan MinimumTimeBetweenLogins { get; init; } = TimeSpan.FromMinutes(5);

    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(Username)
        && !string.IsNullOrWhiteSpace(Password)
        && !string.IsNullOrWhiteSpace(Vin);
}
