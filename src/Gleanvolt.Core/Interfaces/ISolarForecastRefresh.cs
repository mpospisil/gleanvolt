namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// What a refresh scheduler reads from the held forecast to decide when to ask again: whether the sun
/// is up, and when it next will be. Asking is <see cref="ISolarForecastSource.RequestRefreshAsync"/>, on
/// whichever source is active (issue #387).
///
/// <para><b>Why this is separate from <see cref="ISolarForecastService"/>.</b> Reading a forecast to
/// decide on charging and reading one to pace the fetches are different jobs with different consumers:
/// every reader in the codebase — the polling loop, the day planner, the API — depends on the service,
/// and only the refresh worker on this.</para>
/// </summary>
public interface ISolarForecastRefresh
{
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
