using Microsoft.Extensions.Options;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Forecasting;

namespace Gleanvolt.Hosting.Targeting;

/// <summary>
/// Owns everything about "how is the target going": it meters what the charger has actually delivered
/// since the request was activated, fetches the forecast over the window that request spans, rebuilds
/// the <see cref="TargetedChargePlan"/> every poll, and does the logging around it.
///
/// <para>The <see cref="DayPlanProvider"/> pattern, and for the same reason: the planner in
/// <c>Gleanvolt.Core</c> stays pure, and this is where its output meets the clock, the forecast cache
/// and the log. Singleton — it accumulates delivery against one request.</para>
///
/// <para>It also answers <see cref="ITargetedChargePreview"/>, because everything a preview needs is
/// already here — the last reading, the forecast cache, the house-load profile, the bias and the
/// options. A preview is the same planner call with nothing delivered and nothing logged, and it
/// leaves the running target's metering exactly where it found it.</para>
/// </summary>
public sealed class TargetedChargeProvider : ITargetedChargePreview
{
    /// <summary>Floor on how often the plan line may be logged at Information. See <see cref="LogPlan"/>.</summary>
    private static readonly TimeSpan MinimumPlanLogInterval = TimeSpan.FromMinutes(5);

    private readonly ITargetedChargeSelector _selector;
    private readonly ISolarForecastService _forecast;
    private readonly DayPlanProvider _dayPlan;
    private readonly ChargePowerConverter _power;
    private readonly ChargeControlOptions _chargeControl;
    private readonly ForecastChargeOptions _forecastOptions;
    private readonly TargetedChargeOptions _options;
    private readonly IForecastRuntimeSettings? _runtime;
    private readonly ILogger<TargetedChargeProvider> _logger;
    private readonly TimeProvider _timeProvider;

    // Delivery is metered from activation, not from the plug-in: energy the car took under some earlier
    // mode is not part of this promise.
    private readonly EnergyIntegrator _delivered = new();
    private DateTimeOffset? _activatedAt;

    // The last reading a plan was built from, kept so a preview can be anchored to the same instant the
    // running plan is. Volatile rather than locked: it is a whole immutable record replaced wholesale,
    // and a preview reading the poll before the current one is a plan one cycle old, not a wrong one.
    private volatile EnergyState? _lastState;

    private string? _lastSignature;
    private DateTimeOffset _lastLoggedAt = DateTimeOffset.MinValue;
    private TargetedChargeStrategy? _lastStrategy;

