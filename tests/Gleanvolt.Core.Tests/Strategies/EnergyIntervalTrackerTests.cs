using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

/// <summary>
/// The bucketing rules, exercised without a database: where a boundary splits the energy, what a gap
/// does to coverage, and what makes a forecast figure null rather than zero.
/// </summary>
public class EnergyIntervalTrackerTests
{
    // Deliberately on a bucket boundary, so a test that wants to straddle one has to say so.
    private static readonly DateTimeOffset Noon = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Quarter = TimeSpan.FromMinutes(15);

    private static ChargeControlStatus Status(
        DateTimeOffset at,
        double solarWatts = 6000,
        double gridWatts = 0,
        double evWatts = 4000,
        double batteryWatts = 0,
        double soc = 80) => new(
        Mode: ChargeControlMode.Solar,
        DryRun: false,
        HoldingControl: true,
        State: ChargeControlState.Charging,
        SurplusWatts: 2000,
        TargetCurrentAmps: 6,
        ActiveCurrentAmps: 6,
        BatterySocPercent: soc,
        ChargerStatus: EvChargerStatus.Charging,
        CarConnected: true,
        SolarPowerWatts: solarWatts,
        ForecastSolarPowerWatts: solarWatts,
        EvChargerPowerWatts: evWatts,
        EvChargingCurrentAmps: 6,
        BatteryPowerWatts: batteryWatts,
        GridPowerWatts: gridWatts,
        BatteryHoldEnabled: false,
        BatteryHoldRequested: false,
        BatteryHoldActive: false,
        BatteryHoldTargetWatts: null,
        Plan: null,
        LoanPowerWatts: 0,
        SessionEnergyWh: 0,
        LoanedTodayWh: 0,
        TomorrowForecastWh: null,
        Timestamp: at);

    private static EnergyIntervalTracker NewTracker(TimeSpan? maxGap = null) =>
        new(Quarter, maxGap ?? TimeSpan.FromMinutes(5), TimeProvider.System);

    [Fact]
    public void TheFirstSnapshotOpensABucketAndCompletesNothing()
    {
        var tracker = NewTracker();

        var completed = tracker.Observe(Status(Noon.AddMinutes(3)), forecastPowerWatts: 6000);

        Assert.Empty(completed);

        // The bucket it opened is the grid slot the snapshot fell in, not the snapshot's own instant.
        Assert.Equal(Noon, tracker.OpenPeriodStart);
    }

    [Fact]
    public void BucketsAlignToTheIntervalGridWhateverTimeTheServiceStarted()
    {
        var tracker = NewTracker();

        tracker.Observe(Status(Noon.AddMinutes(7).AddSeconds(23)), forecastPowerWatts: null);

        Assert.Equal(Noon, tracker.OpenPeriodStart);
    }

    [Fact]
    public void ASteadyQuarterHourIsIntegratedIntoOneRow()
    {
        var tracker = NewTracker();
        var completed = Run(tracker, Noon, Quarter + TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
            solarWatts: 6000, evWatts: 4000);

        var interval = Assert.Single(completed);

        Assert.Equal(Noon, interval.PeriodStart);
        Assert.Equal(Noon.AddMinutes(15), interval.PeriodEnd);

        // 6 kW and 4 kW held for a quarter hour.
        Assert.Equal(1.5, interval.SolarKwh, 6);
        Assert.Equal(1.0, interval.EvKwh, 6);
        Assert.Equal(Quarter, interval.Covered);
        Assert.Equal(1.0, interval.Coverage, 6);
    }

    [Fact]
    public void EnergyStraddlingABoundaryIsSplitBetweenTheTwoBuckets()
    {
        var tracker = NewTracker();

        // One snapshot two minutes before the boundary and the next two minutes after it: the 3.6 kW
        // held across the gap belongs 2/4 to each side, not wholly to whichever bucket was open when
        // the second snapshot arrived.
        tracker.Observe(Status(Noon.AddMinutes(13), solarWatts: 3600, evWatts: 0), forecastPowerWatts: null);
        var completed = tracker.Observe(Status(Noon.AddMinutes(17), solarWatts: 0, evWatts: 0), forecastPowerWatts: null);

        var closed = Assert.Single(completed);
        Assert.Equal(3600 * (2.0 / 60) / 1000, closed.SolarKwh, 6);
        Assert.Equal(TimeSpan.FromMinutes(2), closed.Covered);

        // The other two minutes are still in the open bucket; flushing proves where they went.
        var rest = Assert.Single(tracker.Flush(Noon.AddMinutes(17)));
        Assert.Equal(Noon.AddMinutes(15), rest.PeriodStart);
        Assert.Equal(3600 * (2.0 / 60) / 1000, rest.SolarKwh, 6);
    }

