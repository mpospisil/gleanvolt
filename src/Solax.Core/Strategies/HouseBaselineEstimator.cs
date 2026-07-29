namespace Solax.Core.Strategies;

/// <summary>
/// A rolling estimate of household consumption excluding the EV charger, used to work out how much of
/// the forecast is spoken for before either battery sees it.
///
/// <para>Seeded from configuration and blended towards the observed mean as samples arrive, so the
/// plan is sane from the first poll after a restart rather than waiting a day to learn. Deliberately
/// simple: an exponentially-weighted mean over the recent past, not a per-hour profile — nothing is
/// persisted across restarts anywhere in this service yet, and a learned profile that resets on every
/// deploy would be worse than an honest average.</para>
/// </summary>
public sealed class HouseBaselineEstimator
{
    private readonly double _seedWatts;
    private readonly double _smoothing;
    private readonly int _minSamples;

    private double _mean;
    private int _samples;

    /// <param name="seedWatts">The configured baseline, used until enough samples have accumulated.</param>
    /// <param name="smoothing">
    /// Weight given to each new sample (0..1]. The default 0.001 at a 5-second poll gives a time
    /// constant of roughly 1.5 hours — slow enough that a kettle doesn't move the day plan.
    /// </param>
    /// <param name="minSamples">How many samples before the estimate is used instead of the seed.</param>
    public HouseBaselineEstimator(double seedWatts, double smoothing = 0.001, int minSamples = 60)
    {
        _seedWatts = Math.Max(0, seedWatts);
        _smoothing = Math.Clamp(smoothing, 0.0001, 1);
        _minSamples = Math.Max(1, minSamples);
        _mean = _seedWatts;
    }

    /// <summary>The current baseline in watts: the seed until <c>minSamples</c> are in, then the mean.</summary>
    public double BaselineWatts => _samples >= _minSamples ? _mean : _seedWatts;

    /// <summary>Whether the estimate is running on observed data rather than the configured seed.</summary>
    public bool IsLearned => _samples >= _minSamples;

    /// <summary>Feeds one observation of household load (EV and battery excluded) and returns the baseline.</summary>
    public double Add(double houseLoadWatts)
    {
        // Negative readings are noise around zero (or a momentary accounting artefact in the residual);
        // folding them in would bias the baseline low and make the plan optimistic.
        var sample = Math.Max(0, houseLoadWatts);

        _mean += _smoothing * (sample - _mean);
        _samples++;
        return BaselineWatts;
    }
}