    public TargetedChargeProvider(
        ITargetedChargeSelector selector,
        ISolarForecastService forecast,
        DayPlanProvider dayPlan,
        ChargePowerConverter power,
        IOptions<ChargeControlOptions> chargeControlOptions,
        IOptions<ForecastChargeOptions> forecastOptions,
        IOptions<TargetedChargeOptions> options,
        ILogger<TargetedChargeProvider> logger,
        IForecastRuntimeSettings? runtime = null,
        TimeProvider? timeProvider = null)
    {
        _selector = selector;
        _forecast = forecast;
        _dayPlan = dayPlan;
        _power = power;
        _chargeControl = chargeControlOptions.Value;
        _forecastOptions = forecastOptions.Value;
        _options = options.Value;
        _runtime = runtime;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Energy the charger has delivered since the active request was activated, in watt-hours.</summary>
    public double DeliveredWh => _delivered.EnergyWattHours;

    /// <summary>
    /// Folds one telemetry reading into the target's progress and returns the current plan, or null
    /// when nothing has been requested.
    ///
    /// <para>Call it after <see cref="DayPlanProvider.Update"/> on the same reading: the house-load
    /// profile and the forecast bias this plan is built on are that provider's, kept in one place so
    /// both modes see the same picture of the day.</para>
    /// </summary>
    public TargetedChargePlan? Update(EnergyState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        // Before the early return, not after it: a preview is wanted precisely when no target is
        // running, and a "no poll has completed yet" on a service that has been up for hours would be
        // a lie told at the moment the owner is deciding whether to trust the plan at all.
        _lastState = state;

        var request = _selector.Request;

        // A new request starts a new count. Keyed on the activation instant rather than on reference
        // equality, so re-activating the same figures still resets — which is what pressing the button
        // again means.
        if (request?.ActivatedAt != _activatedAt)
        {
            _activatedAt = request?.ActivatedAt;
            _delivered.Reset();
            _lastSignature = null;
            _lastStrategy = null;
        }

        if (request is null)
        {
            return null;
        }

        _delivered.Add(state.Timestamp, Math.Max(0, state.EvChargerPowerWatts));

        var plan = BuildPlan(state, request, _delivered.EnergyWattHours);

        LogPlan(plan);
        return plan;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Nothing delivered, because a preview is a quote for a charge that has not started — the whole
    /// request is still ahead of it. And nothing logged: the owner may press Preview twenty times while
    /// they settle on a departure, and twenty plan lines would bury the one the charger is actually
    /// working to.
    /// </remarks>
    public TargetedChargePlan? Preview(TargetedChargeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _lastState is { } state ? BuildPlan(state, request, deliveredWh: 0) : null;
    }

    private TargetedChargePlan BuildPlan(EnergyState state, TargetedChargeRequest request, double deliveredWh)
    {
        return TargetedChargePlanner.Plan(
            state,
            request,
            deliveredWh,
            // The window the request actually spans, not "today": an overnight target reaches into
            // tomorrow's periods, and this is the call that returns them.
            //
            // Only while there is a window left, though. Past the departure the range runs backwards,
            // and ForPeriod rejects that -- which on 2026-08-21 threw straight out of this method and
            // took the whole poll cycle with it, so the controller never got to notice the departure
            // had passed and the charger sat at 16A until somebody stopped it by hand. The planner
            // handles an expired deadline perfectly well on its own; it just needs to be reached.
            ForecastForWindow(state.Timestamp, request.DepartBy),
            NextBatteryFullBy(state.Timestamp),
            _dayPlan.HouseLoad,
            _dayPlan.BiasFactor,
            PlannerOptions());
    }

    /// <summary>
    /// The forecast over what is left of the request's window, or null once nothing is left of it.
    /// Null is a state the planner already understands -- it is what "no forecast fetched yet" looks
    /// like -- and the plan it produces is the one that ends the mode.
    /// </summary>
    private SolarForecast? ForecastForWindow(DateTimeOffset now, DateTimeOffset departBy) =>
        departBy > now ? _forecast.GetForecast(now, departBy) : null;

    private TargetedChargePlannerOptions PlannerOptions() => new(
        BatteryCapacityWh: _forecastOptions.BatteryCapacityKWh * 1000,
        ChargeEfficiency: _forecastOptions.ChargeEfficiency,
        MinChargePowerWatts: _power.AmpsToWatts(_chargeControl.MinChargingCurrentAmps),
        MaxChargePowerWatts: _power.AmpsToWatts(_chargeControl.MaxChargingCurrentAmps),
        // The floor is the one battery figure the owner can move without a restart, so it comes from
        // the runtime settings when those are wired up — same as the day plan's.
        MinBatterySocFloorPercent: _runtime?.MinBatterySocFloorPercent ?? _forecastOptions.MinBatterySocFloorPercent,
        SafetyMargin: _options.SafetyMargin,
        ReleaseSlack: _options.JustInTime.ReleaseSlack,
        // The targeted mode's own band, not the forecast mode's -- see TargetedChargeOptions.PaceConfidence.
        // Nothing about the deadline promise depends on it: feasibility is the charger against the clock,
        // with no forecast in it. All the band decides is how much gets bought before the sun turns up.
        Confidence: _options.PaceConfidence);

    /// <summary>
    /// The next instant the home battery is required to be at 100% — today's deadline if it is still
    /// ahead, otherwise tomorrow's. An overnight target spans that boundary, and the pack's booking has
    /// to be made against the deadline it is actually working to.
    /// </summary>
    private DateTimeOffset NextBatteryFullBy(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, _timeProvider.LocalTimeZone);
        var today = new DateTimeOffset(local.Date + _forecastOptions.FullByTime, local.Offset);

        return today > now ? today : today.AddDays(1);
    }

    // Rebuilt every poll, so logging it unconditionally at Information would bury everything else --
    // the same trap DayPlanProvider fell into and the same fix: a deliberately coarse signature (whole
    // kWh, the grid start to the minute), a minimum interval behind it, and the strategy changing is
    // always material enough to log at once.
    private void LogPlan(TargetedChargePlan plan)
    {
        var signature = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{plan.Strategy}|{plan.RemainingEnergyWh / 1000:F0}|{plan.GridEnergyWh / 1000:F0}|{plan.GridStart:HH:mm}|{plan.IsUsable}|{plan.HoldUntil:HH:mm}");

        var material = plan.Strategy != _lastStrategy;
        var changed = signature != _lastSignature
            && (material || _timeProvider.GetUtcNow() - _lastLoggedAt >= MinimumPlanLogInterval);

        if (changed)
        {
            _lastLoggedAt = _timeProvider.GetUtcNow();
        }

        _lastSignature = signature;
        _lastStrategy = plan.Strategy;

        _logger.Log(
            changed ? LogLevel.Information : LogLevel.Debug,
            "Targeted plan: {Strategy} Need={NeedKWh:F1}kWh Delivered={DeliveredKWh:F1}kWh Solar={SolarKWh:F1}kWh "
            + "Grid={GridKWh:F1}kWh GridStart={GridStart} Ceiling={CeilingKWh:F1}kWh Short={ShortKWh:F1}kWh "
            + "By={DepartBy:ddd HH:mm} Floor={Floor:F0}% Hold={Hold}. {Reason}",
            plan.Strategy,
            plan.RemainingEnergyWh / 1000,
            plan.DeliveredEnergyWh / 1000,
            plan.SolarEnergyWh / 1000,
            plan.GridEnergyWh / 1000,
            plan.GridStart is { } start ? $"{start.LocalDateTime:HH:mm}" : "none",
            plan.CeilingEnergyWh / 1000,
            plan.ShortfallWh / 1000,
            plan.DepartBy.LocalDateTime,
            plan.SocFloorPercent,
            plan.HoldUntil is { } hold ? $"{plan.TailEnergyWh / 1000:F1}kWh->{hold.LocalDateTime:HH:mm}" : "none",
            plan.Reason);
    }
}
