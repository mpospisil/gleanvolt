using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Strategies;

/// <summary>
/// The one rule for how a mode waits (#280). A controller decides only <em>"not now"</em>, and until
/// when if it knows: the next sun from the forecast, the next tariff edge, the end of a restart dwell.
/// This picks what the charger is told: a <see cref="ChargingControlAction.Pause"/>, which holds it in
/// Fast at the pause current, or a <see cref="ChargingControlAction.StandDown"/>, which puts it in Stop.
///
/// <para><b>Why it matters.</b> This wallbox tolerates Fast at 0 A for about seven minutes. Then it
/// ends the session itself, reports Finishing and switches its own use-mode to Stop (#135), and two
/// minutes later <see cref="ChargerOwnership"/> reads that as the owner taking the charger back. Each
/// mode used to choose for itself, and Solar, Forecasted and Targeted's wait for sun paused however
/// long the wait: Forecasted lost the charger this way every evening.</para>
///
/// <list type="bullet">
/// <item><description><b>Pause</b> when the next change is less than <see cref="StandDownAfter"/> away:
/// a passing cloud costs no use-mode writes.</description></item>
/// <item><description><b>Stand down</b> when it is further, or not known.</description></item>
/// <item><description><b>Stand down</b> a pause that has already lasted <see cref="MaxPause"/>, before
/// the wallbox's own timeout gets there first.</description></item>
/// <item><description>A charger already stood down <b>stays</b> stood down until a charge re-arms
/// it: going back to Fast only to sit at 0 A would spend two use-mode writes on nothing.</description></item>
/// </list>
///
/// <para>Not here: the waits a mode schedules itself, the fast mode's deferred start and the targeted
/// mode's held tail. They know their time, it is hours away, and their release is one-way
/// (<see cref="ChargingControlDecision.Appointment"/>). Nor the minimum run time, which comes before any
/// wait: a charge that has just started still finishes its minimum at the floor.</para>
/// </summary>
public static class ChargerWait
{
    /// <summary>
    /// How far off the next change has to be before a waiting car is stood down rather than paused.
    /// Short gaps (a cloud inside a sunny period, a period boundary a few minutes away) stay a pause;
    /// anything measured in half-hours belongs in Stop.
    /// </summary>
    public static readonly TimeSpan StandDownAfter = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The longest a pause is held before the charger is stood down anyway. Under the wallbox's own
    /// timeout of about seven minutes, with a poll or two of margin, so a "short" wait that keeps
    /// getting longer (a cloud that settles in, a forecast period that slips) ends in our Stop rather
    /// than the wallbox's.
    /// </summary>
    public static readonly TimeSpan MaxPause = TimeSpan.FromMinutes(5);

    /// <summary>The action for a wait that ends at <paramref name="until"/>, or at an unknown time when it is null.</summary>
    /// <param name="input">This cycle's input: the clock, and how long the charger has been paused.</param>
    /// <param name="until">When the wait is expected to end; null when nobody knows.</param>
    /// <param name="reason">Why the mode is waiting, in its own words.</param>
    public static ChargingControlDecision For(ChargingControlInput input, DateTimeOffset? until, string reason)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.ChargerStoodDown)
        {
            return StandDown(reason, until);
        }

        var now = input.State.Timestamp;
        if (until is not { } end)
        {
            return StandDown($"{reason} Not known for how long, so standing the charger down.", until);
        }

        if (end - now >= StandDownAfter)
        {
            return StandDown($"{reason} Standing the charger down until {end.LocalDateTime:HH:mm}.", until);
        }

        if (!input.Charging && input.TimeInCurrentState >= MaxPause)
        {
            return StandDown(
                $"{reason} Paused {input.TimeInCurrentState.TotalMinutes:F0} min already, so standing the charger down "
                + "before the wallbox gives up on the session.",
                until);
        }

        return new ChargingControlDecision(ChargingControlAction.Pause, null, reason, WaitUntil: until);
    }

    /// <summary>
    /// When a restart dwell ends: the moment this pause will have lasted <paramref name="minPauseTime"/>.
    /// </summary>
    public static DateTimeOffset DwellEnds(ChargingControlInput input, TimeSpan minPauseTime)
    {
        ArgumentNullException.ThrowIfNull(input);

        var left = minPauseTime - input.TimeInCurrentState;
        return input.State.Timestamp + (left > TimeSpan.Zero ? left : TimeSpan.Zero);
    }

    /// <summary>
    /// When the forecast has sun clearing the mode's minimum again: the outlook's next sun, which is
    /// now itself inside a sunny stretch. Null when there is no usable forecast to say.
    /// </summary>
    public static DateTimeOffset? NextSun(ChargingControlInput input) =>
        input.Outlook is { ForecastUsable: true, NextSunAt: { } next } ? next : null;

    /// <summary>
    /// The one rule for the end of the day (#280), for the three modes that wait for sun: once the
    /// forecast is usable and has nothing left today that clears the mode's minimum, a wait for sun is
    /// over, and the mode ends itself as <see cref="ChargingSessionEndReason.NoSunLeftToday"/> rather than
    /// sitting stood down all night. Null while there is sun to come, or no forecast to say there is
    /// none: then the mode stays on, stood down, until its owner switches it off.
    ///
    /// <para>Asked only on a wait for sun or for the battery, never on a restart dwell: a mode that is
    /// in the sun now ends on the next poll the sun is not enough.</para>
    ///
    /// <para><b>Until full</b> (#279) changes this one step and nothing else: the day being over is then
    /// a wait for tomorrow's sun, and the charger is stood down for it rather than the mode ended. The
    /// morning re-arm is the mode's own next Charge, once the new day's plan has a window; the time here
    /// is only the estimate the surfaces show.</para>
    /// </summary>
    /// <param name="input">This cycle's input; its <see cref="ChargingControlInput.Outlook"/> decides.</param>
    /// <param name="why">Why the mode is not charging now, in its own words.</param>
    public static ChargingControlDecision? DayIsOver(ChargingControlInput input, string why)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Outlook is not { NoSunLeftToday: true } outlook)
        {
            return null;
        }

        if (input.UntilFull)
        {
            var tomorrow = outlook.TomorrowFirstSunAt;
            return StandDown(
                $"{why} Nothing left of today will do better ({outlook.Reason}); until full, so standing the charger down "
                + (tomorrow is { } at ? $"until tomorrow's first window, ~{at.LocalDateTime:HH:mm}." : "until tomorrow's sun."),
                tomorrow);
        }

        return ChargerOwnership.End(ChargingSessionEndReason.NoSunLeftToday, $"{why} Nothing left of today will do better: {outlook.Reason}");
    }

    private static ChargingControlDecision StandDown(string reason, DateTimeOffset? until) =>
        new(ChargingControlAction.StandDown, null, reason, WaitUntil: until);
}
