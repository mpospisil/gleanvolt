using Microsoft.Extensions.Options;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Forecasting;

namespace Gleanvolt.Hosting.SolarGrid;

/// <summary>
/// Where <see cref="SolarGridOutlookBuilder"/> meets the clock, the forecast cache, the runtime minimum
/// and the log. Called by the poll loop while one of the three modes that wait for sun is selected:
/// SolarGrid against the owner's minimum, and since #280 Solar and Forecasted against what they can
/// charge on, so all three know how long a wait for sun lasts and when their day is over.
///
/// <para>Takes the house-load profile from <see cref="DayPlanProvider"/>, which learns it from every
/// reading in every mode, so the forecast surplus here is the same PV-minus-house the day plan and the
/// targeted planner see.</para>
/// </summary>
public sealed class SolarGridProvider
{
    private readonly ISolarForecastService _forecast;
    private readonly DayPlanProvider _dayPlan;
    private readonly ISolarGridSettings _settings;
    private readonly SolarGridChargeOptions _options;
    private readonly TimeSpan _staleForecastAfter;
    private readonly ForecastChargeOptions _forecastOptions;
    private readonly double? _chargerFloorWatts;
    private readonly ILogger<SolarGridProvider> _logger;
    private readonly TimeProvider _timeProvider;

    private string? _lastLogged;

    public SolarGridProvider(
        ISolarForecastService forecast,
        DayPlanProvider dayPlan,
        ISolarGridSettings settings,
        IOptions<SolarGridChargeOptions> options,
        IOptions<ForecastChargeOptions> forecastOptions,
        ILogger<SolarGridProvider> logger,
        TimeProvider? timeProvider = null,
        ChargingLimits? limits = null,
        ChargePowerConverter? power = null)
    {
        _forecastOptions = forecastOptions.Value;
        _chargerFloorWatts = limits is not null && power is not null ? power.AmpsToWatts(limits.MinAmps) : null;
        _forecast = forecast;
        _dayPlan = dayPlan;
        _settings = settings;
        _options = options.Value;
        _staleForecastAfter = forecastOptions.Value.StaleForecastAfter;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// The outlook for <paramref name="mode"/>, or null for a mode that does not wait for sun (or, for
    /// Solar and Forecasted, when the charger's floor is not known).
    /// </summary>
    public SolarGridOutlook? Update(EnergyState state, ChargeControlMode mode) => mode switch
    {
        ChargeControlMode.SolarGrid => Update(state),
        ChargeControlMode.Solar when _chargerFloorWatts is { } floor => Update(state, floor),
        ChargeControlMode.Forecasted when _chargerFloorWatts is { } floor => Update(state, ForecastedMinimum(floor)),
        _ => null,
    };

    /// <summary>What the rest of today's forecast says about sun clearing the owner's minimum.</summary>
    public SolarGridOutlook Update(EnergyState state) => Update(state, _settings.MinSurplusWatts);

    /// <summary>
    /// The least surplus the forecast-driven mode can charge on: the charger's floor, or with the battery
    /// loan on, the least the loan will bridge. The lower of the two, so a day the loan could still use is
    /// never written off.
    /// </summary>
    private double ForecastedMinimum(double floorWatts) => _forecastOptions.EnableBatteryLoan
        ? Math.Min(floorWatts, Math.Min(_forecastOptions.MinBridgeSurplusWatts, _forecastOptions.SpillBridgeSurplusWatts))
        : floorWatts;

    private SolarGridOutlook Update(EnergyState state, double minSurplusWatts)
    {
        var zone = _timeProvider.LocalTimeZone;
        var midnight = TimeZoneInfo.ConvertTime(state.Timestamp, zone).Date.AddDays(1);
        var endOfDay = new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));

        var outlook = SolarGridOutlookBuilder.Build(
            _forecast.GetForecastForToday(),
            state.Timestamp,
            endOfDay,
            _dayPlan.HouseLoad,
            minSurplusWatts,
            _options.ForecastConfidence,
            _staleForecastAfter);

        Log(outlook);
        return outlook;
    }

    // Once per change of verdict, not once per poll: the reason carries the minimum and the window to
    // the minute, which is exactly the resolution a person reading "why did it stop at 16:30?" needs.
    private void Log(SolarGridOutlook outlook)
    {
        if (outlook.Reason == _lastLogged)
        {
            return;
        }

        _lastLogged = outlook.Reason;
        _logger.LogInformation(
            "Sun outlook: {Reason} ({Confidence}).", outlook.Reason, _options.ForecastConfidence);
    }
}
