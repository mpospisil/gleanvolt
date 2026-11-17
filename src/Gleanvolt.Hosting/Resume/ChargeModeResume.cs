using Gleanvolt.Core.Enums;
using static System.FormattableString;

namespace Gleanvolt.Hosting.Resume;

/// <summary>Whether to take a saved mode up again, and in words why or why not.</summary>
/// <param name="Resume">True to start <see cref="SavedChargeMode.Mode"/> again.</param>
/// <param name="Reason">For the log, either way.</param>
public sealed record ChargeModeResumeDecision(bool Resume, string Reason);

/// <summary>
/// The rule for each mode on whether a restart takes it up again (#413). Pure, so each rule can be
/// tested on its own; <see cref="PollingService"/> does the restoring and the starting.
///
/// <list type="bullet">
/// <item>A mode whose own end passed while Gleanvolt was down is not resumed: a Targeted departure, a
/// scheduled fast charge's departure, a limit already delivered.</item>
/// <item>A mode that has an end of its own still ahead — Targeted, a scheduled fast charge, Forecasted
/// until full — is resumed however long the absence. That end is what the owner set it up for.</item>
/// <item>Every other mode is resumed only after an absence of no more than <c>resumeWithin</c>; after
/// longer, the owner has had the evening to notice, and a mode set yesterday is not one they chose
/// today.</item>
/// </list>
///
/// <para>The car being unplugged does not matter: a resumed mode with no car waits for one, as it does
/// when selected by hand, and a car plugged in later starts a new session anyway (#192).</para>
/// </summary>
public static class ChargeModeResume
{
    /// <summary>
    /// What to do with <paramref name="saved"/> at <paramref name="now"/>, or null when there is nothing
    /// to say: nothing was running.
    /// </summary>
    public static ChargeModeResumeDecision? Decide(SavedChargeMode? saved, DateTimeOffset now, TimeSpan resumeWithin)
    {
        if (saved is null || saved.Mode == ChargeControlMode.Off)
        {
            return null;
        }

        var mode = saved.Mode;

        if (resumeWithin <= TimeSpan.Zero)
        {
            return Stay($"{mode} was running, but resuming after a restart is off (ChargeControl:ResumeWithin is 0)");
        }

        var absent = now - saved.SavedAt;
        var tooLong = absent > resumeWithin;
        var down = Invariant($"Gleanvolt was down for {Describe(absent)}");

        switch (mode)
        {
            case ChargeControlMode.Targeted:
                if (saved.TargetedRequest is not { } request)
                {
                    return Stay("Targeted was running with no target set");
                }

                if (request.DepartBy <= now)
                {
                    return Stay(Invariant($"Targeted's departure, {request.DepartBy.LocalDateTime:ddd HH:mm}, passed while Gleanvolt was down"));
                }

                if (saved.TargetedDeliveredWh >= request.RequiredEnergyWh)
                {
                    return Stay("Targeted had already delivered its target");
                }

                return Go(Invariant(
                    $"Targeted, {saved.TargetedDeliveredWh / 1000:F1} of {request.RequiredEnergyWh / 1000:F1}kWh delivered, by {request.DepartBy.LocalDateTime:ddd HH:mm}; {down}"));

            case ChargeControlMode.FastNoBattery:
                if (saved.FastLimit is { } limit)
                {
                    if (limit.DepartBy is { } departBy && departBy <= now)
                    {
                        return Stay(Invariant($"the scheduled fast charge's departure, {departBy.LocalDateTime:ddd HH:mm}, passed while Gleanvolt was down"));
                    }

                    if (limit.IsMet(saved.FastDeliveredWh))
                    {
                        return Stay("FastNoBattery had already delivered its limit");
                    }

                    // A scheduled charge is an appointment, like a target: kept however long the absence.
                    if (limit.IsDeferred)
                    {
                        return Go(Invariant(
                            $"FastNoBattery, scheduled for {limit.DepartBy!.Value.LocalDateTime:ddd HH:mm}, {saved.FastDeliveredWh / 1000:F1} of {limit.RequiredEnergyWh / 1000:F1}kWh delivered; {down}"));
                    }
                }

                break;

            case ChargeControlMode.Forecasted when saved.UntilFull:
                return Go($"Forecasted until full; {down}");
        }

        if (tooLong)
        {
            return Stay(Invariant($"{mode} was running, but {down}, longer than ChargeControl:ResumeWithin ({Describe(resumeWithin)})"));
        }

        var detail = saved.FastLimit is { } fast
            ? Invariant($", {saved.FastDeliveredWh / 1000:F1} of {fast.RequiredEnergyWh / 1000:F1}kWh delivered")
            : string.Empty;

        return Go($"{mode}{detail}; {down}");
    }

    private static ChargeModeResumeDecision Go(string reason) => new(true, reason);

    private static ChargeModeResumeDecision Stay(string reason) => new(false, reason);

    private static string Describe(TimeSpan span) =>
        span < TimeSpan.Zero ? "0 min"
        : span.TotalHours >= 1 ? Invariant($"{span.TotalHours:F1} h")
        : Invariant($"{Math.Ceiling(span.TotalMinutes):F0} min");
}
