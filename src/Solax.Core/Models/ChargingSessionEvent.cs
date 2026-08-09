using Solax.Core.Enums;

namespace Solax.Core.Models;

/// <summary>
/// Something that happened during a session, as opposed to something that was merely measured. Sparse
/// by design: a handful of rows per session, each carrying a sentence a human can read.
/// </summary>
/// <param name="Detail">
/// The reason in words — typically the controller's own <c>Reason</c> string, which is already written
/// for a human because it goes into the log line.
/// </param>
public sealed record ChargingSessionEvent(
    Guid SessionId,
    DateTimeOffset Timestamp,
    ChargingSessionEventKind Kind,
    string Detail);
