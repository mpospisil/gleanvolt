namespace Gleanvolt.Core.Models;

/// <summary>
/// The <c>Pv</c> keys the web UI may edit (issue #204): the installation as <c>/pv-system</c> shows it,
/// and nothing else. Secrets, the charging and battery settings and Home Assistant stay out — they can
/// follow on the same mechanism, but this list is where that decision is made, and the editor refuses
/// a key that is not on it.
///
/// <para>Configuration paths rather than property names, because that is what the overrides file,
/// the environment variables and the source of each value are all keyed by. The charger is the first
/// and only one, <c>Chargers:0</c>: one is what the control logic can drive.</para>
/// </summary>
public static class PvSystemSettingKeys
{
    public const string Id = "Pv:Id";
    public const string Name = "Pv:Name";
    public const string Address = "Pv:Address";
    public const string InstallDate = "Pv:InstallDate";
    public const string Latitude = "Pv:Latitude";
    public const string Longitude = "Pv:Longitude";
    public const string AzimuthDegrees = "Pv:AzimuthDegrees";
    public const string TiltDegrees = "Pv:TiltDegrees";
    public const string CapacityKwp = "Pv:CapacityKwp";
    public const string InverterCapacityKw = "Pv:InverterCapacityKw";
    public const string LossFactor = "Pv:LossFactor";
    public const string InverterModel = "Pv:Inverter:Model";
    public const string InverterHost = "Pv:Inverter:Host";
    public const string InverterPort = "Pv:Inverter:Port";
    public const string InverterUnitId = "Pv:Inverter:UnitId";
    public const string InverterUnmeteredGridPhase = "Pv:Inverter:UnmeteredGridPhase";
    public const string ChargerId = "Pv:Chargers:0:Id";
    public const string ChargerModel = "Pv:Chargers:0:Model";
    public const string ChargerHost = "Pv:Chargers:0:Host";
    public const string ChargerPort = "Pv:Chargers:0:Port";
    public const string ChargerUnitId = "Pv:Chargers:0:UnitId";

    /// <summary>Every editable key, in the order the page shows them.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Id, Name, Address, InstallDate,
        Latitude, Longitude, AzimuthDegrees, TiltDegrees,
        CapacityKwp, InverterCapacityKw, LossFactor,
        InverterModel, InverterHost, InverterPort, InverterUnitId, InverterUnmeteredGridPhase,
        ChargerId, ChargerModel, ChargerHost, ChargerPort, ChargerUnitId,
    ];

    /// <summary>Whether <paramref name="key"/> is one the web UI may edit. Case-insensitive, as configuration is.</summary>
    public static bool IsEditable(string key) => All.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The environment variable that sets <paramref name="key"/> — <c>Pv__Inverter__Host</c> for
    /// <c>Pv:Inverter:Host</c> — which is what an operator will look for in <c>.env</c>.
    /// </summary>
    public static string EnvironmentVariable(string key) => key.Replace(":", "__", StringComparison.Ordinal);
}

/// <summary>Where the value a key will have on the next start comes from.</summary>
public enum PvSettingSource
{
    /// <summary>Set nowhere: the section's own default applies.</summary>
    Default,

    /// <summary>An <c>appsettings*.json</c> file shipped with the build.</summary>
    AppSettings,

    /// <summary>
    /// An environment variable — which is where <c>.env</c> under Docker and
    /// <c>/etc/gleanvolt/gleanvolt.env</c> under systemd both end up.
    /// </summary>
    Environment,

    /// <summary>A command-line argument, which wins even over the web UI.</summary>
    CommandLine,

    /// <summary>The overrides file the web UI writes.</summary>
    WebUi,
}

/// <summary>
/// One editable key as the page shows it: what the next start will use, where that comes from, and
/// what the running process started with.
/// </summary>
/// <param name="Key">The configuration path, e.g. <c>Pv:Inverter:Host</c>.</param>
/// <param name="Saved">The value the next start will read, or null when the key is set nowhere.</param>
/// <param name="Running">The value this process started with, or null when it was set nowhere.</param>
/// <param name="Source">Where <paramref name="Saved"/> comes from.</param>
/// <param name="SourceDetail">The file or mechanism behind <paramref name="Source"/>, for a tooltip.</param>
/// <param name="Underlying">
/// What the key would be without the web UI's value — so a "Revert" can say what it reverts to.
/// </param>
public sealed record PvSystemSetting(
    string Key,
    string? Saved,
    string? Running,
    PvSettingSource Source,
    string SourceDetail,
    string? Underlying)
{
    /// <summary>Saved differs from running: it takes effect on the next start.</summary>
    public bool IsPending => !string.Equals(Saved ?? string.Empty, Running ?? string.Empty, StringComparison.Ordinal);

    /// <summary>The value comes from the web UI's file, and "Revert" applies.</summary>
    public bool IsEdited => Source == PvSettingSource.WebUi;
}

/// <summary>Every editable key, read afresh, and where the web UI keeps its edits.</summary>
/// <param name="OverridesPath">The absolute path of the overrides file, whether or not it exists yet.</param>
/// <param name="Settings">One entry per key in <see cref="PvSystemSettingKeys.All"/>, in that order.</param>
/// <param name="Unavailable">
/// Why this host cannot take edits at all, or null when it can: the overrides file is not among its
/// configuration sources, so anything written there would never be read.
/// </param>
public sealed record PvSystemSettings(
    string OverridesPath,
    IReadOnlyList<PvSystemSetting> Settings,
    string? Unavailable = null)
{
    public PvSystemSetting this[string key] =>
        Settings.First(setting => string.Equals(setting.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The keys whose saved value the running process is not using yet.</summary>
    public IEnumerable<PvSystemSetting> Pending => Settings.Where(setting => setting.IsPending);
}

/// <summary>What became of a save or a revert.</summary>
/// <param name="Saved">Whether the overrides file was written (or removed).</param>
/// <param name="Problems">Why not, each naming its key; empty when <paramref name="Saved"/>.</param>
public sealed record PvSystemSaveResult(bool Saved, IReadOnlyList<string> Problems)
{
    public static PvSystemSaveResult Success { get; } = new(true, []);

    public static PvSystemSaveResult Refused(IReadOnlyList<string> problems) => new(false, problems);
}

/// <summary>Which of the two devices an address is supposed to reach.</summary>
public enum PvDeviceRole
{
    Inverter,
    Charger,
}

/// <summary>
/// What one read against an address found. Advisory only: a charger that is switched off answers
/// nothing and is still the right address.
/// </summary>
/// <param name="Answered">Something replied to the read at all.</param>
/// <param name="Plausible">What replied looks like the device it was meant to be.</param>
/// <param name="Message">What to tell the operator.</param>
public sealed record DeviceProbeResult(bool Answered, bool Plausible, string Message);
