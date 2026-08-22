using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Strategies;

/// <summary>
/// Turns what somebody asked for — an amount of energy or a state of charge, by a time, optimised one
/// of two ways — into the <see cref="TargetedChargeRequest"/> the controller works to, or into the
/// reason it cannot be one.
///
/// <para><b>One place, because there are now several doors.</b> The web UI's form and the HTTP API
/// both compose requests, and the two must not be able to disagree about what is valid, where the
/// SOC → kWh conversion happens, or how the just-in-time tail is split off. Everything here was the
/// targeted tab's own <c>TryCompose</c> before the API existed; it is pure, so it belongs in
/// <c>Gleanvolt.Core</c> with the planner it feeds.</para>
///
/// <para>Both conversions happen <b>once, here, at the moment the request is made</b>, and nothing
/// downstream re-derives them from a later reading — the rule
/// <see cref="TargetedChargeRequest.TargetSocPercent"/> explains at length: a parked car's cloud SOC
/// arrives when it feels like it, and a promise already half delivered must not move at 02:00 because
/// the car finally phoned home.</para>
/// </summary>
public static class TargetedChargeRequestFactory
{
    /// <summary>
    /// The composed request, or the message to put in front of whoever asked. Exactly one of the two
    /// is non-null.
    /// </summary>
    public sealed record Result(TargetedChargeRequest? Request, string? Error)
    {
        internal static Result Ok(TargetedChargeRequest request) => new(request, null);

        internal static Result Rejected(string error) => new(null, error);
    }

    /// <summary>
    /// Composes a request from an energy figure or a target state of charge — supply exactly one.
    ///
    /// <para>Rejections are the interesting part and are all stated in the terms they were asked in:
    /// a car already at or above the target, a departure in the past, a departure past the horizon,
    /// or a SOC target on an installation that cannot convert one.</para>
    /// </summary>
    /// <param name="departBy">When the energy has to be in the car.</param>
    /// <param name="energyWh">The energy asked for, at the charger. Null when asking in state of charge.</param>
    /// <param name="targetSocPercent">The state of charge asked for. Null when asking in energy.</param>
    /// <param name="priority">What to optimise while delivering it.</param>
    /// <param name="restSocPercent">
    /// Where a just-in-time hold parks the car, or null for <see cref="TargetedChargeRequestLimits.DefaultRestSocPercent"/>.
    /// Ignored under <see cref="TargetedChargePriority.Cheapest"/>.
    /// </param>
    /// <param name="vehicleSocPercent">What the car last reported, or null when there is no reading.</param>
    /// <param name="limits">The installation's horizon and pack figures.</param>
    /// <param name="now">The instant the request is being made — what delivery is metered from.</param>
    public static Result Create(
        DateTimeOffset departBy,
        double? energyWh,
        double? targetSocPercent,
        TargetedChargePriority priority,
        double? restSocPercent,
        double? vehicleSocPercent,
        TargetedChargeRequestLimits limits,
        DateTimeOffset now)
    {
        double askedWh;
        double? soc = null;
        double? socNow = null;

        if (targetSocPercent is { } target)
        {
            if (energyWh is not null)
            {
                return Result.Rejected("Ask for energy or for a state of charge, not both.");
            }

            if (!limits.CanTargetSoc)
            {
                return Result.Rejected(
                    "A target state of charge needs the car's usable capacity, which is not configured "
                    + "(Vehicle:BatteryCapacityKWh). Ask in kilowatt-hours instead.");
            }

            socNow = vehicleSocPercent;
            var requiredWh = VehicleTargetEnergy.RequiredWh(
                socNow, target, limits.BatteryCapacityWh, limits.ChargeEfficiency);

            if (requiredWh is null)
            {
                return Result.Rejected(
                    "The car has not reported a state of charge, so there is nothing to measure the gap "
                    + "from. Ask in kilowatt-hours instead.");
            }

            if (requiredWh.Value <= 0)
            {
                return Result.Rejected(
                    $"The car is already at {socNow:F0}%, at or above the {target:F0}% asked for.");
            }

            askedWh = requiredWh.Value;
            soc = target;
        }
        else
        {
            if (energyWh is not { } wh || wh <= 0)
            {
                return Result.Rejected("Enter how much energy the car needs.");
            }

            askedWh = wh;
        }

        if (departBy <= now)
        {
            return Result.Rejected("The departure time is in the past.");
        }

        if (departBy - now > limits.MaxHorizon)
        {
            return Result.Rejected(
                $"A departure more than {limits.MaxHorizon.TotalHours:F0} hours away is further than "
                + "the forecast — or a request that does not survive a restart — can honestly promise.");
        }

        var request = new TargetedChargeRequest(askedWh, departBy, now, soc, socNow) with { Priority = priority };

        // The split, made once and here — the same place and the same moment as the SOC → kWh
        // conversion above, so a reading that lands later cannot move a promise already part delivered.
        // The planner is handed watt-hours and never sees a percentage again.
        if (priority == TargetedChargePriority.JustInTime && limits.CanTargetSoc)
        {
            var rest = restSocPercent ?? limits.DefaultRestSocPercent;

            var endSoc = soc ?? VehicleTargetEnergy.ResultingSocPercent(
                vehicleSocPercent, askedWh, limits.BatteryCapacityWh, limits.ChargeEfficiency);

            var tailWh = endSoc is { } end
                ? VehicleTargetEnergy.TailAboveRestWh(
                    vehicleSocPercent, end, rest, limits.BatteryCapacityWh, limits.ChargeEfficiency)
                : null;

            request = request with
            {
                // Never more than the request itself: a rest point below where the car already sits
                // would otherwise hold back energy the request never contained.
                TailEnergyWh = Math.Clamp(tailWh ?? 0, 0, askedWh),
                RestSocPercent = rest,
            };
        }

        return Result.Ok(request);
    }
}
