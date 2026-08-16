using Microsoft.Extensions.Options;
using Solax.Core.Enums;
using Solax.Core.Interfaces;
using Solax.Core.Models;
using Solax.Core.Strategies;
using Solax.Hosting.Configuration;

namespace Solax.Hosting.Forecasting;

/// <summary>
/// Owns everything about "how does today look": it feeds the house-load profile and the forecast
/// accuracy tracker from each telemetry reading, builds the <see cref="SolarDayPlan"/> for the
/// forecast-driven charge mode, and does all the logging around it.
///
/// <para>The planner and tracker in <c>Solax.Core</c> stay pure; this is where their output meets the
/// clock, the log and the day boundary. Singleton — it accumulates today's energy totals.</para>
/// </summary>
public sealed class DayPlanProvider
{
    /// <summary>Floor on how often the day-plan line may be logged at Information. See <see cref="LogPlan"/>.</summary>
    private static readonly TimeSpan MinimumPlanLogInterval = TimeSpan.FromMinutes(5);

    private readonly ISolarForecastService _forecast;
    private readonly ForecastChargeOptions _options;
    private readonly IForecastRuntimeSettings? _runtime;
    private readonly ChargePowerConverter _power;
    private readonly int _minChargingCurrentAmps;
    private readonly ILogger<DayPlanProvider> _logger;
    private readonly TimeProvider _timeProvider;

    private readonly HouseLoadProfile _houseLoad;
    private readonly ForecastAccuracyTracker _accuracy;
    private readonly EnergyIntegrator _evDeliveredToday = new();
    private readonly EnergyIntegrator _exportedToday = new();

    private DateOnly _day;
    private DateTimeOffset? _firstSampleToday;
    private string? _lastPlanSignature;
    private DateTimeOffset _lastPlanLoggedAt = DateTimeOffset.MinValue;
    private bool _lastPlanUsable;
    private DayOutlook _lastOutlook = DayOutlook.Unknown;
    private bool _summaryLogged;

    public DayPlanProvider(
        ISolarForecastService forecast,
        IOptions<ForecastChargeOptions> options,
        IOptions<ChargeControlOptions> chargeControlOptions,
        ChargePowerConverter power,
        ILogger<DayPlanProvider> logger,
        IForecastRuntimeSettings? runtime = null,
        TimeProvider? timeProvider = null)
    {
        _forecast = forecast;
        _options = options.Value;
        _runtime = runtime;
        _power = power;
        _minChargingCurrentAmps = chargeControlOptions.Value.MinChargingCurrentAmps;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        _houseLoad = new HouseLoadProfile(_options.BaselineHouseLoadWatts);
        _accuracy = new ForecastAccuracyTracker(
            _options.BiasMinPeriods,
            _options.BiasClampMin,
            _options.BiasClampMax,
            _options.TrustBandMin,
            _options.TrustBandMax,
            _options.TrustBreachPeriods);
    }

    /// <summary>Energy delivered to the car so far today, in watt-hours.</summary>
    public double EvDeliveredTodayWh => _evDeliveredToday.EnergyWattHours;

    /// <summary>The learned household-load profile the plan is built on.</summary>
    public IHouseLoadProfile HouseLoad => _houseLoad;

    /// <summary>The realised forecast bias currently being applied to the remaining forecast.</summary>
    public double BiasFactor => _accuracy.BiasFactor;

