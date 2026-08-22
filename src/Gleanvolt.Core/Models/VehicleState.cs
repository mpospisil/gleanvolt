using Gleanvolt.Core.Enums;

namespace Gleanvolt.Core.Models;

/// <summary>
/// A normalised snapshot of the EV's own battery state, as last reported by whatever source is
/// feeding it — a Home Assistant integration over MQTT, an OBD dongle, anything.
///
/// <para><b>Everything except <see cref="CapturedAt"/> is optional</b>, and that is the whole point:
/// no two sources report the same set. The ID.4 via <c>volkswagen_connect</c> gives SOC, charge state
/// and plug state but no target SOC; the EU Data Act feed gives charge settings but not always a plug
/// state; an OBD dongle gives SOC alone. Consumers must degrade rather than assume, so absent means
/// null or <c>Unknown</c> — never zero, and never an exception.</para>
///
/// <para><see cref="CapturedAt"/> is mandatory because it is the <b>car's</b> capture time, not the
/// time the message arrived, and without it staleness cannot be judged. That matters more here than
/// anywhere else in this codebase: a parked car's report is routinely hours old (2h and 3.5h were
/// both observed on the reference install), and the cloud session behind it expired after ~15 hours
/// needing a human with an email OTP to restore. So this data is <b>advisory</b>: it may be old, it
/// may stop arriving entirely, and nothing that writes to hardware may depend on it.</para>
/// </summary>
/// <param name="CapturedAt">
/// When the <b>vehicle</b> produced this reading — not when we received or parsed it. Carries an
/// offset so a source reporting local time is not silently reinterpreted as UTC.
/// </param>
/// <param name="SocPercent">The drive battery's state of charge, 0-100, or null if not reported.</param>
/// <param name="RangeKm">
/// The range the <b>car</b> reckons is left in it, in kilometres, or null if not reported. Its own
/// estimate off its own recent consumption, which is the only reason it is worth showing: nothing here
/// could compute it, and it is the figure that actually answers "is 80% enough for the trip?".
///
/// <para>Display only — no plan reads it. It is quoted at whatever SOC
/// <see cref="CapturedAt"/> belongs to, so a stale reading's range is stale by exactly as much.</para>
/// </param>
/// <param name="ChargeTimeRemaining">
/// How much longer the <b>car</b> says it needs to finish charging, or null if not reported. The car's
/// own estimate, not one of ours: it knows its charge curve, its taper and its target, and we know
/// none of those. Null and <see cref="TimeSpan.Zero"/> are different facts — zero means the car says
/// it is done.
/// </param>
/// <param name="ChargeState">What the car says it is doing, or <see cref="VehicleChargeState.Unknown"/>.</param>
/// <param name="PlugState">Whether the car says a cable is connected, or <see cref="VehiclePlugState.Unknown"/>.</param>
/// <param name="SourceId">
/// Which feed this came from (e.g. <c>"id4"</c>), purely for display and diagnostics. Phase 1 tracks a
/// single vehicle, so this identifies the source rather than selecting between vehicles.
/// </param>
public sealed record VehicleState(
    DateTimeOffset CapturedAt,
    double? SocPercent = null,
    double? RangeKm = null,
    TimeSpan? ChargeTimeRemaining = null,
    VehicleChargeState ChargeState = VehicleChargeState.Unknown,
    VehiclePlugState PlugState = VehiclePlugState.Unknown,
    string? SourceId = null)
{
    /// <summary>
    /// How old this reading is at <paramref name="now"/>. Can be negative if the source's clock runs
    /// ahead of ours; callers that care should treat that as fresh rather than as an error, which is
    /// what <see cref="IsStaleAt"/> does.
    /// </summary>
    public TimeSpan AgeAt(DateTimeOffset now) => now - CapturedAt;

    /// <summary>
    /// Whether this reading is older than <paramref name="maxAge"/> and so must not be relied on.
    ///
    /// <para>Being stale is not the same as being wrong: a parked car's SOC doesn't drift, so a
    /// twelve-hour-old reading of a car that hasn't moved is perfectly accurate. The guard exists to
    /// catch a <i>dead feed</i> — an expired cloud session, a broken automation, a car that stopped
    /// reporting mid-charge — not to reject merely old numbers.</para>
    /// </summary>
    public bool IsStaleAt(DateTimeOffset now, TimeSpan maxAge) => AgeAt(now) > maxAge;
}
