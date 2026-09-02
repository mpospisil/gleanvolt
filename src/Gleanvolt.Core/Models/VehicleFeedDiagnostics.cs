namespace Gleanvolt.Core.Models;

/// <summary>
/// What a vehicle feed can say about its own running, beyond whether it currently works (issue #140).
///
/// <para><b>Deliberately not part of <see cref="Interfaces.IVehicleUpdateService"/>.</b> That contract
/// is five members and stays that way: a service whose API pushes rather than polls has no session to
/// age and no interval to be due on, and making it invent both would be the contract growing to fit
/// one implementation. A feed that has these offers them; a feed that has not is not diminished.</para>
///
/// <para>Every figure here is since the process started. Nothing is persisted, because the question
/// this answers — "is the feed actually running, and is it holding a session?" — is about the process
/// in front of you.</para>
/// </summary>
/// <param name="Attempts">Fetches made.</param>
/// <param name="Readings">Fetches that produced a reading. The gap between the two is the failures.</param>
/// <param name="LastAttemptAt">When the last fetch was made, or null before the first.</param>
/// <param name="LastReadingAt">
/// When the last <i>successful</i> fetch was made — not the car's capture time, which is on the
/// reading itself and is usually older.
/// </param>
/// <param name="NextDueAt">
/// When the next fetch is due, on the delay the service last asked for. Null when it has stopped
/// asking, which is what a feed blocked on its owner does.
/// </param>
/// <param name="Sessions">
/// How many times this feed has signed in since the process started. <b>One is the healthy answer</b>,
/// however long it has been running: more means sessions are expiring, which is the measurement
/// issue #138 could not make.
/// </param>
/// <param name="SessionAge">How long the current session has been alive, or null when there is none.</param>
public sealed record VehicleFeedDiagnostics(
    int Attempts,
    int Readings,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastReadingAt,
    DateTimeOffset? NextDueAt,
    int Sessions,
    TimeSpan? SessionAge)
{
    /// <summary>Nothing has happened yet.</summary>
    public static VehicleFeedDiagnostics None { get; } = new(0, 0, null, null, null, 0, null);
}