    /// <summary>
    /// Folds one telemetry reading into today's picture and returns the current plan. Called every
    /// poll, in every mode: the accuracy tracking and the day totals are worth having even when the
    /// forecast-driven mode isn't the one driving the charger.
    /// </summary>
    /// <param name="state">The latest telemetry reading.</param>
    /// <param name="loanedTodayWh">Energy lent out of the home battery today, from the coordinator.</param>
    public SolarDayPlan Update(EnergyState state, double loanedTodayWh)
    {
        var localNow = TimeZoneInfo.ConvertTime(state.Timestamp, _timeProvider.LocalTimeZone);
        RollDayIfNeeded(DateOnly.FromDateTime(localNow.DateTime));
        _firstSampleToday ??= state.Timestamp;

        _houseLoad.Add(state.Timestamp, state.OtherLoadsPowerWatts);
        _evDeliveredToday.Add(state.Timestamp, Math.Max(0, state.EvChargerPowerWatts));
        _exportedToday.Add(state.Timestamp, Math.Max(0, -state.GridPowerWatts));

        var todaysForecast = _forecast.GetForecastForToday();
        _accuracy.Add(state.Timestamp, state.SolarPowerWatts, todaysForecast);
        LogClosedPeriod();

        var deadline = new DateTimeOffset(localNow.Date + _options.FullByTime, localNow.Offset);
        var plan = BuildPlan(state, todaysForecast, deadline);

        LogPlan(plan);
        LogDaySummaryIfDue(state, todaysForecast, plan, loanedTodayWh);

        return plan;
    }

    private SolarDayPlan BuildPlan(EnergyState state, SolarForecast? todaysForecast, DateTimeOffset deadline)
    {
        if (todaysForecast is null || todaysForecast.Periods.Count == 0)
        {
            return SolarDayPlan.Unavailable(deadline, "no forecast fetched yet");
        }

        var age = state.Timestamp - todaysForecast.RetrievedAt;
        if (age > _options.StaleForecastAfter)
        {
            return SolarDayPlan.Unavailable(deadline, $"forecast is {age.TotalHours:F1}h old (stale after {_options.StaleForecastAfter.TotalHours:F0}h)");
        }

        // A forecast this far from reality is not a basis for lending the battery out or for releasing
        // the car early; the mode degrades to the live-solar behaviour for the rest of the day.
        if (_accuracy.TrustBreached)
        {
            return SolarDayPlan.Unavailable(
                deadline, $"forecast accuracy {_accuracy.BiasFactor:F2} outside the trust band for {_options.TrustBreachPeriods} periods");
        }

        var plannerOptions = new SolarDayPlannerOptions(
            BatteryCapacityWh: _options.BatteryCapacityKWh * 1000,
            ChargeEfficiency: _options.ChargeEfficiency,
            MinChargePowerWatts: _power.AmpsToWatts(_minChargingCurrentAmps),
            MaxLoanPowerWatts: _options.EnableBatteryLoan ? _options.MaxLoanPowerWatts : 0,
            EnableBatteryLoan: _options.EnableBatteryLoan,
            MinViableWindow: _options.MinViableWindow,
            // The floor and the day's EV target are the two things worth changing from Home Assistant
            // without a restart, so they come from the runtime settings when those are wired up.
            MinBatterySocFloorPercent: _runtime?.MinBatterySocFloorPercent ?? _options.MinBatterySocFloorPercent,
            DailyEvTargetWh: _runtime?.DailyEvTargetWh ?? _options.DailyEvTargetKWh * 1000,
            Confidence: _options.ForecastConfidence);

        return SolarDayPlanner.Plan(
            state,
            todaysForecast,
            deadline,
            _houseLoad,
            _accuracy.BiasFactor,
            _evDeliveredToday.EnergyWattHours,
            plannerOptions,
            _lastOutlook);
    }

    private void RollDayIfNeeded(DateOnly today)
    {
        if (_day == today)
        {
            return;
        }

        _day = today;
        _firstSampleToday = null;
        _evDeliveredToday.Reset();
        _exportedToday.Reset();
        _accuracy.Reset();
        _lastPlanSignature = null;
        _lastPlanLoggedAt = DateTimeOffset.MinValue;
        _lastOutlook = DayOutlook.Unknown;
        _summaryLogged = false;
    }

