namespace Gleanvolt.Core.Models;

/// <summary>
/// Limits the owner has put on <em>how</em> a targeted request may be met (issue #128): when the
/// charger may run, and how much may be bought.
///
/// <para><b>Constraints, and deliberately not a schedule.</b> The obvious shape for "let me edit the
/// plan" is to accept the edited plan back and execute it. That cannot work here, and the reason is
/// the mode's whole point: <see cref="Strategies.TargetedChargePlanner"/> rebuilds the plan on every
/// poll from a refreshed forecast and the measured delivery, so a sunnier afternoon than forecast
/// shrinks the grid block before any of it is bought. A plan quoted at 22:00 is stale by 22:05, by
/// design. Replaying one would give that up — and worse, would go on buying grid for energy already in
/// the car, because the figures it was computed from stopped being true the moment it was quoted.</para>
///
/// <para>So what a caller edits is turned into this, and the planner keeps planning <em>inside</em> it.
/// The window is the owner's; what happens in the window is still the forecast's to decide.</para>
///
/// <para><b>A constraint may reduce what is delivered. It may never make the plan lie about what will
/// be.</b> Anything a constraint puts out of reach comes back as
/// <see cref="TargetedChargePlan.ShortfallWh"/>, on exactly the terms a departure that is too close
/// already does — the planner does not quietly relax a limit, and does not quietly under-report.</para>
/// </summary>
/// <param name="NotBefore">
/// The charger may not run before this. Null for no lower bound — which is what every request that
/// says nothing gets, and is why an absent constraint set changes nothing.
/// </param>
/// <param name="NotAfter">
/// The charger may not run after this. Null for no upper bound; the deadline already applies either way,
/// so this can only ever pull the window in.
/// </param>
/// <param name="ForbiddenWindows">
/// Stretches that must stay idle — a tariff's peak hours, a neighbour asleep on the other side of the
/// wall. Overlapping and out-of-order entries are fine; what matters is the union.
/// </param>
/// <param name="MaxGridEnergyWh">
/// The most that may be bought from the grid over the whole plan. Null for no cap. Zero is a real
/// value and means "sun only" — the request is then met from the roof or not at all, and the remainder
/// is reported as shortfall rather than silently imported.
/// </param>
public sealed record TargetedChargeConstraints(
    DateTimeOffset? NotBefore = null,
    DateTimeOffset? NotAfter = null,
    IReadOnlyList<TimeWindow>? ForbiddenWindows = null,
    double? MaxGridEnergyWh = null)
{
    /// <summary>The constraint set that constrains nothing — what a request without one behaves as.</summary>
    public static TargetedChargeConstraints None { get; } = new();

    /// <summary>Whether anything here actually narrows a plan.</summary>
    public bool IsEmpty =>
        NotBefore is null
        && NotAfter is null
        && MaxGridEnergyWh is null
        && ForbiddenWindows is not { Count: > 0 };

    /// <summary>Whether <paramref name="instant"/> falls inside a window that must stay idle.</summary>
    public bool IsForbiddenAt(DateTimeOffset instant) =>
        ForbiddenWindows?.Any(window => window.Covers(instant)) == true;

    /// <summary>
    /// Whether charging is allowed to run at <paramref name="instant"/> — all four limits at once.
    /// </summary>
    public bool Allows(DateTimeOffset instant) =>
        (NotBefore is not { } before || instant >= before)
        && (NotAfter is not { } after || instant < after)
        && !IsForbiddenAt(instant);
}

/// <summary>
/// A half-open stretch of time, <c>[Start, End)</c>. Half-open so that two windows meeting at an
/// instant neither overlap nor leave a gap, which is the property every one of these comparisons wants.
/// </summary>
/// <param name="Start">When it begins.</param>
/// <param name="End">When it ends. At or before <paramref name="Start"/> means an empty window, which covers nothing.</param>
public sealed record TimeWindow(DateTimeOffset Start, DateTimeOffset End)
{
    /// <summary>Whether <paramref name="instant"/> falls inside.</summary>
    public bool Covers(DateTimeOffset instant) => instant >= Start && instant < End;

    /// <summary>Whether this window shares any instant with <c>[start, end)</c>.</summary>
    public bool Overlaps(DateTimeOffset start, DateTimeOffset end) => start < End && Start < end;

    /// <summary>How long it runs for; never negative.</summary>
    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;
}
