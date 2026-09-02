namespace Gleanvolt.Core.Models;

/// <summary>
/// What came of asking a manufacturer's portal for the car — the reading if it arrived, and otherwise
/// why it did not, in words a page can show.
///
/// <para>The <b>kind</b> of failure is carried separately from the sentence because the kinds need
/// different things from the owner: a consent screen wants a browser and must never be retried, an
/// expired session is ordinary and wants the button pressed again. A single "it didn't work" would
/// throw away the only part the owner can act on. See <see cref="IsWorthRetrying"/>.</para>
///
/// <para><see cref="UnmappedFields"/> is here for a reason that will expire: the portal's vocabulary
/// was written from a description rather than a capture, so a missing SOC usually means the field has
/// a name nothing here reads yet. Showing the list turns a week of wondering into one glance.</para>
/// </summary>
/// <param name="Succeeded">Whether a usable reading came back.</param>
/// <param name="State">The reading, or null when it did not.</param>
/// <param name="Vehicle">Which car answered, masked for display.</param>
/// <param name="SnapshotCount">How many report snapshots the download held.</param>
/// <param name="OldestSnapshot">The earliest capture time in the bundle, for judging its spread.</param>
/// <param name="NewestSnapshot">The latest capture time, which is what <see cref="State"/> is quoted at.</param>
/// <param name="TargetSocPercent">The car's own charge limit, when the portal carried one. Nothing reads it yet.</param>
/// <param name="OdometerKm">The odometer, when carried. Nothing reads it yet.</param>
/// <param name="UnmappedFields">Field names in the bundle that nothing here recognises.</param>
/// <param name="FailureKind">A short name for what went wrong, or null on success.</param>
/// <param name="Message">The failure in a sentence, or null on success.</param>
/// <param name="IsWorthRetrying">
/// Whether pressing the button again could plausibly work. False for the two that need a human: a
/// refused password, and a screen only a browser can answer.
/// </param>
public sealed record VehiclePortalReading(
    bool Succeeded,
    VehicleState? State = null,
    string? Vehicle = null,
    int SnapshotCount = 0,
    DateTimeOffset? OldestSnapshot = null,
    DateTimeOffset? NewestSnapshot = null,
    double? TargetSocPercent = null,
    double? OdometerKm = null,
    IReadOnlyList<string>? UnmappedFields = null,
    string? FailureKind = null,
    string? Message = null,
    bool IsWorthRetrying = false)
{
    /// <summary>The unrecognised field names, never null.</summary>
    public IReadOnlyList<string> Unmapped => UnmappedFields ?? [];

    /// <param name="unmapped">
    /// The field names nothing recognised, when the failure <b>is</b> that nothing was recognised.
    /// A failed read is exactly when this list is most worth having, and dropping it here is what
    /// used to leave the page saying "unusable data" while holding the answer in its hand.
    /// </param>
    public static VehiclePortalReading Failed(
        string kind, string message, bool worthRetrying, IReadOnlyList<string>? unmapped = null) =>
        new(false, FailureKind: kind, Message: message, IsWorthRetrying: worthRetrying,
            UnmappedFields: unmapped);
}
