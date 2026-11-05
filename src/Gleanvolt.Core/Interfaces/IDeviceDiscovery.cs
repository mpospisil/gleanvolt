using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// Looks for the inverter and the charger on the local network, for the setup wizard (#302).
///
/// <para>Declared here and implemented by the host, like <see cref="IVehicleAccountSignIn"/>: the web UI
/// that asks has no business knowing how a scan is done, and a test can stand in for one without a
/// network.</para>
///
/// <para><b>One scan at a time, for the whole process.</b> A scan opens connections to every host on a
/// subnet, and two at once would be twice that for nothing; the second browser sees the first one's
/// progress instead. <see cref="Changed"/> fires as a scan advances, so a page can follow it live.</para>
///
/// <para><b>Roles are suggested, never assumed.</b> A found device says what it identified as; the page
/// asks the owner to confirm, because a swapped inverter and charger both answer Modbus on port 502
/// and look, once running, like a frozen battery and no sun.</para>
/// </summary>
public interface IDeviceDiscovery
{
    /// <summary>
    /// The subnets this host would scan by default, e.g. "192.168.1.0/24", with any reason to doubt
    /// them (a container that cannot see the LAN).
    /// </summary>
    DiscoverySuggestion Suggest();

    /// <summary>The scan running now, or the last one to finish; null before the first.</summary>
    DiscoveryScan? Current { get; }

    /// <summary>
    /// Starts a scan of <paramref name="subnet"/> (CIDR, or a single address), or of the suggested
    /// subnets when it is empty. False, with a reason, when one is already running or the subnet is
    /// not one this host would scan.
    /// </summary>
    DiscoveryStart Start(string? subnet);

    /// <summary>Stops the running scan; what it found so far is kept.</summary>
    void Cancel();

    /// <summary>Raised from the scan's own thread whenever <see cref="Current"/> changes.</summary>
    event Action? Changed;
}

/// <summary>What the host would scan by default.</summary>
public sealed record DiscoverySuggestion(IReadOnlyList<string> Subnets, IReadOnlyList<string> Warnings);

/// <summary>Whether a scan started, and why not.</summary>
public sealed record DiscoveryStart(bool Started, string? Problem = null, Phrase? ProblemPhrase = null);

/// <summary>
/// A scan in progress or finished. <see cref="Probed"/> of <see cref="ToProbe"/> is how far the first,
/// longer part has got: one connect per host and port. Devices appear as they are identified.
/// </summary>
public sealed record DiscoveryScan(
    IReadOnlyList<string> Networks,
    int ToProbe,
    int Probed,
    bool Running,
    bool Cancelled,
    IReadOnlyList<FoundDevice> Devices,
    IReadOnlyList<string> Warnings,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt);

/// <summary>
/// One address that answered. <see cref="Role"/> is what the device identified as, null when it did
/// not say; <see cref="Supported"/> is false for a SolaX model Gleanvolt cannot drive and for
/// anything that is not SolaX at all.
/// </summary>
public sealed record FoundDevice(
    string Host,
    int Port,
    byte? UnitId,
    PvDeviceRole? Role,
    string? Model,
    string? SerialNumber,
    bool Supported,
    string Note);
