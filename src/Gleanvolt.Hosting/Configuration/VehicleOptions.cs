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
    /// How long to wait before retrying after a failed connection, and how often the loop re-checks
    /// that the subscription is still alive. This is not a poll interval — telemetry arrives by
    /// subscription, whenever the publisher sends it.
    /// </summary>
    public TimeSpan ReconnectInterval { get; init; } = TimeSpan.FromSeconds(30);
}
