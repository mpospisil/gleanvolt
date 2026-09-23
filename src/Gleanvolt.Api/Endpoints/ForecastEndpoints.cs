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
            var tomorrow = today.AddDays(1);

            // Whole local days from the retained history, not a window of the live cache (issue #221).
            // A provider answers only what is still to come, so the cache at four in the afternoon
            // cannot say what the morning was forecast to bring -- and the day totals below always
            // could. Reading both from the same place is what stops this response contradicting itself.
            var todayForecast = forecasts.GetDayForecast(today);
            var tomorrowForecast = forecasts.GetDayForecast(tomorrow);

            var startLocal = today.ToDateTime(TimeOnly.MinValue);

            var remaining = forecasts.GetForecast(now, new DateTimeOffset(
                startLocal.AddDays(1), zone.GetUtcOffset(startLocal.AddDays(1))));

            // Opt-in, because unlike everything else here it is a live call to a third party with a
            // quota: the forecast is cached and free to ask for, the weather is neither. Off by default
            // so a client polling this endpoint cannot quietly spend the site's allowance.
            var observation = weather == true && weatherService.IsConfigured
                ? await weatherService.GetCurrentAsync(cancellationToken)
                : null;

            // Ordered by period end, which is the provider's own identity for a period and the order
            // each day already comes in -- today's periods all end before tomorrow's first.
            var periods = (todayForecast?.Periods ?? []).Concat(tomorrowForecast?.Periods ?? []).ToList();

            ForecastDayResponse? Day(DateOnly date, Core.Models.SolarForecast? held, int offset)
            {
                // Both or neither: a day with periods but no summary would be a source in an
                // inconsistent state, and inventing the figures here is exactly what this endpoint is
                // being fixed for not doing.
                if (held is null || forecasts.GetDaySummary(date) is not { } summary)
                {
                    return null;
                }

                return ForecastDayResponse.From(date, summary, held, LocalMidnight(offset));
            }

            DateTimeOffset LocalMidnight(int offset)
            {
                // The zone's offset at that midnight rather than a fixed 24 hours from this one, so a
                // clocks-change night puts the boundary where the clocks actually put it.
                var local = startLocal.AddDays(offset);
                return new DateTimeOffset(local, zone.GetUtcOffset(local));
            }

            return Results.Ok(new ForecastResponse(
                RetrievedAt: todayForecast?.RetrievedAt ?? tomorrowForecast?.RetrievedAt,
                // A day with nothing held is absent rather than zero, for the reason GetDayForecast
                // answers null: "we don't know" and "no sun" are different facts.
                Days: [.. new[] { Day(today, todayForecast, 0), Day(tomorrow, tomorrowForecast, 1) }.OfType<ForecastDayResponse>()],
                // The day totals the dashboard shows, taken when the forecast landed rather than summed
                // here -- one number for one question, whichever surface asks it.
                TodayExpectedWh: forecasts.GetDayEnergyWattHours(today),
                TodayRemainingWh: remaining?.ExpectedEnergyWattHours,
                TomorrowExpectedWh: forecasts.GetDayEnergyWattHours(tomorrow),
                // The peak of what is returned, which is what this field has always claimed to be. Off
                // the periods rather than off the summaries, so it cannot disagree with the list beside
                // it -- and it no longer falls towards zero as the afternoon wears on.
                PeakPowerWatts: periods.Count == 0 ? 0 : periods.Max(p => p.EstimatedPowerWatts),
                Periods: [.. periods.Select(ForecastPeriodResponse.From)],
                Weather: observation is null ? null : WeatherResponse.From(observation.Observation)));
        })
            .WithName("getForecast")
            .WithSummary("The solar forecast the controller is working from")
            .WithDescription(
                "Today and tomorrow in full, period by period, from the cached forecast the poll loop "
                + "is deciding on rather than a fresh fetch. Elapsed periods are included, so summing "
                + "a day's periods gives that day's total in 'days' — every figure here comes from the "
                + "same two days of retained forecast. Empty periods and null totals mean no forecast "
                + "is in hand — no provider key, the provider is down, or nothing has been fetched yet "
                + "— which the controller degrades around rather than failing.\n\n"
                + "'days' carries each day's median, p10, p90 and peak, worked out when the forecast "
                + "landed. Check 'complete' before treating a day's periods as the whole day: a "
                + "provider answers only what is still to come, so a controller started at 10:44 has "
                + "nothing for today before then, and 'heldFrom' says where that day's periods really "
                + "begin. 'todayRemainingWh' is the one figure that is not a whole day.\n\n"
                + "Pass weather=true to also fetch current conditions; that one is a live third-party "
                + "call against a quota, which is why it is off by default.")
            .Produces<ForecastResponse>();

        api.MapGet("/vehicle", (
            IVehicleTelemetry telemetry,
            ApiHostInfo host,
            Core.Models.TargetedChargeRequestLimits limits,
            Core.Models.EvInfo ev,
            TimeProvider time) =>
        {
            var state = telemetry.GetCurrentState();

            return Results.Ok(state is null
                ? VehicleResponse.Unavailable(ev)
                : VehicleResponse.From(state, time.GetUtcNow(), host.VehicleMaxAge, limits.CanTargetSoc, ev));
        })
            .WithName("getVehicle")
            .WithSummary("What the car last said about itself")
            .WithDescription(
                "State of charge, range, plug and charge state — and how old the reading is, which is "
                + "part of the reading. A cloud-reported SOC arrives hours late as a matter of course, "
                + "so use 'ageSeconds' and 'stale' before drawing any conclusion from the number beside "
                + "them. Nothing about how the charger is driven depends on this feed: it shapes what "
                + "can be asked for, never how it is delivered. 'available' is false when no feed is "
                + "configured or nothing has arrived, which is a supported installation, not a fault.\n\n"
                + "'vehicle' is a different kind of thing from the rest: what the car *is* rather than "
                + "what it last said — its pack, and the currents and phases it will accept. That is "
                + "configuration, so it changes only across a restart, and it is null when no car has "
                + "been described.")
            .Produces<VehicleResponse>();
    }
}
