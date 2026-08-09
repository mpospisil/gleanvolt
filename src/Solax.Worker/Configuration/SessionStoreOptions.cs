namespace Solax.Worker.Configuration;

/// <summary>
/// Configuration for the charging session store (issue #32). Bound from the <c>"SessionStore"</c>
/// section.
///
/// <para>Unlike <c>BatteryHold</c> and <c>ChargeControl</c>, this feature is <b>on by default</b>:
/// it only ever observes. It reads no register the poll loop wasn't reading anyway and writes to no
/// device, so the reasoning that keeps the hardware-writing features off out of the box doesn't apply
/// — and a store that only starts collecting once somebody notices it exists is worth very little.</para>
/// </summary>
public sealed class SessionStoreOptions
{
    public const string SectionName = "SessionStore";

    /// <summary>Whether sessions are recorded at all. Setting it false makes the worker inert.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The SQLite file, relative to the content root unless absolute. In the container this must
    /// resolve onto a bind mount, or the history dies with the container.
    /// </summary>
    public string Path { get; init; } = "data/sessions.db";

    /// <summary>
    /// How often a sample is stored. Deliberately much coarser than <c>Solax:PollIntervalSeconds</c>:
    /// every poll is still integrated into the running energy totals, but storing one row per poll
    /// would be ~2,900 rows for a four-hour session and buy nothing a 30-second cadence doesn't
    /// already show. Transitions are never blurred by it — any change forces a sample regardless.
    /// </summary>
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long samples may sit in memory before being committed. Batching is what keeps this feature
    /// from writing to the SD card every few seconds; the cost is that an ungraceful power cut loses
    /// at most this much of the current session. Session starts and ends bypass it and commit at once,
    /// so the header is never the thing that goes missing.
    /// </summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long closed sessions are kept, in days. Pruned once at startup. Zero or negative disables
    /// pruning and keeps everything.
    /// </summary>
    public int RetentionDays { get; init; } = 365;

    /// <summary>
    /// Also record sessions where a car is plugged in but no mode is driving the charger, as a
    /// baseline to compare controlled sessions against. Off by default: it records sessions we did
    /// nothing to, which is a different question from the one this store was built to answer.
    /// </summary>
    public bool RecordUncontrolledSessions { get; init; }
}
