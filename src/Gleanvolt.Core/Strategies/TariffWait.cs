using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Strategies;

/// <summary>
/// The one rule for a mode set to <b>low tariff only</b> (issue #274), shared by Fast and SolarGrid:
/// in high tariff the car waits, and the decision says until when.
///
/// <para><b>A wait, not a finish.</b> It is decided as a pause or a stand-down, never as a charge, so
/// the coordinator's own state reads "not charging" through it. That is what keeps it from being read
/// as the car having reached its limit: both modes only judge a finished car while they are asking it
/// to charge, and on the low-tariff edge the coordinator gives the car a fresh idle dwell to spin up
/// in.</para>
///
/// <para><b>Stood down for a long wait</b>, paused for a short one, through <see cref="ChargerWait"/>
/// like every other wait: a high tariff can last most of a day, and this wallbox tolerates 0 A in Fast
/// for minutes, not hours (#135).</para>
/// </summary>
public static class TariffWait
{
    /// <summary>
    /// The wait, or null when <paramref name="input"/> carries no restriction or the tariff is low.
    /// </summary>
    /// <param name="input">This cycle's input; its <see cref="ChargingControlInput.LowTariffOnly"/> decides.</param>
    /// <param name="mode">The mode's name as the owner knows it, for the reason: "Fast", "SolarGrid".</param>
    public static ChargingControlDecision? For(ChargingControlInput input, string mode)
    {
        if (input.LowTariffOnly is not { IsHigh: true } tariff)
        {
            return null;
        }

        var until = tariff.NextChange is { } change ? $"until {change.LocalDateTime:HH:mm}" : "for as far as the tariff is known";

        return ChargerWait.For(input, tariff.NextChange, $"High tariff {until}; waiting for low tariff ({mode}, low tariff only).");
    }
}
