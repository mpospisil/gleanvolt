using Gleanvolt.Api.Contracts;
using Gleanvolt.Core.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Gleanvolt.Api.Endpoints;

/// <summary>Recorded charging sessions: what each one cost, and where its energy came from.</summary>
internal static class SessionEndpoints
{
    internal static void MapSessions(this IEndpointRouteBuilder api)
    {
        api.MapGet("/sessions", async (
            DateTimeOffset? from,
            DateTimeOffset? to,
            int? limit,
            IChargingSessionStore store,
            IOptions<ApiOptions> options,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var settings = options.Value;

            if (!ApiResults.TryResolveRange(
                from, to, TimeSpan.FromDays(30), settings.MaxQueryRange, time.GetUtcNow(),
                out var start, out var end, out var error))
            {
                return error!;
            }

            var take = Math.Clamp(limit ?? settings.MaxSessions, 1, settings.MaxSessions);

            try
            {
                var sessions = await store.GetSessionsAsync(start, end, cancellationToken);

                return Results.Ok(new SessionListResponse(
                    start,
                    end,
                    Math.Min(sessions.Count, take),
                    sessions.Count > take,
                    [.. sessions.Take(take).Select(SessionResponse.From)]));
            }
            catch (Exception)
            {
                return ApiResults.StoreUnavailable("charging-session history");
            }
        })
            .WithName("getSessions")
            .WithSummary("Charging sessions in a range")
            .WithDescription(
                "Sessions that started inside [from, to), newest first. Defaults to the last 30 days. "
                + "Each carries the energy split between sun, grid and the home battery — an "
                + "attribution made from the power flows at each poll, not four separate meters.")
            .Produces<SessionListResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        api.MapGet("/sessions/{id:guid}", async (
            Guid id,
            IChargingSessionStore store,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var document = await store.ExportAsync(id, cancellationToken);

                return document is null
                    ? ApiResults.NotFound($"No charging session with id {id}.")
                    : Results.Ok(SessionDetailResponse.From(document));
            }
            catch (Exception)
            {
                return ApiResults.StoreUnavailable("charging-session history");
            }
        })
            .WithName("getSession")
            .WithSummary("One session in full")
            .WithDescription(
                "The session, every poll recorded against it, and its notable moments. A session still "
                + "running is returned too, but only a closed one will never change again.")
            .Produces<SessionDetailResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }
}
