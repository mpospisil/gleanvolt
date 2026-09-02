namespace Gleanvolt.Core.Models;

/// <summary>
/// Everything <see cref="VehicleFeedComparison"/> has counted, as one immutable snapshot (issue #141).
///
/// <para>The four questions Phase 3 of #137 has to answer read straight off this: cadence from
/// <see cref="VehicleFeedTally.MeanGap"/> and the buckets beside it, agreement from
/// <see cref="Pairs"/>, coverage from the per-field counts, and survival from
/// <see cref="VehicleFeedDiagnostics.Sessions"/>, which is the feed's own business and stays there.</para>
/// </summary>
/// <param name="ObservingSince">When counting started, which is when the process did.</param>
/// <param name="ObservedFor">
/// How long it has been counting. The figure to read <b>first</b>: every other number here is worth
/// exactly as much as the window it was measured over, and a week is what #141 asks for.
/// </param>
/// <param name="Sources">One entry per feed that has offered a reading, busiest first.</param>
/// <param name="Pairs">
/// One entry per pair of feeds that have described the same battery close enough in time to be
/// compared. Empty on an installation running a single feed, which is not a fault — it is the state
/// the handover deliberately arrives at.
/// </param>
public sealed record VehicleFeedReport(
    DateTimeOffset ObservingSince,
    TimeSpan ObservedFor,
    IReadOnlyList<VehicleFeedTally> Sources,
    IReadOnlyList<VehicleFeedPair> Pairs)
{
    /// <summary>Nothing has been offered yet.</summary>
    public static VehicleFeedReport Empty { get; } =
        new(DateTimeOffset.MinValue, TimeSpan.Zero, [], []);

    /// <summary>Whether two or more feeds have been seen, which is the arrangement #141 measures.</summary>
    public bool BothFeedsSeen => Sources.Count > 1;
}

/// <summary>
/// One feed's delivery record: how much it offered, how much of that was news, how far apart the news
/// was, and which fields it actually carried.
/// </summary>
/// <param name="SourceId">
/// The name the feed put on its readings — <c>VehicleState.SourceId</c>, the field #73 added for
/// exactly this. Feeds name themselves; nothing here dispatches on the name.
/// </param>
/// <param name="Offered">Readings handed to the holder, news or not.</param>
/// <param name="Captures">
/// Readings carrying a capture time this feed had not produced before. <b>This is the delivery
/// count</b>, and the one the cadence is measured over.
/// </param>
/// <param name="Repeats">
/// Readings whose capture time this feed had <i>already</i> produced — the portal handing back the
/// bundle it handed back last time, or a retained MQTT message replayed on reconnect. Ordinary, and
/// the reason cadence is measured on capture times rather than on fetches.
/// </param>
/// <param name="Regressions">
/// Readings carrying a capture time <b>older</b> than one this feed has already produced: the feed's
/// own account of the car going backwards.
///
/// <para><b>Counted apart from <paramref name="Repeats"/> because it is a different fault.</b> A
/// repeat is a feed with nothing new to say. A regression is a feed reaching back past a snapshot it
/// has already shown us, which is what a portal whose continuous data request has stopped filling
/// looks like — and from every other angle it is indistinguishable from a quiet car. Counted together,
/// as they were at first, it hides behind a column that reads as harmless.</para>
/// </param>
/// <param name="WorstRegression">
/// The largest step backwards seen, or null if there has been none. The figure that separates a
/// timestamp jittering by a minute from a feed stuck on this morning.
/// </param>
/// <param name="Superseded">
/// Readings the holder declined because another feed was already holding something at least as fresh.
/// A feed that is nearly always superseded is a feed the dashboard is not showing, however healthy it
/// looks on its own page.
/// </param>
/// <param name="FirstCapturedAt">The car's capture time on this feed's first delivery.</param>
/// <param name="LastCapturedAt">And on its most recent one.</param>
/// <param name="GapCount">How many intervals between successive deliveries have been seen — one fewer than <paramref name="Captures"/>.</param>
/// <param name="ShortestGap">The closest two deliveries have ever been.</param>
/// <param name="MeanGap">The average interval. The number that answers "does it hold the cadence it claims?".</param>
/// <param name="LongestGap">The worst dropout. Read beside the buckets: one bad night and a bad week look alike in this figure alone.</param>
/// <param name="GapBuckets">
/// How many intervals fell in each band of <see cref="VehicleFeedComparison.GapBucketEdges"/>, plus a
/// final open-ended one. Counting the gaps rather than eyeballing them, which is what #141 asks for.
/// </param>
/// <param name="SocReadings">Deliveries that carried a state of charge.</param>
/// <param name="RangeReadings">Deliveries that carried the car's own range estimate.</param>
/// <param name="ChargeTimeReadings">
/// Deliveries that carried a charge-time-remaining. #73 never established whether this car reports one
/// at all, and a week of zero here is the answer.
/// </param>
/// <param name="ChargeStateReadings">Deliveries whose charge state was something other than Unknown.</param>
/// <param name="PlugStateReadings">Deliveries whose plug state was something other than Unknown.</param>
/// <param name="LastReading">
/// This feed's own most recent reading, whatever became of it.
///
/// <para><b>The only place a losing reading survives.</b> <see cref="VehicleStateHolder"/> keeps one
/// state — the newest — so the feed that came second leaves nothing behind but a count. While two
/// feeds are being compared, each one's own account of the car is worth showing beside the other's:
/// the dashboard's card can then say which feed it is quoting <i>and</i> what the other one would
/// have said, which is the difference between "the portal is healthy" and "the portal is delivering".</para>
///
/// <para>Never null once <paramref name="Captures"/> is non-zero, and display only — nothing plans on
/// it, and the reading the rest of the controller uses is still whatever the holder holds.</para>
/// </param>
public sealed record VehicleFeedTally(
    string SourceId,
    int Offered,
    int Captures,
    int Repeats,
    int Regressions,
    TimeSpan? WorstRegression,
    int Superseded,
    DateTimeOffset? FirstCapturedAt,
    DateTimeOffset? LastCapturedAt,
    int GapCount,
    TimeSpan? ShortestGap,
    TimeSpan? MeanGap,
    TimeSpan? LongestGap,
    IReadOnlyList<int> GapBuckets,
    int SocReadings,
    int RangeReadings,
    int ChargeTimeReadings,
    int ChargeStateReadings,
    int PlugStateReadings,
    VehicleState? LastReading = null)
{
    /// <summary>
    /// Every reading that was not a delivery, however it failed to be one. <see cref="Offered"/> is
    /// this plus <see cref="Captures"/>, always.
    /// </summary>
    public int NotNews => Repeats + Regressions;

    /// <summary>Whether this feed's account of the car has ever gone backwards.</summary>
    public bool HasRegressed => Regressions > 0;

    /// <summary>
    /// How often a field arrived, as a fraction of deliveries, or null before there is anything to
    /// divide by. Absent is a legitimate answer for every field but the capture time (#73), so this
    /// is a coverage figure and not a failure rate.
    /// </summary>
    public double? Coverage(int readings) => Captures == 0 ? null : (double)readings / Captures;
}