    // One line per closed forecast period: the record that later explains why the car was paused at
    // 14:00 three weeks ago. A sustained breach of the trust band is a warning, not information --
    // it means the plan has stopped being believed.
    private void LogClosedPeriod()
    {
        if (_accuracy.TakeClosedPeriod() is not { } comparison)
        {
            return;
        }

        var level = _accuracy.TrustBreached ? LogLevel.Warning : LogLevel.Information;
        _logger.Log(
            level,
            "Forecast check: Period={PeriodStart:HH:mm}-{PeriodEnd:HH:mm} Forecast={ForecastWh:F0}Wh Actual={ActualWh:F0}Wh "
            + "Delta={DeltaWh:F0}Wh ({DeltaPercent}) CumForecast={CumForecastKWh:F1}kWh CumActual={CumActualKWh:F1}kWh Bias={Bias:F2}{TrustNote}",
            comparison.PeriodStart.LocalDateTime,
            comparison.PeriodEnd.LocalDateTime,
            comparison.ForecastWattHours,
            comparison.ActualWattHours,
            comparison.DeltaWattHours,
            comparison.DeltaFraction is { } fraction ? $"{fraction:P0}" : "n/a",
            comparison.CumulativeForecastWattHours / 1000,
            comparison.CumulativeActualWattHours / 1000,
            comparison.BiasFactor,
            _accuracy.TrustBreached ? " — outside the trust band; day plan abandoned" : string.Empty);
    }

    // The plan is recomputed every poll (every few seconds), so logging it unconditionally at
    // Information would bury everything else. It goes out at Information only when something a person
    // would care about actually changed; the unchanged case stays at Debug.
    //
    // Validation showed the first attempt at this failing badly -- 13,475 Information lines in a day,
    // one per poll, because the signature carried the budget to 0.1 kWh and that drifts continuously.
    // The signature is now deliberately coarse (whole kWh, whole percent, the window to the minute),
    // and a minimum interval backstops it in case some other field turns out to be just as restless.
    private void LogPlan(SolarDayPlan plan)
    {
        var signature = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{plan.Outlook}|{plan.FeasibleEvEnergyWh / 1000:F0}|{plan.RequiredSocFloorPercent:F0}|{plan.NextFeasibleWindow?.Start:HH:mm}-{plan.NextFeasibleWindow?.End:HH:mm}|{plan.IsUsable}");

        // A material change -- the outlook moving, or the plan becoming usable/unusable -- always logs.
        // The interval only damps the incremental drift, so the first real plan after startup, or after
        // a forecast finally arrives, is never held back.
        var material = plan.Outlook != _lastOutlook || plan.IsUsable != _lastPlanUsable;
        _lastPlanUsable = plan.IsUsable;

        var changed = signature != _lastPlanSignature
            && (material || _timeProvider.GetUtcNow() - _lastPlanLoggedAt >= MinimumPlanLogInterval);

        if (changed)
        {
            _lastPlanLoggedAt = _timeProvider.GetUtcNow();
        }

        _lastPlanSignature = signature;

        if (plan.Outlook != _lastOutlook)
        {
            _lastOutlook = plan.Outlook;
            _logger.LogInformation(
                "Day outlook: {Outlook} — Forecast={RemainingKWh:F1}kWh House={HouseKWh:F1}kWh BattToFull={BatteryKWh:F1}kWh "
                + "EvTarget={EvTargetKWh:F1}kWh Short={ShortfallKWh:F1}kWh EvExpectedToday={EvExpectedKWh:F1}kWh. {Reason}",
                plan.Outlook,
                plan.RemainingPvWh / 1000,
                plan.ExpectedHouseWh / 1000,
                plan.BatteryToFullWh / 1000,
                plan.EvTargetWh / 1000,
                plan.ShortfallWh / 1000,
                plan.EvExpectedTodayWh / 1000,
                plan.Reason);
        }

        _logger.Log(
            changed ? LogLevel.Information : LogLevel.Debug,
            "Day plan: Shoulder={ShoulderKWh:F1}kWh Plateau={PlateauKWh:F1}kWh House={HouseKWh:F1}kWh BattToFull={BatteryKWh:F1}kWh "
            + "Claimed={ClaimedKWh:F1}kWh EvBudget={BudgetKWh:F1}kWh Feasible={FeasibleKWh:F1}kWh Window={Window} "
            + "SocFloor={SocFloor:F0}% Bias={Bias:F2} Baseline={BaselineWatts:F0}W ({Confidence})",
            plan.ShoulderEnergyWh / 1000,
            plan.PlateauEnergyWh / 1000,
            plan.ExpectedHouseWh / 1000,
            plan.BatteryToFullWh / 1000,
            plan.PlateauClaimedByBatteryWh / 1000,
            plan.EvBudgetWh / 1000,
            plan.FeasibleEvEnergyWh / 1000,
            plan.NextFeasibleWindow is { } window
                ? $"{window.Start.LocalDateTime:HH:mm}-{window.End.LocalDateTime:HH:mm}"
                : "none",
            plan.RequiredSocFloorPercent,
            plan.BiasFactor,
            _houseLoad.DailyMeanWatts,
            _options.ForecastConfidence);
    }

