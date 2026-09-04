namespace Gleanvolt.Core.Models;

/// <summary>
/// What came of asking the car, now, because somebody wanted to know (issue #168).
///
/// <para>Distinct from <see cref="VehicleState"/> itself because a refresh can fail while a reading
/// still exists: the feed may be unreachable and the last answer still worth showing, provided its
/// age is shown with it. Callers get both facts rather than having to infer one from the other.</para>
/// </summary>
/// <param name="Succeeded">Whether the source answered with a usable reading.</param>
/// <param name="State">
/// The reading — the fresh one on success, or the last known one when the refresh failed and there
/// was something to fall back on. Null when neither.
/// </param>
/// <param name="IsFresh">
/// Whether <see cref="State"/> is what this refresh fetched, as opposed to what was already held. The
/// difference matters on a page that promised to show only what was just asked for.
/// </param>
/// <param name="Source">Which feed answered, for display. Null when none did.</param>
/// <param name="Message">Why it failed, in words a page can show. Null on success.</param>
public sealed record VehicleRefreshResult(
    bool Succeeded,
    VehicleState? State = null,
    bool IsFresh = false,
    string? Source = null,
    string? Message = null)
{
    /// <summary>Nothing is configured to ask, so nothing was asked.</summary>
    public static VehicleRefreshResult NoFeed { get; } =
        new(false, Message: "No vehicle feed is configured, so there is nothing to ask.");

    public static VehicleRefreshResult Fresh(VehicleState state, string? source) =>
        new(true, state, IsFresh: true, Source: source);

    /// <summary>
    /// The ask failed. <paramref name="lastKnown"/> is handed back when there is one — a plan built on
    /// an old number that says so is worth more than no plan at all.
    /// </summary>
    public static VehicleRefreshResult Failed(string message, VehicleState? lastKnown = null) =>
        new(false, lastKnown, IsFresh: false, Source: lastKnown?.SourceId, Message: message);
}
