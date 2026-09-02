using Gleanvolt.Core.Enums;

namespace Gleanvolt.Core.Models;

/// <summary>
/// What each vehicle feed has actually delivered, counted rather than eyeballed (issue #141).
///
/// <para>Phase 3 of #137 runs both feeds — the Home Assistant topic and the manufacturer's own portal —
/// side by side for a week and then decides between them <b>on numbers</b>: how often each one really
/// produces a fresh reading, whether the two agree about the same battery, and which fields either of
/// them carries at all. This is the instrument that produces those numbers. Nothing reads it: it is
/// observation, on no hardware path, and switching it off would change no charging decision.</para>
///
/// <para><b>Every reading is counted, including the ones the holder throws away.</b> That is the whole
/// reason this lives inside <see cref="VehicleStateHolder"/> rather than beside it: the holder keeps
/// the newest reading and discards an older one, so a feed that is consistently second would otherwise
/// leave no trace at all — and "consistently second" is exactly the finding that decides the handover.</para>
///
/// <para><b>A new reading is not the same as a new capture.</b> The portal is asked every fifteen
/// minutes and will happily hand back the delivery it handed back last time; a retained MQTT message is
/// replayed on every reconnect. Counting those as deliveries would report a cadence the car never had,
/// so the cadence here is measured between <i>distinct</i> <see cref="VehicleState.CapturedAt"/> values
/// and a re-delivery is counted separately as a repeat.</para>
///
/// <para><b>Everything is since this process started, and that is deliberate.</b> The measurement is of
/// an <i>unattended run</i> — a gap that spans a restart is the restart, not the feed, and folding the
/// two together would quietly turn a redeployment into evidence against the portal. A controller that
/// has been up for two days says two days, and the week starts again from there. Same rule, and the
/// same reason, as <see cref="VehicleFeedDiagnostics"/>.</para>
/// </summary>
public sealed class VehicleFeedComparison
{
    /// <summary>
    /// How far apart two feeds' capture times may be and still be treated as describing the same
    /// moment. Wide, because that is the honest width: the portal is a quarter-hour batch and the
    /// integration behind the topic has a lag of its own, so a narrower window would mostly measure
    /// which feed happened to land first rather than whether they agree. The cost is paid back by
    /// reporting <see cref="VehicleSocAgreement.MeanSeparation"/> beside every figure, so a difference
    /// measured across twenty minutes is never mistaken for a difference measured at one instant.
    /// </summary>
    public static readonly TimeSpan PairingWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The upper edge of each cadence bucket; anything longer falls in a final open-ended one, so there
    /// is always one more bucket than there are edges here.
    ///
    /// <para>Chosen around the two claims being tested rather than around round numbers: the portal
    /// says a quarter of an hour, so 20 minutes is "it kept its word" and 45 is "it missed one".
    /// Everything past two hours is a dropout of the kind the charger's Modbus link produces about
    /// forty-five times a day, and past six hours the feed was effectively down.</para>
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> GapBucketEdges =
    [
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(20),
        TimeSpan.FromMinutes(45),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(6),
    ];

