namespace Gleanvolt.Hosting.Configuration;

/// <summary>
/// The car's readings, wherever they come from: bound from the <c>"Vehicle"</c> section. Since #486 nothing here
/// connects anywhere; a car plugin is the only feed (<c>Vehicle:External</c>).
/// </summary>
public sealed class VehicleOptions
{
    public const string SectionName = "Vehicle";

    /// <summary>
    /// How old a reading may be before it is shown as stale and (in a later phase) refused as an input
    /// to any decision.
    ///
    /// <para>Twelve hours sounds enormous and isn't: a parked car's SOC does not drift, so an
    /// overnight-old reading of a car that hasn't moved is exactly correct. What this has to catch is a
    /// <b>dead feed</b> — on the reference install the upstream cloud session expired after roughly
    /// fifteen hours and needed a human with an email OTP to restore, so the guard is sized to notice
    /// that rather than to reject merely old numbers.</para>
    /// </summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromHours(12);

    /// <summary>
    /// Ask the car only when somebody wants to know — a person pressing the button, or a plan being
    /// prepared — rather than on a clock (issue #168).
    ///
    /// <para><b>Off by default</b>, so an installation that has not asked for this behaves exactly as
    /// it did. On, the update worker stops polling and the dashboard shows no battery or range until
    /// it is asked for one.</para>
    ///
    /// <para>The reason it is worth having: on the EU Data Act portal a reading is <b>2-4 hours
    /// behind</b> — measured across a 7h43m window, best case 1h48m — because VW's pipeline sits on
    /// each report the car makes. A card showing that number continuously is confident and wrong, and
    /// the eye reads the number rather than the age beside it. A figure nobody requested is a figure
    /// nobody checked the age of.</para>
    ///
    /// <para>The cost, which is real: nothing polls, so
    /// <c>ChargingSessionTracker</c> records no state of charge against a session. On a feed hours
    /// behind, those samples were the same pre-session number repeated rather than a charge curve —
    /// but an install that wants them back turns this off.</para>
    /// </summary>
    public bool OnDemandOnly { get; init; }
}
