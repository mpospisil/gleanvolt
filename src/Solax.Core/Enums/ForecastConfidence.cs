namespace Solax.Core.Enums;

/// <summary>
/// Which of the provider's estimate bands the day plan is built on. Solcast returns three figures per
/// period: a median (<c>pv_estimate</c>) and a 10th/90th-percentile pair.
/// </summary>
public enum ForecastConfidence
{
    /// <summary>
    /// The 10th percentile — "it will almost certainly make at least this much". The conservative
    /// choice, and the default: planning the evening 100% guarantee against the median means missing
    /// it roughly half the time. Falls back to the median on providers/periods that omit it.
    /// </summary>
    P10,

    /// <summary>The median estimate. Optimistic for a guarantee, but the honest expected value.</summary>
    P50,

    /// <summary>The 90th percentile. Included for completeness; not a sane basis for a guarantee.</summary>
    P90,
}
