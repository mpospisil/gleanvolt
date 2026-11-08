namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// Settings → "Save register snapshot" (issue #319): one read of every register the running inverter driver
/// uses, as a file. It is how a tester sends a recording of a family Gleanvolt does not drive yet (#325),
/// and how a driver's conformance tests get their fixtures.
///
/// <para>Reads only, on the inverter's own client, so it takes its turn with the poll loop rather than
/// opening a second connection the inverter may not allow.</para>
/// </summary>
public interface IInverterSnapshot
{
    /// <summary>Takes the snapshot.</summary>
    /// <param name="note">What is happening at the time ("night, battery idle, importing"); may be empty.</param>
    /// <exception cref="InvalidOperationException">There is no inverter to read, as in setup mode.</exception>
    /// <remarks>Throws whatever the link throws when the inverter does not answer.</remarks>
    Task<RegisterSnapshotFile> TakeAsync(string note, CancellationToken cancellationToken = default);
}

/// <summary>A snapshot as the browser saves it.</summary>
/// <param name="FileName">A name that says what and when: <c>gleanvolt-registers-solax-20261108-213000.json</c>.</param>
/// <param name="Json">The register dump.</param>
public sealed record RegisterSnapshotFile(string FileName, string Json);
