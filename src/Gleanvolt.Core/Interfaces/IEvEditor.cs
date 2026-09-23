using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// Edits the car and the feed that reads it from <c>/car</c> (issue #214), on the mechanism
/// <see cref="IPvSystemEditor"/> already built for the installation: a save writes the configuration
/// the <i>next</i> start will read, and a restart applies it.
///
/// <para><b>Saved is not applied</b>, and here it cannot even be probed. The feed is constructed at
/// startup out of these very keys, so there is nothing running to test them against — and a cold
/// volkswagen.de sign-in wants an emailed one-time code anyway. The sequence is <i>save → restart →
/// sign in → Ask the car</i>, all four of which are on <c>/car</c> since issue #227, and the page says
/// so rather than implying that a green save means a working feed.</para>
///
/// <para><b>Only a configuration that would start is saved.</b> A save runs the merged result through
/// the same rules startup uses — <c>EvRules</c> against the charger's amp band, and the feed rules that
/// make two live feeds a startup exception — and a refusal writes nothing.</para>
///
/// <para><b>Secrets are write-only.</b> A password goes to <see cref="ISecretStore"/> and never to the
/// overrides file; nothing here returns one, which is why <see cref="EvSettings.Secrets"/> carries
/// <see cref="EvSecret"/> rather than a value. An empty string for a secret key means <i>unchanged</i>
/// and never <i>clear</i>; clearing is <see cref="Revert"/>.</para>
///
/// <para>Implemented in <c>Gleanvolt.Hosting</c>, which owns the configuration; the web UI only asks.</para>
/// </summary>
public interface IEvEditor
{
    /// <summary>Every editable key as the next start would see it, read afresh from the overrides file.</summary>
    EvSettings Read();

    /// <summary>
    /// Stores <paramref name="values"/> (configuration key to value; an empty string clears a value,
    /// except for a secret key, where it means <i>unchanged</i>) — if the merged configuration passes
    /// the same rules startup applies. A value equal to what the key would be without the file is not
    /// stored, so the file only ever holds real differences.
    /// </summary>
    SettingsSaveResult Save(IReadOnlyDictionary<string, string> values);

    /// <summary>
    /// Switches to <paramref name="feed"/> and applies <paramref name="values"/> in the same save, so
    /// that the feed being left is disabled by the same write that enables the new one. A half-applied
    /// switch — two feeds enabled — is exactly the state that throws on the next start.
    /// </summary>
    SettingsSaveResult SaveFeed(VehicleFeed feed, IReadOnlyDictionary<string, string> values);

    /// <summary>
    /// Removes <paramref name="key"/> from the overrides file — or, for a secret key, from the secret
    /// store — so it comes from the environment or <c>appsettings.json</c> again. Checked like a save;
    /// removing the last key deletes the file.
    /// </summary>
    SettingsSaveResult Revert(string key);
}
