using Solax.Core.Models;

namespace Solax.Core.Interfaces;

/// <summary>
/// Persists recorded charging sessions. The recording worker is the only writer; readers (a future
/// uploader, a read API, a viewer) go through the query members.
///
/// <para>Implementations must treat every operation as best-effort from the caller's point of view:
/// this is an observation feature, and no failure here may ever be allowed to disturb polling or
/// charge control.</para>
/// </summary>
public interface IChargingSessionStore
{
    /// <summary>
    /// Prepares the store — creates or migrates the schema — and closes any session left open by a
    /// previous run, ending it at its last recorded sample with
    /// <see cref="Enums.ChargingSessionEndReason.Interrupted"/>. Returns the number of sessions so
    /// recovered.
    /// </summary>
    Task<int> InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Writes the header of a session that has just opened.</summary>
    Task StartSessionAsync(ChargingSession session, CancellationToken cancellationToken);

    /// <summary>Appends a batch of samples and events. Both may be empty.</summary>
    Task AppendAsync(
        IReadOnlyList<ChargingSessionSample> samples,
        IReadOnlyList<ChargingSessionEvent> events,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes the final header of a closed session: end time, end reason, end SOC and the totals.
    /// After this the session is immutable.
    /// </summary>
    Task CompleteSessionAsync(ChargingSession session, CancellationToken cancellationToken);

    /// <summary>Session headers that started within <c>[from, to)</c>, newest first.</summary>
    Task<IReadOnlyList<ChargingSession>> GetSessionsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);

    /// <summary>
    /// A closed session as a publishable document, or null when the id is unknown. Open sessions are
    /// returned too — a viewer wants to watch one in progress — but only a closed one is safe to
    /// upload, because only a closed one will never change again.
    /// </summary>
    Task<ChargingSessionDocument?> ExportAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes closed sessions that ended longer ago than <paramref name="retention"/>, with their
    /// samples and events. Returns how many were removed.
    /// </summary>
    Task<int> PruneAsync(TimeSpan retention, CancellationToken cancellationToken);
}