    // Once a day, at the deadline: did the strategy actually work? Forecast against reality, whether
    // the battery made 100%, and what the car and the loan got out of the day.
    private void LogDaySummaryIfDue(EnergyState state, SolarForecast? todaysForecast, SolarDayPlan plan, double loanedTodayWh)
    {
        if (_summaryLogged || state.Timestamp < plan.Deadline)
        {
            return;
        }

        _summaryLogged = true;

        // Starting the service in the evening must not print a summary of a day it never watched: the
        // totals would all be zero and the line would be a lie. Wait for tomorrow.
        if (_firstSampleToday >= plan.Deadline)
        {
            return;
        }

        // Both figures come from the accuracy tracker, which integrates them live, period by period.
        // The cached forecast cannot be used here: Solcast returns only *future* periods, so by the
        // deadline "today's forecast" holds just the evening -- which is how the first version of this
        // line came to report "ForecastToday=6.6kWh ActualToday=52.7kWh (702%)" on a day the forecast
        // had in fact been accurate to within 3%.
        var forecastTodayWh = _accuracy.ForecastEnergyWhToday;
        var actualTodayWh = _accuracy.ActualEnergyWhToday;
        var deltaFraction = forecastTodayWh > 1 ? (actualTodayWh - forecastTodayWh) / forecastTodayWh : (double?)null;

        _logger.LogInformation(
            "Day summary: ForecastToday={ForecastKWh:F1}kWh ActualToday={ActualKWh:F1}kWh ({DeltaPercent}) "
            + "BatterySoc@{Deadline:HH:mm}={Soc:F0}% EvDelivered={EvKWh:F1}kWh Loaned={LoanedKWh:F1}kWh Exported={ExportedKWh:F1}kWh",
            forecastTodayWh / 1000,
            actualTodayWh / 1000,
            deltaFraction is { } fraction ? $"{fraction:P0}" : "n/a",
            plan.Deadline.LocalDateTime,
            state.BatterySocPercent,
            _evDeliveredToday.EnergyWattHours / 1000,
            loanedTodayWh / 1000,
            _exportedToday.EnergyWattHours / 1000);

        // The learned shape of the house, hour by hour. This is what the plan subtracts from the
        // forecast before the car sees any of it, so when a day's decisions look wrong this is the
        // first line to read.
        _logger.LogInformation(
            "House profile ({Learned}, mean {MeanWatts:F0}W): {Hourly}",
            _houseLoad.IsFullyLearned ? "learned" : "still learning; unobserved hours use the configured seed",
            _houseLoad.DailyMeanWatts,
            string.Join(" ", _houseLoad.HourlyWatts.Select((w, h) => $"{h:00}:{w:F0}W")));
    }
}
