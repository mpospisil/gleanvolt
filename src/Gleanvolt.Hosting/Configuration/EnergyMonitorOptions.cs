namespace Gleanvolt.Hosting.Configuration;

/// <summary>
/// Configuration for the energy monitor. Bound from the <c>"EnergyMonitor"</c> section.
///
/// <para>Like <c>SessionStore</c>, and unlike the features that drive hardware, this is <b>on by
/// default</b>: it only ever observes, reads no register the poll loop wasn't reading anyway, and
/// writes to no device. A history that only starts accumulating once somebody discovers the feature
/// exists is worth very little — the whole value of this table is that it goes back far enough to
/// answer a question nobody had thought to ask yet.</para>
/// </summary>
public sealed class EnergyMonitorOptions
{
    public const string SectionName = "EnergyMonitor";

    /// <summary>Whether intervals are recorded at all. Setting it false makes the worker inert.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The SQLite file, relative to the content root unless absolute. In the container this must
    /// resolve onto a bind mount, or the history dies with the container.
    ///
    /// <para>Deliberately not the session database. See <c>SqliteEnergyIntervalStore</c> for why the
    /// two stores keep separate files, and how to query across them anyway.</para>
    /// </summary>
    public string Path { get; init; } = "data/energy.db";

    /// <summary>
    /// The bucket length — how much of the day one row covers.
    ///
    /// <para>Must divide 24 hours evenly, or a short stub bucket would appear at midnight whose
    /// figures no other row is comparable with; an interval that doesn't is rejected at startup and
    /// 15 minutes is used instead. Fifteen is the default because it is what electricity markets,
    /// distribution tariffs and half-hourly settlement data are all divisible into, so a series
    /// recorded at it can be aggregated to anything a later analysis wants.</para>
    ///
    /// <para>Cost scales the way you would expect: 15 minutes is 96 rows a day, about 35,000 a year
    /// and a few megabytes. Five minutes is three times that and still nothing on any device this
    /// runs on — the reason not to go finer is that the roof's own variation stops being signal, not
    /// that the disk cares.</para>
    /// </summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long a silence may be integrated across before it counts as lost time rather than as a
    /// steady reading. Beyond it the open bucket is closed short — leaving its <c>covered_seconds</c>
    /// visibly below the interval — and a fresh one opens when polling resumes, rather than the last
    /// power before an outage being held across the whole outage and inventing energy nobody measured.
    /// </summary>
    public TimeSpan MaxGap { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long intervals are kept, in days. Pruned once at startup. <b>Zero — the default — disables
    /// pruning and keeps everything</b>, which is the opposite of the session store's 365 days and
    /// deliberately so: this table exists to be looked at years later, and at 15-minute resolution a
    /// decade of it is still a file you could email.
    /// </summary>
    public int RetentionDays { get; init; }
}
