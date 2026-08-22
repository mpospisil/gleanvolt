using Gleanvolt.Api.Contracts;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Gleanvolt.Api.Endpoints;

/// <summary>What the controller is doing, and whether it is doing anything at all.</summary>
internal static class StatusEndpoints
{
    internal static void MapStatus(this IEndpointRouteBuilder api)
    {
        api.MapGet("/status", (ChargeControlStatusHolder holder) =>
            holder.Current is { } status
                ? Results.Ok(StatusResponse.From(status))
                : ApiResults.NotPolled())
            .WithName("getStatus")
            .WithSummary("The live state of the site")
            .WithDescription(
                "Every figure the control loop last read: what the roof is making, which way the meter "
                + "is running, where the home battery sits, what the car is drawing, and which mode is "
                + "driving the charger. Published once per poll (five seconds by default), so polling "
                + "this faster than that returns the same snapshot.")
            .Produces<StatusResponse>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        api.MapGet("/health", async (
            ChargeControlStatusHolder holder,
            ISolarForecastService forecast,
            IVehicleTelemetry vehicle,
            IWeatherService weather,
            IEnergyIntervalStore energy,
            IChargingSessionStore sessions,
            ApiHostInfo host,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var now = time.GetUtcNow();
            var status = holder.Current;
            var age = status is null ? (TimeSpan?)null : now - status.Timestamp;
            var reading = vehicle.GetCurrentState();
            var vehicleAge = reading?.AgeAt(now);

            // A probe rather than a flag: whether a store is *readable* is the only useful answer, and
            // it cannot be derived from configuration -- a store can be enabled and still have failed to
            // open its file, which is exactly the case this endpoint exists to surface.
            var energyOk = await Probe(() => energy.GetIntervalsAsync(now.AddMinutes(-1), now, cancellationToken));
            var sessionsOk = await Probe(() => sessions.GetSessionsAsync(now.AddMinutes(-1), now, cancellationToken));

            var today = forecast.GetForecastForToday();

            return Results.Ok(new HealthResponse(
                // Two poll intervals plus a margin. Not configurable on purpose: this is a liveness
                // bound, not a tuning knob, and the poll interval it is generous against is five
                // seconds by default.
                Ok: age is { } since && since < TimeSpan.FromMinutes(2),
                Version: host.Version,
                Now: now,
                TimeZoneId: time.LocalTimeZone.Id,
                LastPollAt: status?.Timestamp,
                LastPollAgeSeconds: age?.TotalSeconds is { } seconds ? Math.Max(0, seconds) : null,
                Mode: status?.Mode,
                DryRun: status?.DryRun,
                ForecastAvailable: today is not null,
                ForecastRetrievedAt: today?.RetrievedAt,
                WeatherConfigured: weather.IsConfigured,
                VehicleAvailable: reading is not null,
                VehicleAgeSeconds: vehicleAge?.TotalSeconds is { } vehicleSeconds ? Math.Max(0, vehicleSeconds) : null,
                VehicleStale: reading?.IsStaleAt(now, host.VehicleMaxAge) ?? false,
                EnergyHistoryAvailable: energyOk,
                SessionHistoryAvailable: sessionsOk));
        })
            .WithName("getHealth")
            .WithSummary("Whether the controller is alive and what it can see")
            .WithDescription(
                "Poll this to answer 'is it working?'. It reports the running build, how long ago the "
                + "last poll completed, whether a forecast and a vehicle reading are in hand, and "
                + "whether the two history databases can be read. It never fails for a component being "
                + "down -- that is what it is reporting.")
            .Produces<HealthResponse>();
    }

    /// <summary>
    /// Whether a store answered at all. Its own contract is that failures are best-effort and must
    /// never disturb anything else, so the exception is the answer here rather than an error.
    /// </summary>
    private static async Task<bool> Probe(Func<Task> query)
    {
        try
        {
            await query();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
