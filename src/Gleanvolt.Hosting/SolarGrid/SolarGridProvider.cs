using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Forecasting;

namespace Gleanvolt.Hosting.SolarGrid;

/// <summary>
/// Where <see cref="SolarGridOutlookBuilder"/> meets the clock, the forecast cache, the runtime minimum
/// and the log. Called by the poll loop only while the solar-grid mode is selected.
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
        TimeProvider? timeProvider = null)
    {
        _forecast = forecast;
        _dayPlan = dayPlan;
        _settings = settings;
        _options = options.Value;
        _staleForecastAfter = forecastOptions.Value.StaleForecastAfter;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>What the rest of today's forecast says about sun clearing the owner's minimum.</summary>
    public SolarGridOutlook Update(EnergyState state)
    {
        var zone = _timeProvider.LocalTimeZone;
        var midnight = TimeZoneInfo.ConvertTime(state.Timestamp, zone).Date.AddDays(1);
        var endOfDay = new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));

        var outlook = SolarGridOutlookBuilder.Build(
            _forecast.GetForecastForToday(),
            state.Timestamp,
            endOfDay,
            _dayPlan.HouseLoad,
            _settings.MinSurplusWatts,
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
            "Solar-grid outlook: {Reason} ({Confidence}).", outlook.Reason, _options.ForecastConfidence);
    }
}
