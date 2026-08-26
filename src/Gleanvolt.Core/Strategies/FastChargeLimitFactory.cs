using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Strategies;

/// <summary>
/// Turns what somebody asked a fast charge for — nothing, an amount of energy, or a state of charge —
/// into the <see cref="FastChargeLimit"/> the mode stops itself at, or into the reason it cannot be
/// one.
///
/// <para><b>One place, because there are three doors.</b> The web tab's button, the HTTP API and the
/// Home Assistant button all compose a limit, and the three must not be able to disagree about what is
/// valid or where the SOC → kWh conversion happens. The same arrangement, and the same reasoning, as
/// <see cref="TargetedChargeRequestFactory"/> beside it.</para>
///
/// <para>The conversion happens <b>once, here, at the moment the limit is set</b>, and nothing
/// downstream re-derives it from a later reading — the rule
/// <see cref="TargetedChargeRequest.TargetSocPercent"/> explains at length, and it applies here without
/// a word of change.</para>
/// </summary>
public static class FastChargeLimitFactory
{
    /// <summary>
    /// The composed limit, or the message to put in front of whoever asked.
    ///
    /// <para>Three states, not two: a limit, a rejection, or <b>neither</b> — which is
    /// <see cref="FastChargeBasis.Full"/> succeeding. "Charge until the car says stop" is the absence of
    /// a limit rather than a limit of its own, so <see cref="Accepted"/> with a null
    /// <see cref="Limit"/> is a perfectly good answer and every caller has to handle it.</para>
    /// </summary>
    public sealed record Result(FastChargeLimit? Limit, string? Error)
    {
        /// <summary>Whether the request was accepted. A null <see cref="Limit"/> then means Full.</summary>
        public bool Accepted => Error is null;

        internal static Result Unlimited { get; } = new(null, null);

        internal static Result Ok(FastChargeLimit limit) => new(limit, null);

        internal static Result Rejected(string error) => new(null, error);
    }

    /// <summary>
    /// Composes a limit from the basis the owner chose.
    ///
    /// <para>Rejections are stated in the terms they were asked in: a car already at or above the
    /// target, a SOC basis on an installation that cannot convert one, a missing figure. Each surface
    /// shows the message it is given rather than writing its own.</para>
    /// </summary>
    /// <param name="basis">What the owner is aiming at. <see cref="FastChargeBasis.Full"/> needs neither figure.</param>
    /// <param name="energyWh">The energy asked for, at the charger. Read under <see cref="FastChargeBasis.Energy"/>.</param>
    /// <param name="targetSocPercent">The state of charge asked for. Read under <see cref="FastChargeBasis.Soc"/>.</param>
    /// <param name="vehicleSocPercent">What the car last reported, or null when there is no reading.</param>
    /// <param name="pack">The car's capacity and charge efficiency, or an unconfigured pack.</param>
    /// <param name="now">The instant the limit is being set — what delivery is metered from.</param>
    /// <param name="departBy">
    /// When the car has to be ready, or null to charge from the moment the mode starts. A departure
    /// defers the charge so it finishes just in time (#122); it needs an amount to work back from, and
    /// it needs to be in the future.
    /// </param>
    /// <param name="maxHorizon">
    /// How far ahead a departure may be set. Beyond it a request that does not survive a restart cannot
    /// honestly promise anything. Ignored when no departure is given.
    /// </param>
    public static Result Create(
        FastChargeBasis basis,
        double? energyWh,
        double? targetSocPercent,
        double? vehicleSocPercent,
        VehiclePackLimits pack,
        DateTimeOffset now,
        DateTimeOffset? departBy = null,
        TimeSpan? maxHorizon = null)
    {
        ArgumentNullException.ThrowIfNull(pack);

        // Checked before the basis, so "when" is refused in its own terms rather than after the owner
        // has been told something about kilowatt-hours.
        if (departBy is { } departure)
        {
            if (departure <= now)
            {
                return Result.Rejected("The departure time is in the past.");
            }

            if (maxHorizon is { } horizon && departure - now > horizon)
            {
                return Result.Rejected(
                    $"A departure more than {horizon.TotalHours:F0} hours away is further than a charge "
                    + "that does not survive a restart can honestly promise.");
            }

            // The one combination that cannot be honoured: with no amount there is no duration to work
            // back from, so there is no such thing as the latest moment it could start. Refused rather
            // than quietly charging at once, which is what the owner would find at 07:00.
            if (basis == FastChargeBasis.Full)
            {
                return Result.Rejected(
                    "A departure needs an amount to work back from — say how much energy, or what state "
                    + "of charge, the car needs. Without one there is nothing to time.");
            }
        }

        switch (basis)
        {
            case FastChargeBasis.Full:
                // Deliberately not an error when a figure is also supplied. The surfaces keep both boxes
                // filled while the owner switches between them, and refusing the press would be refusing
                // the one basis that asks for nothing.
                return Result.Unlimited;

            case FastChargeBasis.Energy:
                if (energyWh is not { } wh || double.IsNaN(wh) || wh <= 0)
                {
                    return Result.Rejected("Enter how much energy the car needs.");
                }

                return Result.Ok(new FastChargeLimit(wh, now, DepartBy: departBy));

            case FastChargeBasis.Soc:
                if (targetSocPercent is not { } target || double.IsNaN(target))
                {
                    return Result.Rejected("Enter the state of charge to stop at.");
                }

                if (!pack.CanTargetSoc)
                {
                    return Result.Rejected(
                        "A target state of charge needs the car's usable capacity, which is not configured "
                        + "(Vehicle:BatteryCapacityKWh). Ask in kilowatt-hours instead.");
                }

                var requiredWh = VehicleTargetEnergy.RequiredWh(
                    vehicleSocPercent, target, pack.BatteryCapacityWh, pack.ChargeEfficiency);

                if (requiredWh is null)
                {
                    return Result.Rejected(
                        "The car has not reported a state of charge, so there is nothing to measure the gap "
                        + "from. Ask in kilowatt-hours instead.");
                }

                // Rejected in words rather than started and instantly completed: a mode that switches
                // itself off within one poll of being pressed looks like a fault, and the owner is owed
                // the actual reason.
                if (requiredWh.Value <= 0)
                {
                    return Result.Rejected(
                        $"The car is already at {vehicleSocPercent:F0}%, at or above the {target:F0}% asked for.");
                }

                return Result.Ok(new FastChargeLimit(requiredWh.Value, now, target, vehicleSocPercent, departBy));

            default:
                return Result.Rejected($"Unknown fast charge basis '{basis}'.");
        }
    }
}
