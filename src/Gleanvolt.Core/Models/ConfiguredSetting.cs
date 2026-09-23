namespace Gleanvolt.Core.Models;

/// <summary>Where the value a key will have on the next start comes from.</summary>
public enum SettingSource
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

    /// <summary>
    /// The secret store (issue #215), which is where a password typed into the web UI goes. Never the
    /// overrides file: that file is JSON beside the configuration and a password does not belong in it.
    /// </summary>
    SecretStore,
}

/// <summary>
/// One editable key as a settings page shows it: what the next start will use, where that comes from,
/// and what the running process started with.
///
/// <para>Shared by <c>/pv-system</c> (issue #204) and <c>/car</c> (issue #214), because the question is
/// the same on both — <i>saved, running, and from where</i> — and two copies of it would be two places
/// for "Revert" to mean something slightly different.</para>
/// </summary>
/// <param name="Key">The configuration path, e.g. <c>Pv:Inverter:Host</c>.</param>
/// <param name="Saved">The value the next start will read, or null when the key is set nowhere.</param>
/// <param name="Running">The value this process started with, or null when it was set nowhere.</param>
/// <param name="Source">Where <paramref name="Saved"/> comes from.</param>
/// <param name="SourceDetail">The file or mechanism behind <paramref name="Source"/>, for a tooltip.</param>
/// <param name="Underlying">
/// What the key would be without the web UI's value — so a "Revert" can say what it reverts to.
/// </param>
public sealed record ConfiguredSetting(
    string Key,
    string? Saved,
    string? Running,
    SettingSource Source,
    string SourceDetail,
    string? Underlying)
{
    /// <summary>Saved differs from running: it takes effect on the next start.</summary>
    public bool IsPending => !string.Equals(Saved ?? string.Empty, Running ?? string.Empty, StringComparison.Ordinal);

    /// <summary>The value comes from the web UI's file, and "Revert" applies.</summary>
    public bool IsEdited => Source == SettingSource.WebUi;
}

/// <summary>What became of a save or a revert.</summary>
/// <param name="Saved">Whether the overrides file was written (or removed).</param>
/// <param name="Problems">Why not, each naming its key; empty when <paramref name="Saved"/>.</param>
public sealed record SettingsSaveResult(bool Saved, IReadOnlyList<string> Problems)
{
    public static SettingsSaveResult Success { get; } = new(true, []);

    public static SettingsSaveResult Refused(IReadOnlyList<string> problems) => new(false, problems);
}
