using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// Settings → "Save register snapshot" (issues #319, #350): one read of every register the running driver of
/// the inverter or the charger uses, as a file. It is how a tester sends a recording of a family Gleanvolt
/// does not drive yet (#325, #354), and how a driver's conformance tests get their fixtures.
///
/// <para>Reads only, on the device's own client, so it takes its turn with the poll loop rather than
/// opening a second connection the device may not allow. The charger needs this most: it serves one Modbus
/// client at a time, and a second connection is dropped on its first request.</para>
/// </summary>
public interface IDeviceSnapshot
{
    /// <summary>Takes the snapshot of <paramref name="role"/>'s device.</summary>
    /// <param name="role">The inverter, or the controlled charger.</param>
    /// <param name="note">What is happening at the time ("night, battery idle, importing"); may be empty.</param>
    /// <exception cref="InvalidOperationException">
    /// There is no such device to read, as in setup mode, or its driver keeps no registers (a charger reached
    /// over HTTP).
    /// </exception>
    /// <remarks>Throws whatever the link throws when the device does not answer.</remarks>
    Task<RegisterSnapshotFile> TakeAsync(PvDeviceRole role, string note, CancellationToken cancellationToken = default);
}

/// <summary>A snapshot as the browser saves it.</summary>
/// <param name="FileName">
/// A name that says what and when: <c>gleanvolt-registers-solax-20261108-213000.json</c> for the inverter,
/// <c>gleanvolt-registers-solax-charger-20261108-213000.json</c> for the charger.
/// </param>
/// <param name="Json">The register dump.</param>
public sealed record RegisterSnapshotFile(string FileName, string Json);
