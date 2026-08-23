using Gleanvolt.Api.Contracts;
using Gleanvolt.Core.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Gleanvolt.Api.Endpoints;

/// <summary>What the sun is expected to do, and what the car says about itself.</summary>
internal static class ForecastEndpoints
{
    internal static void MapForecast(this IEndpointRouteBuilder api)
    {
        api.MapGet("/forecast", async (
            bool? weather,
            ISolarForecastService forecasts,
            IWeatherService weatherService,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var now = time.GetUtcNow();
            var zone = time.LocalTimeZone;
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

            var todayForecast = forecasts.GetDayForecast(today);
            var tomorrowForecast = forecasts.GetDayForecast(today.AddDays(1));

            var startLocal = today.ToDateTime(TimeOnly.MinValue);
            var endLocal = startLocal.AddDays(2);
            var window = forecasts.GetForecast(
                new DateTimeOffset(startLocal, zone.GetUtcOffset(startLocal)),
                new DateTimeOffset(endLocal, zone.GetUtcOffset(endLocal)));

            var remaining = forecasts.GetForecast(now, new DateTimeOffset(
                startLocal.AddDays(1), zone.GetUtcOffset(startLocal.AddDays(1))));

            // Opt-in, because unlike everything else here it is a live call to a third party with a
            // quota: the forecast is cached and free to ask for, the weather is neither. Off by default
            // so a client polling this endpoint cannot quietly spend the site's allowance.
            var observation = weather == true && weatherService.IsConfigured
                ? await weatherService.GetCurrentAsync(cancellationToken)
                : null;

            var periods = window?.Periods ?? [];

            return Results.Ok(new ForecastResponse(
                RetrievedAt: window?.RetrievedAt ?? todayForecast?.RetrievedAt,
                TodayExpectedWh: todayForecast?.ExpectedEnergyWattHours,
                TodayRemainingWh: remaining?.ExpectedEnergyWattHours,
                TomorrowExpectedWh: tomorrowForecast?.ExpectedEnergyWattHours,
                PeakPowerWatts: window?.PeakPowerWatts ?? 0,
                Periods: [.. periods.Select(ForecastPeriodResponse.From)],
                Weather: observation is null ? null : WeatherResponse.From(observation.Observation)));
        })
            .WithName("getForecast")
            .WithSummary("The solar forecast the controller is working from")
            .WithDescription(
                "Today and tomorrow, period by period, from the cached forecast the poll loop is "
                + "deciding on rather than a fresh fetch. Empty periods and null totals mean no "
                + "forecast is in hand — no provider key, the provider is down, or nothing has been "
                + "fetched yet — which the controller degrades around rather than failing. Pass "
                + "weather=true to also fetch current conditions; that one is a live third-party call "
                + "against a quota, which is why it is off by default.")
            .Produces<ForecastResponse>();

        api.MapGet("/vehicle", (
            IVehicleTelemetry telemetry,
            ApiHostInfo host,
            Core.Models.TargetedChargeRequestLimits limits,
            TimeProvider time) =>
        {
            var state = telemetry.GetCurrentState();

            return Results.Ok(state is null
                ? VehicleResponse.Unavailable()
                : VehicleResponse.From(state, time.GetUtcNow(), host.VehicleMaxAge, limits.CanTargetSoc));
        })
            .WithName("getVehicle")
            .WithSummary("What the car last said about itself")
            .WithDescription(
                "State of charge, range, plug and charge state — and how old the reading is, which is "
                + "part of the reading. A cloud-reported SOC arrives hours late as a matter of course, "
                + "so use 'ageSeconds' and 'stale' before drawing any conclusion from the number beside "
                + "them. Nothing about how the charger is driven depends on this feed: it shapes what "
                + "can be asked for, never how it is delivered. 'available' is false when no feed is "
                + "configured or nothing has arrived, which is a supported installation, not a fault.")
            .Produces<VehicleResponse>();
    }
}
