using Gleanvolt.Api.Contracts;
using Gleanvolt.Core.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Gleanvolt.Api.Endpoints;

/// <summary>
/// The site's energy history: what the roof made, what crossed the meter each way, what the car took
/// and where the home battery sat — for every quarter hour of every day, charging or not.
/// </summary>
internal static class EnergyEndpoints
{
    internal static void MapEnergy(this IEndpointRouteBuilder api)
    {
        api.MapGet("/energy/intervals", async (
            DateTimeOffset? from,
            DateTimeOffset? to,
            IEnergyIntervalStore store,
            IOptions<ApiOptions> options,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            if (!ApiResults.TryResolveRange(
                from, to, TimeSpan.FromDays(1), options.Value.MaxQueryRange, time.GetUtcNow(),
                out var start, out var end, out var error))
            {
                return error!;
            }

            try
            {
                var intervals = await store.GetIntervalsAsync(start, end, cancellationToken);

                return Results.Ok(new EnergySeriesResponse(
                    start, end, intervals.Count, [.. intervals.Select(EnergyIntervalResponse.From)]));
            }
            catch (Exception)
            {
                return ApiResults.StoreUnavailable("energy history");
            }
        })
            .WithName("getEnergyIntervals")
            .WithSummary("The energy series at recording resolution")
            .WithDescription(
                "Every recorded window that starts inside [from, to), oldest first — a quarter hour "
                + "each by default. Defaults to the last 24 hours. Read each row's 'coverage' before "
                + "trusting it: a window the service was not running for all of is short by the same "
                + "fraction, and a restart is not the sun going out. The range is bounded; ask for a "
                + "long history in several calls.")
            .Produces<EnergySeriesResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        api.MapGet("/energy/days/{date}", async (
            DateOnly date,
            IEnergyIntervalStore store,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var zone = time.LocalTimeZone;

            // Built from the zone's offset at each end rather than from a fixed 24 hours, so the
            // clocks-change days are the 23 and 25 hours they really are.
            var startLocal = date.ToDateTime(TimeOnly.MinValue);
            var endLocal = startLocal.AddDays(1);
            var start = new DateTimeOffset(startLocal, zone.GetUtcOffset(startLocal));
            var end = new DateTimeOffset(endLocal, zone.GetUtcOffset(endLocal));

            try
            {
                var intervals = await store.GetIntervalsAsync(start, end, cancellationToken);

                return Results.Ok(EnergyDayResponse.Aggregate(date, zone.Id, start, end, intervals));
            }
            catch (Exception)
            {
                return ApiResults.StoreUnavailable("energy history");
            }
        })
            .WithName("getEnergyDay")
            .WithSummary("A whole local day, added up")
            .WithDescription(
                "The same columns as an interval, summed over one local day in the site's own zone — "
                + "so 'how was Tuesday?' is one call rather than ninety-six rows to add up. A day with "
                + "nothing recorded comes back with zeroes and an interval count of 0, which is the "
                + "answer, not an error.")
            .Produces<EnergyDayResponse>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }
}
