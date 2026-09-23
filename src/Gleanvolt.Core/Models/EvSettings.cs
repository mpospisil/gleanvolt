namespace Gleanvolt.Core.Models;

/// <summary>
/// The keys <c>/car</c> may edit (issue #214): what the car is, what it will accept, and the one feed
/// that reads it. <see cref="PvSystemSettingKeys"/>'s arrangement, applied to the other side of the
/// cable, and the editor refuses a key that is not on this list for the same reason.
///
/// <para><b>Four sections, one page.</b> An owner has no way of knowing that <c>Ev:Vehicles:0</c>,
/// <c>Vehicle:Website</c>, <c>Vehicle:Skoda</c>, <c>Vehicle:DataAct</c> and <c>Vehicle</c> are about
/// the same car; the page knows, which is what lets it offer one manufacturer choice instead of five
/// sections and a startup exception.</para>
///
/// <para><b>Out of scope on purpose:</b> a second car (<c>Ev:Vehicles:1</c> and beyond — see
/// <c>EvRules.SupportedVehicleCount</c>), the poll intervals and timeouts, <c>Vehicle:MaxAge</c>, and
/// the MQTT broker's own host and credentials, which are the house broker's rather than the car's.</para>
/// </summary>
public static class EvSettingKeys
{
    /// <summary>The one car. The index is a constant here, because the page edits one and only one.</summary>
    private const string Vehicle = "Ev:Vehicles:0";

    public const string Id = Vehicle + ":Id";
    public const string Name = Vehicle + ":Name";
    public const string Make = Vehicle + ":Make";
    public const string Model = Vehicle + ":Model";
    public const string BatteryCapacityKWh = Vehicle + ":BatteryCapacityKWh";
    public const string ChargeEfficiency = Vehicle + ":ChargeEfficiency";
    public const string Phases = Vehicle + ":Phases";
    public const string MinChargingCurrentAmps = Vehicle + ":MinChargingCurrentAmps";
    public const string MaxChargingCurrentAmps = Vehicle + ":MaxChargingCurrentAmps";

    /// <summary>The owner's own MQTT topic. The per-car half of the <c>Vehicle</c> section.</summary>
    public const string TelemetryTopic = Vehicle + ":Telemetry:Topic";

    public const string WebsiteEnabled = "Vehicle:Website:Enabled";
    public const string WebsiteUsername = "Vehicle:Website:Username";
    public const string WebsitePassword = "Vehicle:Website:Password";
    public const string WebsiteVin = "Vehicle:Website:Vin";

    public const string SkodaEnabled = "Vehicle:Skoda:Enabled";
    public const string SkodaVin = "Vehicle:Skoda:Vin";

    public const string DataActEnabled = "Vehicle:DataAct:Enabled";
    public const string DataActBrand = "Vehicle:DataAct:Brand";
    public const string DataActClientId = "Vehicle:DataAct:ClientId";
    public const string DataActUsername = "Vehicle:DataAct:Username";
    public const string DataActPassword = "Vehicle:DataAct:Password";
    public const string DataActVin = "Vehicle:DataAct:Vin";

    /// <summary>Whether the owner's own MQTT topic is subscribed to at all — <c>VehicleOptions.Enabled</c>.</summary>
    public const string MqttEnabled = "Vehicle:Enabled";

    /// <summary>The car's own fields, which are worth setting whether or not any feed reads it.</summary>
    public static IReadOnlyList<string> Car { get; } =
    [
        Id, Name, Make, Model,
        BatteryCapacityKWh, ChargeEfficiency,
        Phases, MinChargingCurrentAmps, MaxChargingCurrentAmps,
    ];

    /// <summary>Every editable key, in the order the page shows them.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        .. Car,
        WebsiteEnabled, WebsiteUsername, WebsitePassword, WebsiteVin,
        SkodaEnabled, SkodaVin,
        DataActEnabled, DataActBrand, DataActClientId, DataActUsername, DataActPassword, DataActVin,
        MqttEnabled, TelemetryTopic,
    ];

    /// <summary>
    /// The keys that are bearer-equivalent: hold one and you are the owner at the manufacturer's end.
    /// They never reach the overrides file, are never rendered back, and never appear in a message —
    /// see <see cref="EvSecret"/>.
    /// </summary>
    public static IReadOnlyList<string> Secrets { get; } = [WebsitePassword, DataActPassword];

    /// <summary>Whether <paramref name="key"/> is one the web UI may edit. Case-insensitive, as configuration is.</summary>
    public static bool IsEditable(string key) => All.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="key"/> holds a secret, and must therefore never be read back.</summary>
    public static bool IsSecret(string key) => Secrets.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>The key as <see cref="All"/> spells it, or null when it is not editable.</summary>
    public static string? Canonical(string key) =>
        All.FirstOrDefault(candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The environment variable that sets <paramref name="key"/> — <c>Vehicle__Skoda__Vin</c> for
    /// <c>Vehicle:Skoda:Vin</c> — which is what an operator will look for in <c>.env</c>.
    /// </summary>
    public static string EnvironmentVariable(string key) => key.Replace(":", "__", StringComparison.Ordinal);
}

