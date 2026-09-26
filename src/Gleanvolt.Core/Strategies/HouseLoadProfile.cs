using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

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
/// <para><b>Nothing of its own is persisted, but it does not start from nothing</b> (issue #229). The
/// learning is slow on purpose, around three days of an hour to settle, and the Pi is redeployed more
/// often than that, so a profile that restarted from the seed was almost never learned at all. On
/// 2026-09-26, 33 hours after a deploy, the afternoon hours were still about 80 % seed: the plan
/// expected 1.3 kWh of house between 16:00 and 19:00, the site's own history said 2.4, and the evening
/// floor was built on the 1.3. The energy history already holds every quarter hour the house drew, so
/// <see cref="SeedFrom"/> rebuilds the hours from it at startup, and live samples carry on from there.</para>
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

    /// <summary>
    /// How much stored history an hour needs before <see cref="SeedFrom"/> trusts it over the seed: two
    /// days of that hour. One day is a single evening, and a single evening is exactly the sample the
    /// slow smoothing exists to keep from rewriting the profile.
    /// </summary>
    public static readonly TimeSpan MinSeedCoverage = TimeSpan.FromHours(2);

    /// <summary>
    /// Replaces each hour's mean with what the house drew in that hour across <paramref name="intervals"/>
    /// — the energy the house used with the car taken out, divided by the time actually observed, so a
    /// quarter hour the service spent restarting counts for the minutes it saw and no more.
    ///
    /// <para>A mean of energy rather than a median of readings, because the planner sums energy: the
    /// evening's two-kilowatt bursts are energy the pack does not get, however rare they are.</para>
    ///
    /// <para>An hour with less than <see cref="MinSeedCoverage"/> of history is left as it was. One with
    /// enough counts as learned straight away, and live samples go on adjusting it at the usual rate.</para>
    /// </summary>
    /// <returns>How many hours were seeded.</returns>
    public int SeedFrom(IEnumerable<EnergyInterval> intervals)
    {
        ArgumentNullException.ThrowIfNull(intervals);

        var energyWh = new double[24];
        var covered = new TimeSpan[24];

        foreach (var interval in intervals)
        {
            if (interval.Covered <= TimeSpan.Zero)
            {
                continue;
            }

            // Clamped per row for the reason Add clamps a reading: a slightly negative residual is meter
            // disagreement, and summing it in would bias the hour low.
            var hour = interval.PeriodStart.LocalDateTime.Hour;
            energyWh[hour] += Math.Max(0, interval.OtherLoadsKwh) * 1000;
            covered[hour] += interval.Covered;
        }

        var seeded = 0;
        for (var hour = 0; hour < 24; hour++)
        {
            if (covered[hour] < MinSeedCoverage)
            {
                continue;
            }

            _hourlyMean[hour] = energyWh[hour] / covered[hour].TotalHours;
            _hourlySamples[hour] = Math.Max(_hourlySamples[hour], _minSamplesPerHour);
            seeded++;
        }

        return seeded;
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