    private readonly object _gate = new();
    private readonly Dictionary<string, Tally> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string First, string Second), Pair> _pairs = [];
    private readonly TimeProvider _time;

    public VehicleFeedComparison(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        ObservingSince = _time.GetUtcNow();
    }

    /// <summary>When this instrument started watching — the process's start, in practice.</summary>
    public DateTimeOffset ObservingSince { get; }

    /// <summary>
    /// What a feed named itself with when it named itself nothing. A reading with no
    /// <see cref="VehicleState.SourceId"/> still has to be counted as coming from <i>somewhere</i>, and
    /// lumping every anonymous feed together under one name is better than dropping them: on an
    /// installation with one unnamed feed it reads correctly, and on one with two it is visibly a
    /// configuration to fix rather than a measurement quietly averaging two cars.
    /// </summary>
    public const string UnnamedSource = "unnamed";

    /// <summary>
    /// Counts one reading offered to the holder.
    /// </summary>
    /// <param name="state">The reading, whatever became of it.</param>
    /// <param name="taken">
    /// Whether the holder kept it. False means another feed was already holding something at least as
    /// fresh, which is the count that says which feed is leading.
    /// </param>
    public void Record(VehicleState state, bool taken)
    {
        var source = string.IsNullOrWhiteSpace(state.SourceId) ? UnnamedSource : state.SourceId.Trim();

        lock (_gate)
        {
            if (!_sources.TryGetValue(source, out var tally))
            {
                tally = new Tally(source);
                _sources[source] = tally;
            }

            var isNewCapture = tally.Add(state, taken);

            if (isNewCapture)
            {
                PairAgainstOthers(source, state);
            }
        }
    }

    /// <summary>Everything counted so far, as a snapshot that will not change under the caller.</summary>
    public VehicleFeedReport Report()
    {
        lock (_gate)
        {
            var sources = _sources.Values
                .OrderByDescending(tally => tally.Captures)
                .ThenBy(tally => tally.SourceId, StringComparer.OrdinalIgnoreCase)
                .Select(tally => tally.ToRecord())
                .ToList();

            var pairs = _pairs
                .OrderByDescending(entry => entry.Value.All.Samples)
                .Select(entry => new VehicleFeedPair(
                    entry.Key.First, entry.Key.Second, entry.Value.All.ToRecord(), entry.Value.Quiet.ToRecord()))
                .ToList();

            return new VehicleFeedReport(ObservingSince, _time.GetUtcNow() - ObservingSince, sources, pairs);
        }
    }

    /// <summary>
    /// Compares this fresh capture with what every other feed last said, when the two are close enough
    /// in time to be talking about the same battery.
    ///
    /// <para>The pair is keyed in a fixed order so that the signed mean means something: a persistent
    /// <c>+3</c> is one feed reading three points above the other every time, which is the systematic
    /// offset #141 is looking for, and it would average to nothing if the sign flipped with whichever
    /// feed happened to arrive second.</para>
    /// </summary>
    private void PairAgainstOthers(string source, VehicleState state)
    {
        if (state.SocPercent is not { } soc)
        {
            return;
        }

        foreach (var other in _sources.Values)
        {
            if (string.Equals(other.SourceId, source, StringComparison.OrdinalIgnoreCase)
                || other.Last is not { SocPercent: { } otherSoc } last)
            {
                continue;
            }

            var separation = state.CapturedAt - last.CapturedAt;
            var apart = separation < TimeSpan.Zero ? -separation : separation;

            if (apart > PairingWindow)
            {
                continue;
            }

            var ordered = string.Compare(source, other.SourceId, StringComparison.OrdinalIgnoreCase) <= 0;
            var key = ordered ? (source, other.SourceId) : (other.SourceId, source);
            var delta = ordered ? soc - otherSoc : otherSoc - soc;

            if (!_pairs.TryGetValue(key, out var pair))
            {
                pair = new Pair();
                _pairs[key] = pair;
            }

            pair.All.Add(delta, apart);

            // A car that is charging genuinely does change between two capture times, so a difference
            // measured across that is drift and not disagreement. The quiet subset is the one that can
            // answer "is one of these two being read wrong?", because a parked car's state of charge
            // does not move.
            if (state.ChargeState != VehicleChargeState.Charging
                && last.ChargeState != VehicleChargeState.Charging)
            {
                pair.Quiet.Add(delta, apart);
            }
        }
    }

    /// <summary>One feed's running totals. Mutable, and only ever touched under the lock.</summary>
    private sealed class Tally(string sourceId)
    {
        private int _repeats;
        private int _regressions;
        private TimeSpan _worstRegression;

        private readonly int[] _buckets = new int[GapBucketEdges.Count + 1];
        private TimeSpan _gapTotal;

        public string SourceId { get; } = sourceId;

        public VehicleState? Last { get; private set; }

        public int Offered { get; private set; }

        public int Captures { get; private set; }

        public int Superseded { get; private set; }

        private DateTimeOffset? _firstCapturedAt;
        private int _gapCount;
        private TimeSpan? _shortestGap;
        private TimeSpan? _longestGap;
        private int _soc;
        private int _range;
        private int _chargeTime;
        private int _chargeState;
        private int _plugState;

        /// <returns>Whether this reading carried a capture time this feed had not produced before.</returns>
        public bool Add(VehicleState state, bool taken)
        {
            Offered++;

            if (!taken)
            {
                Superseded++;
            }

            // Not newer than what this feed last said is not news, and there are two ways of not
            // being news which want telling apart.
            //
            // A REPEAT is the same capture time again: the portal handing back the bundle it handed
            // back last time, or a retained MQTT message replayed on reconnect. Ordinary, and the
            // reason cadence is measured on capture times at all.
            //
            // A REGRESSION is an OLDER capture time than this feed has already produced -- the feed's
            // own account of the car going backwards. That is a different and more serious thing: a
            // portal whose delivery reaches back past a snapshot it has already shown us has a
            // continuous data request that has stopped filling, and it looks exactly like a quiet car
            // until somebody notices the timestamp. Lumping the two together, which this did at
            // first, hides it behind a column that reads as harmless.
            if (Last is { } previous && state.CapturedAt <= previous.CapturedAt)
            {
                if (state.CapturedAt == previous.CapturedAt)
                {
                    _repeats++;
                }
                else
                {
                    _regressions++;

                    var backwards = previous.CapturedAt - state.CapturedAt;
                    if (backwards > _worstRegression)
                    {
                        _worstRegression = backwards;
                    }
                }

                return false;
            }

            if (Last is { } last)
            {
                var gap = state.CapturedAt - last.CapturedAt;
                _gapCount++;
                _gapTotal += gap;
                _shortestGap = _shortestGap is { } shortest && shortest <= gap ? shortest : gap;
                _longestGap = _longestGap is { } longest && longest >= gap ? longest : gap;
                _buckets[BucketFor(gap)]++;
            }
            else
            {
                _firstCapturedAt = state.CapturedAt;
            }

            Last = state;
            Captures++;

            if (state.SocPercent is not null) { _soc++; }
            if (state.RangeKm is not null) { _range++; }
            if (state.ChargeTimeRemaining is not null) { _chargeTime++; }
            if (state.ChargeState != VehicleChargeState.Unknown) { _chargeState++; }
            if (state.PlugState != VehiclePlugState.Unknown) { _plugState++; }

            return true;
        }

        public VehicleFeedTally ToRecord() => new(
            SourceId,
            Offered,
            Captures,
            _repeats,
            _regressions,
            _regressions == 0 ? null : _worstRegression,
            Superseded,
            _firstCapturedAt,
            Last?.CapturedAt,
            _gapCount,
            _shortestGap,
            _gapCount == 0 ? null : _gapTotal / _gapCount,
            _longestGap,
            [.. _buckets],
            _soc,
            _range,
            _chargeTime,
            _chargeState,
            _plugState,
            Last);

        private static int BucketFor(TimeSpan gap)
        {
            for (var index = 0; index < GapBucketEdges.Count; index++)
            {
                if (gap <= GapBucketEdges[index])
                {
                    return index;
                }
            }

            return GapBucketEdges.Count;
        }
    }

    /// <summary>Two feeds compared, all of it and the parked-car subset of it.</summary>
    private sealed class Pair
    {
        public Accumulator All { get; } = new();

        public Accumulator Quiet { get; } = new();
    }

    private sealed class Accumulator
    {
        private double _sum;
        private double _absSum;
        private double _max;
        private TimeSpan _separation;

        public int Samples { get; private set; }

        public void Add(double delta, TimeSpan apart)
        {
            Samples++;
            _sum += delta;
            _absSum += Math.Abs(delta);
            _max = Math.Max(_max, Math.Abs(delta));
            _separation += apart;
        }

        public VehicleSocAgreement ToRecord() => Samples == 0
            ? VehicleSocAgreement.None
            : new VehicleSocAgreement(Samples, _sum / Samples, _absSum / Samples, _max, _separation / Samples);
    }
}
