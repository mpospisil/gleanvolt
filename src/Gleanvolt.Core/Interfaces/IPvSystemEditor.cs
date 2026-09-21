using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// Edits the installation's configuration from the web UI (issue #204) without touching the running
/// process: a save writes the configuration the <i>next</i> start will read, and a restart applies it.
///
/// <para><b>Saved is not applied.</b> The running <see cref="PvSystemInfo"/> and the Modbus clients
/// built from it stay exactly as they started. Everything here is about the file the next start reads,
/// and <see cref="PvSystemSettings.Pending"/> is the difference between the two.</para>
///
/// <para><b>Only a configuration that would start is saved.</b> A save runs the merged result through
/// the same resolver startup uses, and a refusal writes nothing: a file that stopped the controller
/// from starting could only be removed from a shell.</para>
///
/// <para>Implemented in <c>Gleanvolt.Hosting</c>, which owns the configuration; the web UI only asks.</para>
/// </summary>
public interface IPvSystemEditor
{
    /// <summary>Every editable key as the next start would see it, read afresh from the overrides file.</summary>
    PvSystemSettings Read();

    /// <summary>
    /// Stores <paramref name="values"/> (configuration key to value; an empty string clears a value)
    /// in the overrides file, merged with what is there already — if the merged configuration passes
    /// the startup resolver. A value equal to what the key would be without the file is not stored,
    /// so the file only ever holds real differences.
    /// </summary>
    PvSystemSaveResult Save(IReadOnlyDictionary<string, string> values);

    /// <summary>
    /// Removes <paramref name="key"/> from the overrides file, so it comes from the environment or
    /// <c>appsettings.json</c> again. Checked by the resolver like a save; removing the last key
    /// deletes the file.
    /// </summary>
    PvSystemSaveResult Revert(string key);

    /// <summary>
    /// One read against a device address, to catch a swapped inverter and charger before a restart
    /// makes it look like a frozen battery and zero solar. Never throws for an unreachable device.
    /// </summary>
    Task<DeviceProbeResult> ProbeAsync(
        PvDeviceRole role,
        string host,
        int port,
        byte unitId,
        CancellationToken cancellationToken = default);
}
