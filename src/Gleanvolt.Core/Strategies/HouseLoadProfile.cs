using Gleanvolt.Core.Interfaces;

namespace Gleanvolt.Core.Strategies;

/// <summary>
/// A learned hour-of-day profile of household consumption (EV excluded): 24 buckets, each an
/// exponentially-weighted mean of what the house drew during that hour, seeded from configuration
/// until enough has been observed.
///
/// <para><b>Why not a single rolling average.</b> The first implementation was one EWMA with a ~1.4 h
/// time constant, which sounds slow but is fast compared with a day: it tracked the diurnal curve, so
/// by mid-afternoon it reported the afternoon peak and the planner projected that flat across the
/// remaining hours. On the reference site that turned a morning plan of "33.6 kWh available, window
/// 08:00–16:30" into "no EV charging today" by noon, on a day whose forecast was accurate to 5%. A
/// per-hour profile answers the question the planner actually asks — "what will the house draw between
/// now and the deadline?" — rather than "what is it drawing at this second?".</para>
///
/// <para>Nothing is persisted, so a restart falls back to the seed and re-learns over a few days;
/// consistent with the rest of the service.</para>
/// </summary>
public sealed class HouseLoadProfile : IHouseLoadProfile
{
    private readonly double _seedWatts;
    private readonly double _smoothing;
    private readonly int _minSamplesPerHour;
    private readonly double[] _hourlyMean = new double[24];
    private readonly int[] _hourlySamples = new int[24];

    /// <param name="seedWatts">Baseline used for any hour that hasn't been observed enough yet.</param>
    /// <param name="smoothing">
    /// Weight of each new sample within its hour bucket (0..1]. The default gives a bucket a time
    /// constant of roughly three days' worth of that hour, so the profile settles within a few days
    /// but a single unusual afternoon doesn't rewrite it.
    /// </param>
    /// <param name="minSamplesPerHour">Samples an hour needs before it is trusted over the seed.</param>
    public HouseLoadProfile(double seedWatts, double smoothing = 0.0005, int minSamplesPerHour = 120)
    {
        _seedWatts = Math.Max(0, seedWatts);
        _smoothing = Math.Clamp(smoothing, 0.00001, 1);
        _minSamplesPerHour = Math.Max(1, minSamplesPerHour);
        Array.Fill(_hourlyMean, _seedWatts);
    }

    /// <summary>Whether every hour of the day has been observed enough to be trusted.</summary>
    public bool IsFullyLearned => _hourlySamples.All(s => s >= _minSamplesPerHour);

    /// <summary>The mean of the profile across the whole day, in watts — a one-number summary for logs.</summary>
    public double DailyMeanWatts => Enumerable.Range(0, 24).Average(WattsForHour);

    /// <summary>Feeds one observation. <paramref name="instant"/> is used for its local hour.</summary>
    public double Add(DateTimeOffset instant, double houseLoadWatts)
    {
        // Negative readings are noise around zero (or a momentary artefact in the residual); folding
        // them in would bias the profile low and make the plan optimistic.
        var sample = Math.Max(0, houseLoadWatts);
        var hour = instant.LocalDateTime.Hour;

        _hourlyMean[hour] += _smoothing * (sample - _hourlyMean[hour]);
        _hourlySamples[hour]++;
        return WattsForHour(hour);
    }

    public double ExpectedWattsAt(DateTimeOffset instant) => WattsForHour(instant.LocalDateTime.Hour);

    /// <summary>The learned profile, hour by hour, for diagnostics.</summary>
    public IReadOnlyList<double> HourlyWatts => [.. Enumerable.Range(0, 24).Select(WattsForHour)];

    private double WattsForHour(int hour) =>
        _hourlySamples[hour] >= _minSamplesPerHour ? _hourlyMean[hour] : _seedWatts;
}
