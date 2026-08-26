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
            IFastChargeSelector fast,
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

            if (body.Target is not null && body.Mode != ChargeControlMode.Targeted)
            {
                return ApiResults.BadRequest(
                    $"A target is only meaningful for the targeted mode, not for '{body.Mode}'.");
            }

            if (body.Fast is not null && body.Mode != ChargeControlMode.FastNoBattery)
            {
                return ApiResults.BadRequest(
                    $"A fast charge limit is only meaningful for the fastNoBattery mode, not for '{body.Mode}'.");
            }

            if (body.Mode == ChargeControlMode.FastNoBattery)
            {
                // The same factory the web tab and the Home Assistant button go through: the SOC to
                // kilowatt-hours conversion and every refusal live there, so all three doors reject the
                // same things for the same reasons.
                var composed = FastChargeLimitFactory.Create(
                    basis: body.Fast?.Basis ?? FastChargeBasis.Full,
                    energyWh: body.Fast?.EnergyKWh * 1000,
                    targetSocPercent: body.Fast?.TargetSocPercent,
                    vehicleSocPercent: vehicle.GetCurrentState()?.SocPercent,
                    pack: limits.Pack,
                    now: time.GetUtcNow(),
                    departBy: body.Fast?.DepartBy,
                    // The targeted mode's horizon, and for the same reason it has one: neither can
                    // promise anything past what a request that does not survive a restart can carry.
                    maxHorizon: limits.MaxHorizon);

                if (!composed.Accepted)
                {
                    return ApiResults.BadRequest(composed.Error!);
                }

                // Set before the mode, like the targeted request and for the same reason: the controller
                // reads both in the same cycle. Cleared rather than left standing on Full, so a limit
                // from an earlier charge cannot quietly end this one.
                if (composed.Limit is { } limit)
                {
                    fast.Set(limit, source);
                }
                else
                {
                    fast.Clear(source);
                }

                var started = await actions.StartAsync(body.Mode, source, cancellationToken);
                if (!started.Succeeded)
                {
                    // A charger that refuses Fast leaves the mode where it was, and would leave this
                    // limit set with nothing driving it.
                    fast.Clear(source);
                }

                return Respond(started, target, holder, fast);
            }

            if (body.Mode != ChargeControlMode.Targeted)
            {
                return Respond(await actions.StartAsync(body.Mode, source, cancellationToken), target, holder, fast);
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

            return Respond(result, target, holder, fast);
        })
            .WithName("startCharging")
            .WithSummary("Start controlled charging in a mode")
            .WithDescription(
                "Writes the charger's Fast use-mode and then selects the mode, so this works on a "
                + "charger sitting in Green rather than requiring somebody to have set the wallbox by "
                + "hand.\n\n"
                + "For 'targeted', pass the same target the preview took — it is set as the active "
                + "request before the mode is selected, and dropped again if the charger refuses.\n\n"
                + "For 'fastNoBattery', 'fast' says how much to deliver before the mode stops itself: "
                + "an amount of energy, a state of charge, or 'full' to let the car decide (the "
                + "default, and what you get by omitting it). It is a stopping condition and nothing "
                + "more — the charger stays pinned at the installation's maximum either way.\n\n"
                + "Add 'departBy' and the charge is deferred instead: it starts as late as it can and "
                + "still finish in time, so a pack asked to go above 80% spends minutes there rather "
                + "than a whole night. It needs an amount to work back from, so a departure with "
                + "'full' is refused. Read the schedule back under fastCharge.schedule on the "
                + "status.\n\n"
                + "A refused hardware write is reported as 200 with succeeded=false and a message, not "
                + "as an HTTP error: the call was understood and the controller is in a well-defined "
                + "state (exactly the one it was in before). Read the flag, not the status code.\n\n"
                + "The returned status is from the last completed poll, so the powers in it will not "
                + "reflect this action until the next one lands.")
            .Produces<ControlActionResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapPost("/charging/start/targeted", async (
            TargetedChargeRequestBody body,
            HttpContext http,
            IChargeActions actions,
            ITargetedChargeSelector target,
            IFastChargeSelector fast,
            ITargetedChargePreview preview,
            IVehicleTelemetry vehicle,
            ChargeControlStatusHolder holder,
            TargetedChargeRequestLimits limits,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var source = http.Source();

            if (!TargetedRequests.TryCompose(body, vehicle, limits, time, out var request, out var error))
            {
                return error!;
            }

            // Quoted before it is committed to, for one reason: a limit that cannot be met must be a
            // refusal with a reason rather than a charge that quietly under-delivers. The plan is built
            // by the same planner the poll loop uses and writes to nothing.
            var quoted = preview.Preview(request);
            if (quoted is null)
            {
                return ApiResults.NotPolled();
            }

            if (Impossible(quoted, request.Constraints) is { } refusal)
            {
                return ApiResults.BadRequest(refusal);
            }

            target.Set(request, source);

            var result = await actions.StartAsync(ChargeControlMode.Targeted, source, cancellationToken);
            if (!result.Succeeded)
            {
                target.Clear(source);
            }

            return Respond(result, target, holder, fast, Moved(body.Editable?.PlanId, quoted));
        })
            .WithName("startTargetedCharging")
            .WithSummary("Start a targeted charge under the limits a quoted plan was edited into")
            .WithDescription(
                "Takes the same body as POST /plans/targeted/preview — quote a plan, edit its "
                + "'editable' limits, and send the whole thing here to charge under them.\n\n"
                + "What you edit are **limits, not a schedule**, and that is the feature rather than a "
                + "simplification of it. The plan is rebuilt on every poll from a refreshed forecast "
                + "and the measured delivery, which is what lets a sunnier afternoon than forecast "
                + "shrink the grid block before any of it is bought. Replaying a list of blocks would "
                + "give that up, and would go on buying grid for energy already in the car. So the "
                + "planner keeps planning — inside the window you gave it.\n\n"
                + "A limit that makes the request impossible is refused here with the reason, before "
                + "anything starts. A limit that merely makes it expensive or partial is accepted, and "
                + "whatever it puts out of reach is reported as shortfall: a constraint may reduce what "
                + "is delivered, it may never make the plan lie about what will be.\n\n"
                + "Send the quoted plan's 'planId' back and 'forecastMovedSinceQuote' says whether the "
                + "forecast has been refreshed since you were shown it. Advisory only — nothing is "
                + "stored server-side and a start never fails because of it.")
            .Produces<ControlActionResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        api.MapPost("/charging/stop", async (
            HttpContext http,
            IChargeActions actions,
            ITargetedChargeSelector target,
            IFastChargeSelector fast,
            ChargeControlStatusHolder holder,
            CancellationToken cancellationToken) =>
        {
            var source = http.Source();

            // Any standing target goes with it. A targeted request is metered from the moment it was
            // activated, so a "resumed" one would restart its own metering regardless -- keeping it
            // through a stop would leave a promise standing that nobody is keeping and that no longer
            // means what it said. Post it again to start again.
            target.Clear(source);

            // A fast charge's limit on exactly the same reasoning: metered from its own activation, so
            // there is no such thing as resuming one.
            fast.Clear(source);

            return Respond(await actions.StopAsync(source, cancellationToken), target, holder, fast);
        })
            .WithName("stopCharging")
            .WithSummary("Stop controlled charging")
            .WithDescription(
                "Writes Stop to the charger and returns the mode to off, which releases any hold a mode "
                + "had armed and closes the charging session. Always writes, even when the controller "
                + "was already off and never took control — the button says stop charging, so it stops "
                + "charging. Any standing targeted request or fast charge limit is cleared with it.")
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
        ChargeControlStatusHolder holder,
        IFastChargeSelector fast,
        bool? forecastMovedSinceQuote = null) =>
        Results.Ok(new ControlActionResponse(
            result.Succeeded,
            result.Message,
            target.Request is { } request ? TargetedRequestResponse.From(request) : null,
            holder.Current is { } status ? StatusResponse.From(status) : null,
            fast.Limit is { } limit ? FastChargeResponse.From(limit) : null,
            forecastMovedSinceQuote));

    /// <summary>
    /// Whether the forecast has been refreshed since the caller was shown its quote, or null when there
    /// is no way to tell — no <c>planId</c> came back, or neither plan had a forecast behind it.
    /// </summary>
    private static bool? Moved(string? planId, TargetedChargePlan quoted) =>
        PlanIdentity.ForecastAsOf(planId) is { } quotedAt && quoted.ForecastAsOf is { } now
            ? now != quotedAt
            : null;

    /// <summary>
    /// The reason a set of limits cannot be honoured at all, or null when they can.
    ///
    /// <para>Only the impossible is refused. A limit that makes the request <em>partial</em> is a
    /// legitimate thing to ask for — "buy at most 8 kWh and I'll take what that gets me" — and comes
    /// back as a shortfall on the plan rather than as a 400. What is refused is a window with nothing
    /// in it: limits that leave the charger no time at all to run, which would otherwise start a mode
    /// that sits idle until the departure and then reports it delivered nothing.</para>
    /// </summary>
    private static string? Impossible(TargetedChargePlan plan, TargetedChargeConstraints? constraints)
    {
        if (constraints is null || plan.IsComplete || plan.CeilingEnergyWh > 0)
        {
            return null;
        }

        return "Those limits leave no time for the charger to run before the departure. Widen the "
            + "window, remove a forbidden stretch, or move the departure.";
    }
}
