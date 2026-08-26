using Gleanvolt.Api.Contracts;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Gleanvolt.Api.Endpoints;

/// <summary>
/// Quoting a targeted charge without starting one — the endpoint that makes this API worth having for
/// something that reasons rather than something that displays.
/// </summary>
internal static class PlanEndpoints
{
    internal static void MapPlans(this IEndpointRouteBuilder api)
    {
        api.MapPost("/plans/targeted/preview", (
            TargetedChargeRequestBody body,
            ITargetedChargePreview preview,
            IVehicleTelemetry vehicle,
            TargetedChargeRequestLimits limits,
            TimeProvider time) =>
        {
            if (!TargetedRequests.TryCompose(body, vehicle, limits, time, out var request, out var error))
            {
                return error!;
            }

            // Null when no poll has landed since startup: there is no battery SOC, no house load and no
            // instant to anchor to, and a plan built on zeroes is worse than no plan.
            var plan = preview.Preview(request);
            if (plan is null)
            {
                return ApiResults.NotPolled();
            }

            // What the same energy by the same time would have cost without the hold. Only when a hold
            // was actually planned: with nothing above the rest point the two plans are the same plan.
            var cheapest = request.HoldsTail
                ? preview.Preview(request with { Priority = TargetedChargePriority.Cheapest, TailEnergyWh = 0 })
                : null;

            return Results.Ok(new TargetedPreviewResponse(
                TargetedRequestResponse.From(request),
                TargetedPlanResponse.From(plan),
                cheapest is null ? null : TargetedPlanResponse.From(cheapest),
                EditablePlanBody.From(plan, request.Constraints)));
        })
            .WithName("previewTargetedPlan")
            .WithSummary("Quote a targeted charge without starting it")
            .WithDescription(
                "What would happen if this request were started now: the strategy, the pace the charger "
                + "would have to hold, how much comes from the sun and how much from the grid, when the "
                + "import would run, and — when it cannot be met — how short it falls and the departure "
                + "that would have covered it.\n\n"
                + "Writes to nothing. It sets no request, selects no mode, touches no device and does "
                + "not disturb a plan already running, so it is safe to call repeatedly: 'what does 80% "
                + "by seven cost, and would leaving at eight make it free?' is three calls and no "
                + "hardware writes.\n\n"
                + "Under justInTime the response also carries the same request priced as cheaply as "
                + "possible, so the cost of holding the last stretch back can be read off the difference "
                + "in gridEnergyWh.")
            .Produces<TargetedPreviewResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }
}

/// <summary>
/// Turning the request body into a <see cref="TargetedChargeRequest"/>, in one place, because the
/// preview and the start must never disagree about what is valid — the quote has to be the promise.
/// </summary>
internal static class TargetedRequests
{
    internal static bool TryCompose(
        TargetedChargeRequestBody body,
        IVehicleTelemetry vehicle,
        TargetedChargeRequestLimits limits,
        TimeProvider time,
        out TargetedChargeRequest request,
        out IResult? error)
    {
        // The same factory the web UI's form goes through: the SOC to kilowatt-hours conversion, the
        // just-in-time split, the horizon and the "car is already there" refusal all live there.
        var composed = TargetedChargeRequestFactory.Create(
            departBy: body.DepartBy,
            energyWh: body.EnergyKWh * 1000,
            targetSocPercent: body.TargetSocPercent,
            priority: body.Priority,
            restSocPercent: body.RestSocPercent,
            vehicleSocPercent: vehicle.GetCurrentState()?.SocPercent,
            limits: limits,
            now: time.GetUtcNow());

        if (composed.Request is not { } composedRequest)
        {
            request = default!;
            error = ApiResults.BadRequest(composed.Error!);

            return false;
        }

        // The limits ride with the request through the same door, so a plan quoted under them and a
        // charge started under them cannot be built two different ways (#128).
        request = composedRequest with { Constraints = body.Editable?.ToConstraints() };
        error = null;

        return true;
    }
}
