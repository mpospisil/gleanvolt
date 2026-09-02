using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Tests;

/// <summary>
/// The instrument Phase 3 of #137 decides the handover on (issue #141). Every test here is one of the
/// four questions that week has to answer, or one of the ways of getting the answer wrong.
/// </summary>
public class VehicleFeedComparisonTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static VehicleState Reading(
        TimeSpan offset,
        double? soc = 50,
        string source = "vw-group",
        VehicleChargeState charging = VehicleChargeState.Idle) =>
        new(Noon + offset, SocPercent: soc, ChargeState: charging, SourceId: source);

    [Fact]
    public void Nothing_offered_is_an_empty_report_rather_than_a_missing_one()
    {
        var report = new VehicleFeedComparison(new TestClock(Noon)).Report();

        Assert.Empty(report.Sources);
        Assert.Empty(report.Pairs);
        Assert.False(report.BothFeedsSeen);
    }

    [Fact]
    public void A_feed_is_grouped_by_the_name_it_gives_itself()
    {
        var comparison = new VehicleFeedComparison(new TestClock(Noon));

        comparison.Record(Reading(TimeSpan.Zero, source: "mqtt"), taken: true);
        comparison.Record(Reading(TimeSpan.FromMinutes(5), source: "vw-group"), taken: true);

        Assert.Equal(["mqtt", "vw-group"], comparison.Report().Sources.Select(s => s.SourceId).Order());
    }

    [Fact]
    public void An_unnamed_reading_is_still_counted()
    {
        // Dropping it would be worse: a feed that names nothing is a configuration to fix, and a
        // measurement that silently ignored it would report a cadence for a feed that is not there.
        var comparison = new VehicleFeedComparison(new TestClock(Noon));

        comparison.Record(new VehicleState(Noon, SocPercent: 50), taken: true);

        Assert.Equal(VehicleFeedComparison.UnnamedSource, Assert.Single(comparison.Report().Sources).SourceId);
    }

    [Fact]
    public void A_re_delivered_bundle_is_a_repeat_and_not_a_delivery()
    {
        // The whole reason cadence is measured on capture times: the portal is asked every quarter of
        // an hour and will hand back what it handed back last time, and a retained MQTT message is
        // replayed on every reconnect. Counting those would report a cadence the car never had.
        var comparison = new VehicleFeedComparison(new TestClock(Noon));

        comparison.Record(Reading(TimeSpan.Zero), taken: true);
        comparison.Record(Reading(TimeSpan.Zero), taken: true);
        comparison.Record(Reading(TimeSpan.Zero), taken: true);

        var source = Assert.Single(comparison.Report().Sources);

        Assert.Equal(3, source.Offered);
        Assert.Equal(1, source.Captures);
        Assert.Equal(2, source.Repeats);
        Assert.Equal(0, source.GapCount);
    }

    [Fact]
    public void An_older_reading_is_a_repeat_too_and_never_moves_the_cadence_backwards()
    {
        var comparison = new VehicleFeedComparison(new TestClock(Noon));

        comparison.Record(Reading(TimeSpan.FromMinutes(30)), taken: true);
        comparison.Record(Reading(TimeSpan.Zero), taken: false);

        var source = Assert.Single(comparison.Report().Sources);

        Assert.Equal(1, source.Captures);
        Assert.Null(source.MeanGap);
        Assert.Equal(Noon.AddMinutes(30), source.LastCapturedAt);
    }

    [Fact]
    public void The_readings_the_holder_threw_away_are_the_ones_that_say_who_is_leading()
    {
        // The reason this lives inside the holder: a feed that is consistently second leaves no trace
        // anywhere else, and "consistently second" is the finding that decides the handover.
        var comparison = new VehicleFeedComparison(new TestClock(Noon));

        comparison.Record(Reading(TimeSpan.Zero, source: "mqtt"), taken: true);
        comparison.Record(Reading(TimeSpan.FromMinutes(-5), source: "vw-group"), taken: false);
        comparison.Record(Reading(TimeSpan.FromMinutes(-4), source: "vw-group"), taken: false);

        var behind = comparison.Report().Sources.Single(s => s.SourceId == "vw-group");

        Assert.Equal(2, behind.Captures);
        Assert.Equal(2, behind.Superseded);
    }

    [Fact]
    public void The_cadence_is_the_spread_of_the_intervals_and_not_only_their_mean()
    {
        // One bad night and a bad week look identical in a mean and a maximum, which is why the
        // buckets exist.
        var comparison = new VehicleFeedComparison(new TestClock(Noon));
        var at = TimeSpan.Zero;

        foreach (var minutes in new[] { 15, 15, 15, 240 })
        {
            comparison.Record(Reading(at), taken: true);
            at += TimeSpan.FromMinutes(minutes);
        }

        comparison.Record(Reading(at), taken: true);

        var source = Assert.Single(comparison.Report().Sources);

        Assert.Equal(4, source.GapCount);
        Assert.Equal(TimeSpan.FromMinutes(15), source.ShortestGap);
        Assert.Equal(TimeSpan.FromHours(4), source.LongestGap);
        Assert.Equal(TimeSpan.FromMinutes((15 * 3 + 240) / 4.0), source.MeanGap);

        // Three inside the quarter-hour band it claims, one dropout past two hours.
        Assert.Equal([0, 3, 0, 0, 1, 0], source.GapBuckets);
    }

    [Fact]
    public void A_gap_falls_in_the_band_its_edge_names()
    {
        var comparison = new VehicleFeedComparison(new TestClock(Noon));

        comparison.Record(Reading(TimeSpan.Zero), taken: true);
        comparison.Record(Reading(VehicleFeedComparison.GapBucketEdges[1]), taken: true);

        Assert.Equal([0, 1, 0, 0, 0, 0], Assert.Single(comparison.Report().Sources).GapBuckets);
    }

    [Fact]
    public void Coverage_counts_the_fields_that_arrived_over_the_deliveries_that_could_have_carried_them()
    {
        var comparison = new VehicleFeedComparison(new TestClock(Noon));

        comparison.Record(
            new VehicleState(Noon, SocPercent: 50, RangeKm: 210, SourceId: "vw-group"), taken: true);
        comparison.Record(
            new VehicleState(
                Noon.AddMinutes(15),
                SocPercent: 51,
                ChargeTimeRemaining: TimeSpan.FromMinutes(40),
                PlugState: VehiclePlugState.Connected,
                SourceId: "vw-group"),
            taken: true);

        var source = Assert.Single(comparison.Report().Sources);

        Assert.Equal(2, source.SocReadings);
        Assert.Equal(1, source.RangeReadings);
        Assert.Equal(1, source.ChargeTimeReadings);
        Assert.Equal(0, source.ChargeStateReadings);
        Assert.Equal(1, source.PlugStateReadings);
        Assert.Equal(1.0, source.Coverage(source.SocReadings));
        Assert.Equal(0.5, source.Coverage(source.RangeReadings));
    }

    [Fact]
    public void Coverage_is_null_rather_than_zero_before_there_is_anything_to_divide_by()
    {
        Assert.Null(new VehicleFeedTally(
            "vw-group", 0, 0, 0, null, null, 0, null, null, null, [], 0, 0, 0, 0, 0).Coverage(0));
    }

    [Fact]
    public void A_systematic_offset_keeps_its_sign()
    {
        // The finding #141 is looking for: not that two numbers differ, but that one differs the same
        // way every time. Averaging the direction away would hide exactly that.
        var comparison = new VehicleFeedComparison(new TestClock(Noon));

        for (var quarter = 0; quarter < 4; quarter++)
        {
            var at = TimeSpan.FromMinutes(15 * quarter);
            comparison.Record(Reading(at, soc: 60, source: "mqtt"), taken: true);
            comparison.Record(Reading(at + TimeSpan.FromMinutes(1), soc: 57, source: "vw-group"), taken: true);
        }

        var pair = Assert.Single(comparison.Report().Pairs);

        Assert.Equal("mqtt", pair.First);
        Assert.Equal("vw-group", pair.Second);
        Assert.Equal(3, pair.All.MeanDelta, 3);
        Assert.Equal(3, pair.All.MeanAbsDelta, 3);
        Assert.Equal(3, pair.All.MaxAbsDelta, 3);
    }

    [Fact]
    public void The_sign_does_not_depend_on_which_feed_happened_to_arrive_second()
    {
        var comparison = new VehicleFeedComparison(new TestClock(Noon));

        comparison.Record(Reading(TimeSpan.Zero, soc: 60, source: "mqtt"), taken: true);
        comparison.Record(Reading(TimeSpan.FromMinutes(1), soc: 57, source: "vw-group"), taken: true);
        comparison.Record(Reading(TimeSpan.FromMinutes(15), soc: 60, source: "mqtt"), taken: true);

        var pair = Assert.Single(comparison.Report().Pairs);

        Assert.Equal(2, pair.All.Samples);
        Assert.Equal(3, pair.All.MeanDelta, 3);
    }

    [Fact]
    public void A_charging_car_is_kept_out_of_the_subset_that_answers_the_question()
    {
        // A parked car's state of charge does not drift, so a difference there is one of the feeds
        // being read wrong. A difference measured across twenty minutes of charging is the car doing
        // its job, and would otherwise be reported as disagreement.
        var comparison = new VehicleFeedComparison(new TestClock(Noon));

        comparison.Record(Reading(TimeSpan.Zero, soc: 60, source: "mqtt"), taken: true);
        comparison.Record(Reading(TimeSpan.FromMinutes(1), soc: 59, source: "vw-group"), taken: true);

        comparison.Record(
            Reading(TimeSpan.FromMinutes(15), soc: 70, source: "mqtt", charging: VehicleChargeState.Charging),
            taken: true);
        comparison.Record(
            Reading(TimeSpan.FromMinutes(16), soc: 62, source: "vw-group", charging: VehicleChargeState.Charging),
            taken: true);

        var pair = Assert.Single(comparison.Report().Pairs);

        Assert.Equal(3, pair.All.Samples);
        Assert.Equal(1, pair.Quiet.Samples);
        Assert.Equal(1, pair.Quiet.MeanDelta, 3);
    }

    [Fact]
    public void Two_readings_too_far_apart_in_time_are_not_a_comparison()
    {
        var comparison = new VehicleFeedComparison(new TestClock(Noon));

        comparison.Record(Reading(TimeSpan.Zero, soc: 60, source: "mqtt"), taken: true);
        comparison.Record(
            Reading(VehicleFeedComparison.PairingWindow + TimeSpan.FromMinutes(1), soc: 40, source: "vw-group"),
            taken: true);

        Assert.Empty(comparison.Report().Pairs);
    }

    [Fact]
    public void A_reading_without_a_state_of_charge_is_not_compared()
    {
        var comparison = new VehicleFeedComparison(new TestClock(Noon));

        comparison.Record(Reading(TimeSpan.Zero, soc: 60, source: "mqtt"), taken: true);
        comparison.Record(Reading(TimeSpan.FromMinutes(1), soc: null, source: "vw-group"), taken: true);

        Assert.Empty(comparison.Report().Pairs);
    }

    [Fact]
    public void The_window_it_was_measured_over_is_reported_beside_the_numbers()
    {
        // The figure to read first: every other number is worth exactly as much as the time behind it.
        var clock = new TestClock(Noon);
        var comparison = new VehicleFeedComparison(clock);

        clock.Now = Noon.AddDays(7);

        var report = comparison.Report();

        Assert.Equal(Noon, report.ObservingSince);
        Assert.Equal(TimeSpan.FromDays(7), report.ObservedFor);
    }

    [Fact]
    public void Each_feed_keeps_its_own_last_reading_including_the_one_the_holder_discarded()
    {
        // The only place a losing reading survives: the holder keeps one state, so without this the
        // feed that came second leaves nothing behind but a count.
        var holder = new VehicleStateHolder();

        holder.Set(new VehicleState(Noon, SocPercent: 62, SourceId: "mqtt"));
        holder.Set(new VehicleState(Noon.AddMinutes(-12), SocPercent: 58, SourceId: "vw-group"));

        var report = holder.Comparison.Report();

        Assert.Equal(62, report.Sources.Single(s => s.SourceId == "mqtt").LastReading!.SocPercent);
        Assert.Equal(58, report.Sources.Single(s => s.SourceId == "vw-group").LastReading!.SocPercent);
        // And the holder itself is unchanged by any of this: it still holds only the newest.
        Assert.Equal(62, holder.GetCurrentState()!.SocPercent);
    }

    [Fact]
    public void A_repeat_does_not_replace_the_last_reading_with_an_older_one()
    {
        var comparison = new VehicleFeedComparison(new TestClock(Noon));

        comparison.Record(Reading(TimeSpan.FromMinutes(30), soc: 61), taken: true);
        comparison.Record(Reading(TimeSpan.Zero, soc: 40), taken: false);

        Assert.Equal(61, Assert.Single(comparison.Report().Sources).LastReading!.SocPercent);
    }

    [Fact]
    public void The_holder_counts_every_offer_including_the_ones_it_declines()
    {
        var holder = new VehicleStateHolder();

        Assert.True(holder.Set(new VehicleState(Noon, SocPercent: 69, SourceId: "mqtt")));
        Assert.False(holder.Set(new VehicleState(Noon.AddMinutes(-20), SocPercent: 60, SourceId: "vw-group")));

        var report = holder.Comparison.Report();

        Assert.True(report.BothFeedsSeen);
        Assert.Equal(1, report.Sources.Single(s => s.SourceId == "vw-group").Superseded);
        Assert.Equal(0, report.Sources.Single(s => s.SourceId == "mqtt").Superseded);
    }
}