/// <summary>
/// Which feed reads this car — the one control on <c>/car</c> that changes which fields exist.
///
/// <para><b>A choice of feed, never a runtime selector.</b> Picking one writes that feed's
/// <c>Enabled</c> key and clears the others'; the composition root goes on reading only those keys,
/// and nothing reads <c>Ev:Vehicles:0:Make</c>. Making the make select a feed is the natural-looking
/// mistake here, and it would mean a typo in a reported-only field changing which service polls the
/// car.</para>
/// </summary>
public enum VehicleFeed
{
    /// <summary>
    /// No feed. <b>Not an error state</b>: every EV with a Type 2 inlet charges without one. It costs
    /// exactly one thing — targets are asked for in kWh instead of per cent.
    /// </summary>
    ChargeOnly,

    /// <summary>volkswagen.de, live (<c>Vehicle:Website</c>).</summary>
    Volkswagen,

    /// <summary>The MyŠkoda Public API (<c>Vehicle:Skoda</c>); the key is pasted on <c>/car</c>.</summary>
    Skoda,

    /// <summary>The EU Data Act portal (<c>Vehicle:DataAct</c>), for the Group brands with no live feed.</summary>
    DataAct,

    /// <summary>A topic the owner publishes themselves (<c>Vehicle</c>).</summary>
    OwnTopic,
}

/// <summary>
/// What each <see cref="VehicleFeed"/> switches on and what it then needs. Stated once, here, because
/// three surfaces ask it: the page (which fields to show), the editor (what a save must fill in or
/// clear) and its validation (what an enabled feed may not be missing).
/// </summary>
public static class VehicleFeeds
{
    /// <summary>The choices in the order the page offers them.</summary>
    public static IReadOnlyList<VehicleFeed> All { get; } =
    [
        VehicleFeed.Volkswagen, VehicleFeed.Skoda, VehicleFeed.DataAct,
        VehicleFeed.OwnTopic, VehicleFeed.ChargeOnly,
    ];

    /// <summary>The key that switches this feed on, or null for <see cref="VehicleFeed.ChargeOnly"/>.</summary>
    public static string? EnabledKey(VehicleFeed feed) => feed switch
    {
        VehicleFeed.Volkswagen => EvSettingKeys.WebsiteEnabled,
        VehicleFeed.Skoda => EvSettingKeys.SkodaEnabled,
        VehicleFeed.DataAct => EvSettingKeys.DataActEnabled,
        VehicleFeed.OwnTopic => EvSettingKeys.MqttEnabled,
        _ => null,
    };

    /// <summary>Every key this feed owns, in the order the page shows them. Empty for charge-only.</summary>
    public static IReadOnlyList<string> Fields(VehicleFeed feed) => feed switch
    {
        VehicleFeed.Volkswagen =>
            [EvSettingKeys.WebsiteUsername, EvSettingKeys.WebsitePassword, EvSettingKeys.WebsiteVin],
        VehicleFeed.Skoda => [EvSettingKeys.SkodaVin],
        VehicleFeed.DataAct =>
        [
            EvSettingKeys.DataActBrand, EvSettingKeys.DataActUsername,
            EvSettingKeys.DataActPassword, EvSettingKeys.DataActVin, EvSettingKeys.DataActClientId,
        ],
        VehicleFeed.OwnTopic => [EvSettingKeys.TelemetryTopic],
        _ => [],
    };

    /// <summary>
    /// The keys this feed cannot start without. <c>Vehicle:DataAct:ClientId</c> is deliberately not one
    /// of them: it is the escape hatch for a brand whose sign-in id has changed, which is documentation
    /// for a failure rather than a field to fill.
    /// </summary>
    public static IReadOnlyList<string> Required(VehicleFeed feed) => feed switch
    {
        VehicleFeed.Volkswagen =>
            [EvSettingKeys.WebsiteUsername, EvSettingKeys.WebsitePassword, EvSettingKeys.WebsiteVin],
        VehicleFeed.Skoda => [EvSettingKeys.SkodaVin],
        VehicleFeed.DataAct =>
            [EvSettingKeys.DataActBrand, EvSettingKeys.DataActUsername, EvSettingKeys.DataActPassword, EvSettingKeys.DataActVin],
        VehicleFeed.OwnTopic => [EvSettingKeys.TelemetryTopic],
        _ => [],
    };