    [Fact]
    public void ImportAndExportAreSeparateColumnsAndNeverNettedAgainstEachOther()
    {
        var tracker = NewTracker();

        // Seven and a half minutes importing 2 kW, then the same exporting 2 kW: a net of zero, which
        // is exactly what must not be recorded.
        Run(tracker, Noon, TimeSpan.FromMinutes(7.5), TimeSpan.FromSeconds(30), gridWatts: 2000);
        var completed = Run(tracker, Noon.AddMinutes(7.5), TimeSpan.FromMinutes(7.5) + TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30), gridWatts: -2000);

        var interval = Assert.Single(completed);
        Assert.Equal(0.25, interval.GridImportKwh, 3);
        Assert.Equal(0.25, interval.GridExportKwh, 3);
    }

    [Fact]
    public void BatteryChargeAndDischargeAreKeptApartTheSameWay()
    {
        var tracker = NewTracker();

        Run(tracker, Noon, TimeSpan.FromMinutes(7.5), TimeSpan.FromSeconds(30), batteryWatts: 2000);
        var completed = Run(tracker, Noon.AddMinutes(7.5), TimeSpan.FromMinutes(7.5) + TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30), batteryWatts: -2000);

        var interval = Assert.Single(completed);
        Assert.Equal(0.25, interval.BatteryChargeKwh, 3);
        Assert.Equal(0.25, interval.BatteryDischargeKwh, 3);
    }

    [Fact]
    public void TheEnergyBalanceClosesOnHouseLoad()
    {
        var tracker = NewTracker();

        // 6 kW of sun: 4 to the car, 1 into the battery, 1 left for the house — so nothing crosses
        // the meter and the residual must come out at 1 kW for the quarter hour.
        var completed = Run(tracker, Noon, Quarter + TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
            solarWatts: 6000, evWatts: 4000, batteryWatts: 1000, gridWatts: 0);

        var interval = Assert.Single(completed);
        Assert.Equal(1.25, interval.HouseLoadKwh, 6);
        Assert.Equal(0.25, interval.OtherLoadsKwh, 6);
    }

    [Fact]
    public void WithNoForecastAtAllTheForecastFigureIsNullRatherThanZero()
    {
        var tracker = NewTracker();
        var completed = Run(tracker, Noon, Quarter + TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
            forecastWatts: null);

        Assert.Null(Assert.Single(completed).ForecastSolarKwh);
    }

    [Fact]
    public void AForecastOfZeroIsRecordedAsZeroBecauseNightIsAPrediction()
    {
        var tracker = NewTracker();
        var completed = Run(tracker, Noon, Quarter + TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
            solarWatts: 0, forecastWatts: 0);

        Assert.Equal(0, Assert.Single(completed).ForecastSolarKwh);
    }

    [Fact]
    public void ASilenceLongerThanTheGapAllowanceEndsTheBucketShortInsteadOfInventingEnergy()
    {
        var tracker = NewTracker();

        tracker.Observe(Status(Noon.AddMinutes(1), solarWatts: 6000), forecastPowerWatts: null);
        tracker.Observe(Status(Noon.AddMinutes(2), solarWatts: 6000), forecastPowerWatts: null);

        // The inverter goes unreachable for ten minutes, longer than the five-minute allowance.
        var completed = tracker.Observe(Status(Noon.AddMinutes(12), solarWatts: 6000), forecastPowerWatts: null);

        var interrupted = Assert.Single(completed);
        Assert.Equal(Noon, interrupted.PeriodStart);

        // One minute of 6 kW, and a row that says so rather than one claiming eleven.
        Assert.Equal(TimeSpan.FromMinutes(1), interrupted.Covered);
        Assert.Equal(0.1, interrupted.SolarKwh, 6);
        Assert.Equal(1.0 / 15, interrupted.Coverage, 6);

        // Polling resumed inside the same quarter hour, so that is the bucket now filling.
        Assert.Equal(Noon, tracker.OpenPeriodStart);
    }

    [Fact]
    public void SocIsAveragedOverTimeNotOverSamples()
    {
        // A gap allowance wide enough to let one reading stand for twelve minutes, which is the point
        // of the test: the weighting must follow the clock, not the number of snapshots.
        var tracker = NewTracker(maxGap: TimeSpan.FromMinutes(15));

        // 50% stands for twelve minutes, 90% for three. A sample average of the three readings would
        // say ~63%; the time-weighted answer is 58%.
        tracker.Observe(Status(Noon, soc: 50), forecastPowerWatts: null);
        tracker.Observe(Status(Noon.AddMinutes(12), soc: 90), forecastPowerWatts: null);
        var completed = tracker.Observe(Status(Noon.AddMinutes(15), soc: 90), forecastPowerWatts: null);

        var interval = Assert.Single(completed);
        Assert.Equal(58, interval.SocMeanPercent, 6);
        Assert.Equal(50, interval.SocStartPercent);
        Assert.Equal(90, interval.SocEndPercent);
        Assert.Equal(50, interval.SocMinPercent);
        Assert.Equal(90, interval.SocMaxPercent);
    }

    [Fact]
    public void ConsecutiveBucketsJoinUpOnSoc()
    {
        var tracker = NewTracker();

        tracker.Observe(Status(Noon.AddMinutes(14), soc: 62), forecastPowerWatts: null);
        var closed = Assert.Single(tracker.Observe(Status(Noon.AddMinutes(16), soc: 64), forecastPowerWatts: null));
        var open = Assert.Single(tracker.Flush(Noon.AddMinutes(16)));

        // The SOC standing at the boundary ends one bucket and starts the next, so a chart drawn from
        // the rows has no gap between them.
        Assert.Equal(62, closed.SocEndPercent);
        Assert.Equal(62, open.SocStartPercent);
    }

    [Fact]
    public void FlushWritesThePartFinishedBucketSoAShutdownKeepsIt()
    {
        var tracker = NewTracker();
        Run(tracker, Noon, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), solarWatts: 6000);

        var completed = tracker.Flush(Noon.AddMinutes(5));

        var partial = Assert.Single(completed);
        Assert.Equal(Noon, partial.PeriodStart);
        Assert.Equal(TimeSpan.FromMinutes(5), partial.Covered);
        Assert.Equal(0.5, partial.SolarKwh, 6);

        // Nothing is left open afterwards, so a second flush cannot write the same minutes twice.
        Assert.Null(tracker.OpenPeriodStart);
        Assert.Empty(tracker.Flush(Noon.AddMinutes(6)));
    }

    [Fact]
    public void AnIntervalThatCannotTileADayIsRejectedInFavourOfFifteenMinutes()
    {
        Assert.True(EnergyIntervalTracker.IsValidInterval(TimeSpan.FromMinutes(15)));
        Assert.True(EnergyIntervalTracker.IsValidInterval(TimeSpan.FromHours(1)));
        Assert.False(EnergyIntervalTracker.IsValidInterval(TimeSpan.FromMinutes(7)));
        Assert.False(EnergyIntervalTracker.IsValidInterval(TimeSpan.Zero));
        Assert.False(EnergyIntervalTracker.IsValidInterval(TimeSpan.FromDays(2)));

        Assert.Equal(TimeSpan.FromMinutes(15), new EnergyIntervalTracker(TimeSpan.FromMinutes(7)).Interval);
    }

    [Fact]
    public void ASnapshotThatArrivesOutOfOrderIntegratesNothing()
    {
        var tracker = NewTracker();
        tracker.Observe(Status(Noon.AddMinutes(5), solarWatts: 6000), forecastPowerWatts: null);

        // A snapshot stamped earlier than the last one: the clock did not run backwards, so neither
        // may the integration. It updates the held readings and adds no time.
        Assert.Empty(tracker.Observe(Status(Noon.AddMinutes(4), solarWatts: 6000), forecastPowerWatts: null));

        tracker.Observe(Status(Noon.AddMinutes(9), solarWatts: 6000), forecastPowerWatts: null);

        // Four minutes, from the in-order snapshot at +5 -- not the five the out-of-order one would
        // have implied, and not the minus-one it would have subtracted.
        var partial = Assert.Single(tracker.Flush(Noon.AddMinutes(9)));
        Assert.Equal(TimeSpan.FromMinutes(4), partial.Covered);
        Assert.Equal(0.4, partial.SolarKwh, 6);
    }

    // Feeds snapshots at a fixed cadence over [start, start + duration) and returns whatever the last
    // one completed.
    private static IReadOnlyList<EnergyInterval> Run(
        EnergyIntervalTracker tracker,
        DateTimeOffset start,
        TimeSpan duration,
        TimeSpan cadence,
        double solarWatts = 6000,
        double gridWatts = 0,
        double evWatts = 4000,
        double batteryWatts = 0,
        double soc = 80,
        double? forecastWatts = 6000)
    {
        IReadOnlyList<EnergyInterval> completed = [];

        for (var offset = TimeSpan.Zero; offset < duration; offset += cadence)
        {
            var result = tracker.Observe(
                Status(start + offset, solarWatts, gridWatts, evWatts, batteryWatts, soc),
                forecastWatts);

            if (result.Count > 0)
            {
                completed = result;
            }
        }

        return completed;
    }
}
