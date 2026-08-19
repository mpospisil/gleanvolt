using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// Persists the site's energy history as fixed-length intervals. The energy monitor is the only
/// writer; analytics reads through the query member, or straight out of the file with SQL.
///
/// <para>Deliberately separate from <see cref="IChargingSessionStore"/> rather than an extra method on
/// it. The two answer different questions — "what did this charge cost?" against "what did the site
/// do all year?" — keep different retention, and are written by different services; a store that did
/// both would have to be opened, migrated and failed as one thing.</para>
///
/// <para>Like the session store, every operation is best-effort from the caller's point of view: this
/// observes and nothing more, and no failure here may ever be allowed to disturb polling or charge
/// control.</para>
/// </summary>
public interface IEnergyIntervalStore
{
    /// <summary>Prepares the store — creates or migrates the schema. Safe to call on an existing file.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes completed intervals, keyed on their start.
    ///
    /// <para><b>Merging, not replacing.</b> A restart part-way through a window produces two partial
    /// rows for the same start — one written as the old process stopped, one as the new one gets
    /// going — and an implementation must add them together rather than let the second overwrite the
    /// first. Otherwise every deploy would silently delete the minutes before it.</para>
    /// </summary>
    Task AppendAsync(IReadOnlyList<EnergyInterval> intervals, CancellationToken cancellationToken);

    /// <summary>Intervals starting within <c>[from, to)</c>, oldest first.</summary>
    Task<IReadOnlyList<EnergyInterval>> GetIntervalsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes intervals that started longer ago than <paramref name="retention"/>. Returns how many
    /// were removed.
    /// </summary>
    Task<int> PruneAsync(TimeSpan retention, CancellationToken cancellationToken);
}
