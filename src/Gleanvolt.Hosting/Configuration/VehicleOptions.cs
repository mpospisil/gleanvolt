namespace Gleanvolt.Hosting.Configuration;

/// <summary>
/// Configuration for reading the EV's own telemetry off MQTT. Bound from the <c>"Vehicle"</c> section.
///
/// <para>Off by default, like every other feature that depends on something outside the LAN. Unlike
/// <c>ChargeControl</c> and <c>BatteryHold</c> it writes to nothing, so there is no dry-run: the worst
/// a misconfiguration can do in this phase is leave the dashboard card empty.</para>
///
/// <para>The broker settings are deliberately independent of the <c>HomeAssistant</c> section rather
/// than inherited from it. The two features are separately optional — you can read the car without
/// publishing to Home Assistant, or the reverse — and coupling them would mean one section silently
/// changing the other's behaviour.</para>
/// </summary>
public sealed class VehicleOptions
{
    public const string SectionName = "Vehicle";

    /// <summary>Whether to subscribe at all. Off by default; nothing is published or read when false.</summary>
    public bool Enabled { get; init; }

    public string BrokerHost { get; init; } = "localhost";

    public int BrokerPort { get; init; } = 1883;

    /// <summary>Broker username, if it requires one. Secret — supply via <c>.env</c> or an environment variable.</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>Broker password, if it requires one. Secret — supply via <c>.env</c> or an environment variable.</summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>
    /// The topic to subscribe to, which must be the one your Home Assistant automation publishes to.
    ///
    /// <para>A single concrete topic, not a wildcard: this phase tracks one vehicle, and a
    /// <c>+</c> subscription across several would quietly make the newest message win regardless of
    /// which car it described. Multi-vehicle support needs an explicit active-vehicle selector, which
    /// is a later phase.</para>
    /// </summary>
    public string Topic { get; init; } = "gleanvolt/vehicle/state";

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
    /// The drive battery's <b>usable</b> capacity, in kilowatt-hours — the figure the car's own SOC is
    /// a percentage of, not the gross pack size on the brochure. An ID.4 Pro is 77 usable of 82 gross;
    /// using the larger number would overshoot every SOC-based target by 6%.
    ///
    /// <para><b>No default, and that is deliberate.</b> Zero means "not configured", and the only thing
    /// it costs is the <i>Battery target (%)</i> basis on the targeted plan: with no capacity there is
    /// no honest conversion from "80%" to kilowatt-hours, and a guessed pack size would make every such
    /// target quietly wrong rather than visibly unavailable. Everything else works exactly as before.</para>
    /// </summary>
    public double BatteryCapacityKWh { get; init; }

    /// <summary>
    /// How much of what the charger meters actually reaches the cells, (0..1]. Applied to SOC-based
    /// targets: the target is measured at the charger, and asking for the difference in the pack alone
    /// arrives short by the on-board rectifier's losses.
    ///
    /// <para>0.9 is a fair single-phase-to-three-phase AC average. It is not the same number as
    /// <c>Forecast:ChargeEfficiency</c>, which is the <em>home battery's</em> PV → pack figure; the two
    /// describe different hardware and are configured separately for that reason.</para>
    /// </summary>
    public double ChargeEfficiency { get; init; } = 0.9;

    /// <summary>
    /// How long to wait before retrying after a failed connection, and how often the loop re-checks
    /// that the subscription is still alive. This is not a poll interval — telemetry arrives by
    /// subscription, whenever the publisher sends it.
    /// </summary>
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

    public TimeSpan ReconnectInterval { get; init; } = TimeSpan.FromSeconds(30);
}
