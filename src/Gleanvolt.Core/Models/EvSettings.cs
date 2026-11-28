namespace Gleanvolt.Core.Models;

/// <summary>
/// The keys <c>/car</c> may edit (issue #214): what the car is, what it will accept, and the one feed
/// that reads it. <see cref="PvSystemSettingKeys"/>'s arrangement, applied to the other side of the
/// cable, and the editor refuses a key that is not on this list for the same reason.
///
/// <para><b>Two sections, one page.</b> <c>Ev:Vehicles:0</c> is the car and <c>Vehicle:External</c> whether a car
/// plugin reads it. The built-in manufacturer feeds are gone since #477, and the car's own MQTT topic since #486: a
/// car plugin is the only way Gleanvolt reads a car.</para>
///
/// <para><b>Out of scope on purpose:</b> a second car (<c>Ev:Vehicles:1</c> and beyond — see
/// <c>EvRules.SupportedVehicleCount</c>) and <c>Vehicle:MaxAge</c>.</para>
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


    /// <summary>
    /// Whether an external provider reads the car (#408): a program outside Gleanvolt that connects to
    /// <c>/providers/vehicle</c> with the provider token and publishes what the car says.
    /// </summary>
    public const string ExternalEnabled = "Vehicle:External:Enabled";

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
/// <para><b>A car plugin or nothing.</b> volkswagen.de, MyŠkoda and the Data Act portal were built-in choices until
/// #477, and the owner's own MQTT topic until #486; a car plugin reads any of them now, through
/// <see cref="External"/>.</para>
/// </summary>
public enum VehicleFeed
{
    /// <summary>
    /// No feed. <b>Not an error state</b>: every EV with a Type 2 inlet charges without one. It costs
    /// exactly one thing — targets are asked for in kWh instead of per cent.
    /// </summary>
    ChargeOnly,

    /// <summary>
    /// A car plugin (<c>Vehicle:External</c>, #408): any car, read by a program outside Gleanvolt, paired on
    /// Settings → Plugins.
    /// </summary>
    External,
}

/// <summary>
/// What each <see cref="VehicleFeed"/> switches on. Stated once, here, because the page and the editor both ask it.
/// </summary>
public static class VehicleFeeds
{
    /// <summary>The choices in the order the page offers them.</summary>
    public static IReadOnlyList<VehicleFeed> All { get; } = [VehicleFeed.External, VehicleFeed.ChargeOnly];

    /// <summary>The key that switches this feed on, or null for <see cref="VehicleFeed.ChargeOnly"/>.</summary>
    public static string? EnabledKey(VehicleFeed feed) => feed switch
    {
        VehicleFeed.External => EvSettingKeys.ExternalEnabled,
        _ => null,
    };

    /// <summary>The feed the configuration in <paramref name="isEnabled"/> selects.</summary>
    /// <param name="isEnabled">Answers whether an <c>Enabled</c> key reads true.</param>
    public static VehicleFeed Selected(Func<string, bool> isEnabled)
    {
        ArgumentNullException.ThrowIfNull(isEnabled);

        return isEnabled(EvSettingKeys.ExternalEnabled) ? VehicleFeed.External : VehicleFeed.ChargeOnly;
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
