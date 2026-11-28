namespace Gleanvolt.Core.Models;

/// <summary>
/// The keys <c>/car</c> may edit (issue #214): what the car is, what it will accept, and the one feed
/// that reads it. <see cref="PvSystemSettingKeys"/>'s arrangement, applied to the other side of the
/// cable, and the editor refuses a key that is not on this list for the same reason.
///
/// <para><b>Three sections, one page.</b> An owner has no way of knowing that <c>Ev:Vehicles:0</c>,
/// <c>Vehicle:External</c> and <c>Vehicle</c> are about the same car; the page knows. The built-in manufacturer
/// feeds (<c>Vehicle:Website</c>, <c>Vehicle:Skoda</c>, <c>Vehicle:DataAct</c>) are gone since #477: a car plugin
/// reads those cars.</para>
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

    /// <summary>
    /// Whether an external provider reads the car (#408): a program outside Gleanvolt that connects to
    /// <c>/providers/vehicle</c> with the provider token and publishes what the car says.
    /// </summary>
    public const string ExternalEnabled = "Vehicle:External:Enabled";

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
        ExternalEnabled,
        MqttEnabled, TelemetryTopic,
    ];

    /// <summary>Whether <paramref name="key"/> is one the web UI may edit. Case-insensitive, as configuration is.</summary>
    public static bool IsEditable(string key) => All.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>The key as <see cref="All"/> spells it, or null when it is not editable.</summary>
    public static string? Canonical(string key) =>
        All.FirstOrDefault(candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The environment variable that sets <paramref name="key"/> — <c>Vehicle__External__Enabled</c> for
    /// <c>Vehicle:External:Enabled</c> — which is what an operator will look for in <c>.env</c>.
    /// </summary>
    public static string EnvironmentVariable(string key) => key.Replace(":", "__", StringComparison.Ordinal);
}

/// <summary>
/// Which feed reads this car — the one control on <c>/car</c> that changes which fields exist.
///
/// <para><b>A choice of feed, never a runtime selector.</b> Picking one writes that feed's
/// <c>Enabled</c> key and clears the others'; the composition root goes on reading only those keys,
/// and nothing reads <c>Ev:Vehicles:0:Make</c>.</para>
///
/// <para><b>No manufacturer feeds (#477).</b> volkswagen.de, MyŠkoda and the Data Act portal were built-in choices
/// until #477; they are car plugins now, read through <see cref="External"/>.</para>
/// </summary>
public enum VehicleFeed
{
    /// <summary>
    /// No feed. <b>Not an error state</b>: every EV with a Type 2 inlet charges without one. It costs
    /// exactly one thing — targets are asked for in kWh instead of per cent.
    /// </summary>
    ChargeOnly,

    /// <summary>A topic the owner publishes themselves (<c>Vehicle</c>).</summary>
    OwnTopic,

    /// <summary>
    /// A car plugin (<c>Vehicle:External</c>, #408): any car, read by a program outside Gleanvolt, paired on
    /// Settings → Plugins.
    /// </summary>
    External,
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
        [VehicleFeed.External, VehicleFeed.OwnTopic, VehicleFeed.ChargeOnly];

    /// <summary>The key that switches this feed on, or null for <see cref="VehicleFeed.ChargeOnly"/>.</summary>
    public static string? EnabledKey(VehicleFeed feed) => feed switch
    {
        VehicleFeed.External => EvSettingKeys.ExternalEnabled,
        VehicleFeed.OwnTopic => EvSettingKeys.MqttEnabled,
        _ => null,
    };

    /// <summary>Every key this feed owns, in the order the page shows them. Empty for charge-only.</summary>
    public static IReadOnlyList<string> Fields(VehicleFeed feed) => feed switch
    {
        VehicleFeed.OwnTopic => [EvSettingKeys.TelemetryTopic],
        _ => [],
    };

    /// <summary>The keys this feed cannot start without.</summary>
    public static IReadOnlyList<string> Required(VehicleFeed feed) => Fields(feed);

    /// <summary>The feed the configuration in <paramref name="isEnabled"/> selects.</summary>
    /// <param name="isEnabled">Answers whether an <c>Enabled</c> key reads true.</param>
    /// <remarks>A plugin wins over the owner's own topic, which may run beside it.</remarks>
    public static VehicleFeed Selected(Func<string, bool> isEnabled)
    {
        ArgumentNullException.ThrowIfNull(isEnabled);

        foreach (var feed in (VehicleFeed[])[VehicleFeed.External, VehicleFeed.OwnTopic])
        {
            if (isEnabled(EnabledKey(feed)!))
            {
                return feed;
            }
        }

        return VehicleFeed.ChargeOnly;
    }
}

/// <summary>Every editable key of the car, read afresh, and the feed that reads it.</summary>
/// <param name="OverridesPath">The absolute path of the overrides file, whether or not it exists yet.</param>
/// <param name="Settings">One entry per key in <see cref="EvSettingKeys.All"/>, in that order.</param>
/// <param name="Feed">The feed the next start would run.</param>
/// <param name="RunningFeed">The feed this process started.</param>
/// <param name="ChargerMinAmps">The installation's floor, shown beside the car's so the narrower-of-the-two rule is visible.</param>
/// <param name="ChargerMaxAmps">The installation's ceiling, for the same reason.</param>
/// <param name="Unavailable">
/// Why this host cannot take edits at all, or null when it can: the overrides file is not among its
/// configuration sources, so anything written there would never be read.
/// </param>
public sealed record EvSettings(
    string OverridesPath,
    IReadOnlyList<ConfiguredSetting> Settings,
    VehicleFeed Feed,
    VehicleFeed RunningFeed,
    int ChargerMinAmps,
    int ChargerMaxAmps,
    string? Unavailable = null)
{
    public ConfiguredSetting this[string key] =>
        Settings.First(setting => string.Equals(setting.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The keys whose saved value the running process is not using yet.</summary>
    public IEnumerable<ConfiguredSetting> Pending => Settings.Where(setting => setting.IsPending);

    /// <summary>Whether the feed the next start would run differs from the one running now.</summary>
    public bool FeedIsPending => Feed != RunningFeed;
}
