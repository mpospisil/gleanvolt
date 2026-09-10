namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// The refreshing half of a forecast source: what a scheduler needs in order to decide when to ask
/// again, and to ask.
///
/// <para><b>Why this is separate from <see cref="ISolarForecastService"/>.</b> Reading a forecast and
/// maintaining one are different jobs with different consumers. Every reader in the codebase — the
/// polling loop, the day planner, the API — depends on the interface and can therefore be pointed at
/// any implementation. <c>SolarForecastRefreshWorker</c> could not: it took the concrete
/// <c>SolcastForecastService</c>, which made it the one place in the forecast path that a second
/// implementation could not replace. A host that fetches its forecast from somewhere else had to
/// remove the worker's service descriptor to get rid of it, and until it did, the process kept
/// demanding a Solcast API key it had no use for.</para>
///
/// <para>The split is worth having on its own merits: a source that is pushed to rather than polled
/// implements the reader and not this, and says so in its type rather than in a comment.</para>
/// </summary>
public interface ISolarForecastRefresh
{
    /// <summary>
    /// Fetches a new forecast and publishes it.
    ///
    /// <para><b>Does not throw for a failed fetch.</b> Implementations keep whatever they already
    /// held — a transient outage or a rate limit must not blank a good forecast — so a caller pacing
    /// this in a loop needs no error handling of its own, and a bad fetch does not stop later ones.</para>
    /// </summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Expected PV power at an instant from the forecast already held, or null when nothing is held
    /// or the instant falls outside it. Used to tell day from night without spending a call.
    /// </summary>
    double? ExpectedPowerWattsNow(DateTimeOffset instant);

    /// <summary>
    /// When the sun is next forecast to rise above <paramref name="thresholdWatts"/>, or null when
    /// nothing held says. Lets a scheduler sleep through the dark, where a new forecast cannot change
    /// any decision but still costs an API call.
    /// </summary>
    DateTimeOffset? NextDaylightStart(DateTimeOffset after, double thresholdWatts);
}