    /// <summary>The feed the configuration in <paramref name="isEnabled"/> selects.</summary>
    /// <param name="isEnabled">Answers whether an <c>Enabled</c> key reads true.</param>
    /// <remarks>
    /// The order is the one the composition root resolves in (issue #212): a live feed wins over the
    /// Data Act portal, which is set aside rather than run beside it. Two live feeds cannot both be on
    /// — startup refuses that outright — so which of them is named first here never arises through this
    /// page; it matters only for an <c>.env</c> that already holds both, where naming the one that runs
    /// is better than naming neither.
    /// </remarks>
    public static VehicleFeed Selected(Func<string, bool> isEnabled)
    {
        ArgumentNullException.ThrowIfNull(isEnabled);

        foreach (var feed in (VehicleFeed[])[VehicleFeed.Volkswagen, VehicleFeed.Skoda, VehicleFeed.DataAct, VehicleFeed.OwnTopic])
        {
            if (isEnabled(EnabledKey(feed)!))
            {
                return feed;
            }
        }

        return VehicleFeed.ChargeOnly;
    }
}

/// <summary>
/// A VW Group brand as the Data Act choice offers it: the name the configuration takes, and what to
/// print beside it. Supplied by the host from the client's own table, so the day a brand is added
/// there it appears on the page.
/// </summary>
/// <param name="Name">What goes in <c>Vehicle:DataAct:Brand</c>.</param>
/// <param name="Label">What the owner reads.</param>
public sealed record VehicleBrand(string Name, string Label);

/// <summary>
/// A secret as the page may know it: <b>whether</b> there is one, never what it is (issue #214).
///
/// <para>A separate type from <see cref="ConfiguredSetting"/> on purpose. "Never rendered back" is then
/// a property of the shape rather than a discipline every caller has to keep: there is no field here
/// that could carry the password to a browser, so no rendering, log line, provenance display or
/// pending-changes banner can echo it by accident.</para>
/// </summary>
/// <param name="Key">The configuration path the secret would otherwise be set by.</param>
/// <param name="Stored">A value exists for the next start.</param>
/// <param name="Running">This process started with one.</param>
/// <param name="Source">Where the stored one comes from — the store, or the environment beside it.</param>
/// <param name="SourceDetail">The environment variable or the store's own description, for a tooltip.</param>
public sealed record EvSecret(
    string Key,
    bool Stored,
    bool Running,
    SettingSource Source,
    string SourceDetail)
{
    /// <summary>Stored differs from running: a restart is what puts it into use.</summary>
    public bool IsPending => Stored != Running;

    /// <summary>The value is in the secret store, and "Remove" applies.</summary>
    public bool IsEdited => Source == SettingSource.SecretStore;
}

/// <summary>Every editable key of the car, read afresh, and the feed that reads it.</summary>
/// <param name="OverridesPath">The absolute path of the overrides file, whether or not it exists yet.</param>
/// <param name="Settings">One entry per non-secret key in <see cref="EvSettingKeys.All"/>, in that order.</param>
/// <param name="Secrets">One entry per key in <see cref="EvSettingKeys.Secrets"/>. Values are not included.</param>
/// <param name="Feed">The feed the next start would run.</param>
/// <param name="RunningFeed">The feed this process started.</param>
/// <param name="Brands">The Data Act brands this build knows, for the brand list.</param>
/// <param name="SecretProtection">
/// What protects a stored password, taken from the store's own description so the page cannot drift
/// into claiming more than is true.
/// </param>
/// <param name="ChargerMinAmps">The installation's floor, shown beside the car's so the narrower-of-the-two rule is visible.</param>
/// <param name="ChargerMaxAmps">The installation's ceiling, for the same reason.</param>
/// <param name="Unavailable">
/// Why this host cannot take edits at all, or null when it can: the overrides file is not among its
/// configuration sources, so anything written there would never be read.
/// </param>
public sealed record EvSettings(
    string OverridesPath,
    IReadOnlyList<ConfiguredSetting> Settings,
    IReadOnlyList<EvSecret> Secrets,
    VehicleFeed Feed,
    VehicleFeed RunningFeed,
    IReadOnlyList<VehicleBrand> Brands,
    string SecretProtection,
    int ChargerMinAmps,
    int ChargerMaxAmps,
    string? Unavailable = null)
{
    public ConfiguredSetting this[string key] =>
        Settings.First(setting => string.Equals(setting.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The secret for <paramref name="key"/>.</summary>
    public EvSecret Secret(string key) =>
        Secrets.First(secret => string.Equals(secret.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The keys whose saved value the running process is not using yet.</summary>
    public IEnumerable<ConfiguredSetting> Pending => Settings.Where(setting => setting.IsPending);

    /// <summary>The secrets the running process is not using yet — named, never valued.</summary>
    public IEnumerable<EvSecret> PendingSecrets => Secrets.Where(secret => secret.IsPending);

    /// <summary>Whether the feed the next start would run differs from the one running now.</summary>
    public bool FeedIsPending => Feed != RunningFeed;
}