/// <summary>
/// Two feeds' accounts of the same battery, compared (issue #141).
/// </summary>
/// <param name="First">The alphabetically earlier source id; the sign of every delta is <i>first minus second</i>.</param>
/// <param name="Second">The other one.</param>
/// <param name="All">Every comparison made.</param>
/// <param name="Quiet">
/// Only those where neither feed said the car was charging. <b>The subset that can answer the
/// question</b>: a parked car's state of charge does not drift, so a difference here is one of the two
/// feeds being read wrong, whereas a difference measured across twenty minutes of charging is the car
/// doing its job.
/// </param>
public sealed record VehicleFeedPair(
    string First,
    string Second,
    VehicleSocAgreement All,
    VehicleSocAgreement Quiet);

/// <summary>
/// How closely two feeds' states of charge agree.
/// </summary>
/// <param name="Samples">How many comparisons this is averaged over.</param>
/// <param name="MeanDelta">
/// The average signed difference, first minus second. <b>The systematic offset.</b> Near zero with a
/// non-trivial <paramref name="MeanAbsDelta"/> is noise; a persistent few points is one feed reading
/// the car differently from the other, and #141 says it matters which.
/// </param>
/// <param name="MeanAbsDelta">The average size of the difference, ignoring its direction — the noise.</param>
/// <param name="MaxAbsDelta">The worst single disagreement seen.</param>
/// <param name="MeanSeparation">
/// How far apart in time the two capture times typically were. Every figure above is worth only as
/// much as this is small: two readings twenty minutes apart may differ because the car moved on.
/// </param>
public sealed record VehicleSocAgreement(
    int Samples,
    double MeanDelta,
    double MeanAbsDelta,
    double MaxAbsDelta,
    TimeSpan MeanSeparation)
{
    /// <summary>Nothing comparable has arrived.</summary>
    public static VehicleSocAgreement None { get; } = new(0, 0, 0, 0, TimeSpan.Zero);
}
