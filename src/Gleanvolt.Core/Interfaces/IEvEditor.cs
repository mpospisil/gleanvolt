using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// Edits the car and the feed that reads it from <c>/car</c> (issue #214), on the mechanism
/// <see cref="IPvSystemEditor"/> already built for the installation: a save writes the configuration
/// the <i>next</i> start will read, and a restart applies it.
///
/// <para><b>Saved is not applied.</b> The feed is constructed at startup out of these very keys, so the
/// sequence is <i>save → restart → Ask the car</i>, all on <c>/car</c>.</para>
///
/// <para><b>Only a configuration that would start is saved.</b> A save runs the merged result through
/// the same rules startup uses — <c>EvRules</c> against the charger's amp band, and the fields a feed
/// needs — and a refusal writes nothing. No key here is a secret since #477: a car plugin keeps the
/// account on its own page.</para>
///
/// <para>Implemented in <c>Gleanvolt.Hosting</c>, which owns the configuration; the web UI only asks.</para>
/// </summary>
public interface IEvEditor
{
    /// <summary>Every editable key as the next start would see it, read afresh from the overrides file.</summary>
    EvSettings Read();

    /// <summary>
    /// Stores <paramref name="values"/> (configuration key to value; an empty string clears a value) — if the merged configuration passes
    /// the same rules startup applies. A value equal to what the key would be without the file is not
    /// stored, so the file only ever holds real differences.
    /// </summary>
    SettingsSaveResult Save(IReadOnlyDictionary<string, string> values);

    /// <summary>
    /// Switches to <paramref name="feed"/> and applies <paramref name="values"/> in the same save, so
    /// that the feed being left is disabled by the same write that enables the new one.
    /// </summary>
    SettingsSaveResult SaveFeed(VehicleFeed feed, IReadOnlyDictionary<string, string> values);

    /// <summary>
    /// Removes <paramref name="key"/> from the overrides file, so it comes from the environment or <c>appsettings.json</c> again. Checked like a save;
    /// removing the last key deletes the file.
    /// </summary>
    SettingsSaveResult Revert(string key);
}
