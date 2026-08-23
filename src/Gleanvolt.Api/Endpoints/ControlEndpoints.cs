using Gleanvolt.Api.Contracts;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Gleanvolt.Api.Endpoints;

/// <summary>
/// The three things this API can actually do. Each of them is a button the web UI already has: the
/// API is a fourth way to press them, and deliberately not a fifth thing they can do.
/// </summary>
internal static class ControlEndpoints
{
    internal static void MapControl(this IEndpointRouteBuilder api)
    {
        api.MapPost("/charging/start", async (
            StartChargingRequest body,
            HttpContext http,
            IChargeActions actions,
            ITargetedChargeSelector target,
            IVehicleTelemetry vehicle,
            ChargeControlStatusHolder holder,
            TargetedChargeRequestLimits limits,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var source = http.Source();

            if (body.Mode == ChargeControlMode.Off)
            {
                return ApiResults.BadRequest("'off' is not a mode to start. POST /charging/stop instead.");
            }

            if (body.Mode != ChargeControlMode.Targeted)
            {
                if (body.Target is not null)
                {
                    return ApiResults.BadRequest(
                        $"A target is only meaningful for the targeted mode, not for '{body.Mode}'.");
                }

                return Respond(await actions.StartAsync(body.Mode, source, cancellationToken), target, holder);
            }

            if (body.Target is null)
            {
                return ApiResults.BadRequest(
                    "The targeted mode needs a target: how much energy (or what state of charge) by "
                    + "when. Quote it first with POST /plans/targeted/preview.");
            }

            if (!TargetedRequests.TryCompose(body.Target, vehicle, limits, time, out var request, out var error))
            {
                return error!;
            }

            // The request first, then the mode: the controller reads both in the same cycle, and a mode
            // selected a poll before its request would report "no target set" for that poll.
            target.Set(request, source);

            var result = await actions.StartAsync(ChargeControlMode.Targeted, source, cancellationToken);
            if (!result.Succeeded)
            {
                // A charger that refuses Fast leaves the mode where it was, and would leave this request
                // set with nothing driving it -- a promise nobody is keeping.
                target.Clear(source);
            }

            return Respond(result, target, holder);
        })
            .WithName("startCharging")
            .WithSummary("Start controlled charging in a mode")
            .WithDescription(
                "Writes the charger's Fast use-mode and then selects the mode, so this works on a "
                + "charger sitting in Green rather than requiring somebody to have set the wallbox by "
                + "hand.\n\n"
                + "For 'targeted', pass the same target the preview took — it is set as the active "
                + "request before the mode is selected, and dropped again if the charger refuses.\n\n"
                + "A refused hardware write is reported as 200 with succeeded=false and a message, not "
                + "as an HTTP error: the call was understood and the controller is in a well-defined "
                + "state (exactly the one it was in before). Read the flag, not the status code.\n\n"
                + "The returned status is from the last completed poll, so the powers in it will not "
                + "reflect this action until the next one lands.")
            .Produces<ControlActionResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapPost("/charging/stop", async (
            HttpContext http,
            IChargeActions actions,
            ITargetedChargeSelector target,
            ChargeControlStatusHolder holder,
            CancellationToken cancellationToken) =>
        {
            var source = http.Source();

            // Any standing target goes with it. A targeted request is metered from the moment it was
            // activated, so a "resumed" one would restart its own metering regardless -- keeping it
            // through a stop would leave a promise standing that nobody is keeping and that no longer
            // means what it said. Post it again to start again.
            target.Clear(source);

            return Respond(await actions.StopAsync(source, cancellationToken), target, holder);
        })
            .WithName("stopCharging")
            .WithSummary("Stop controlled charging")
            .WithDescription(
                "Writes Stop to the charger and returns the mode to off, which releases any hold a mode "
                + "had armed and closes the charging session. Always writes, even when the controller "
                + "was already off and never took control — the button says stop charging, so it stops "
                + "charging. Any standing targeted request is cleared with it.")
            .Produces<ControlActionResponse>();

        api.MapPut("/battery-hold", (
            BatteryHoldRequest body,
            HttpContext http,
            IBatteryHoldSelector hold,
            ChargeControlStatusHolder holder) =>
        {
            // Configured off means the hold would never reach the inverter. Accepting the call would
            // record an intent that silently does nothing, which is the one outcome an operator cannot
            // tell apart from a hold that is working.
            if (holder.Current is { BatteryHoldEnabled: false })
            {
                return Results.Problem(
                    title: "Battery hold is disabled",
                    detail: "BatteryHold:Enabled is false, so nothing would be written to the inverter. "
                        + "The feature writes to the inverter's power-control block and ships off until "
                        + "the register addresses have been verified against your firmware.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            hold.Set(body.Hold, http.Source());

            return Results.Ok(new ControlActionResponse(
                true,
                null,
                null,
                holder.Current is { } status ? StatusResponse.From(status) : null));
        })
            .WithName("setBatteryHold")
            .WithSummary("Arm or release the battery discharge hold")
            .WithDescription(
                "Stops the home battery serving house load, so the car charges from PV and grid while "
                + "the pack still charges from surplus. Orthogonal to the charge mode — either can be on "
                + "without the other.\n\n"
                + "What comes back under batteryHold.active is what was last written to the inverter, "
                + "not a read-back: the command register cannot be read. Judge whether a hold is really "
                + "in force by batteryPowerWatts on the status, never by the flag.")
            .Produces<ControlActionResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// The uniform answer to an action: what it did, the target now in force, and the controller's
    /// state afterwards — so a caller never has to poll to find out what happened.
    /// </summary>
    private static IResult Respond(
        ChargeActionResult result,
        ITargetedChargeSelector target,
        ChargeControlStatusHolder holder) =>
        Results.Ok(new ControlActionResponse(
            result.Succeeded,
            result.Message,
            target.Request is { } request ? TargetedRequestResponse.From(request) : null,
            holder.Current is { } status ? StatusResponse.From(status) : null));
}
