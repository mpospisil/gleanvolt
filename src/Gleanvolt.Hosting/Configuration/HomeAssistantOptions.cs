namespace Gleanvolt.Hosting.Configuration;

/// <summary>
/// Configuration for the Home Assistant integration over MQTT. Bound from the <c>"HomeAssistant"</c>
/// section. Disabled by default. Broker credentials are secrets — supply via <c>.env</c> / env var,
/// not <c>appsettings.json</c>.
/// </summary>
public sealed class HomeAssistantOptions
{
    public const string SectionName = "HomeAssistant";

    /// <summary>Master on/off switch for the integration itself (independent of charge control).</summary>
    public bool Enabled { get; init; }

    public string BrokerHost { get; init; } = "localhost";

    public int BrokerPort { get; init; } = 1883;

    /// <summary>MQTT username (secret), or null for anonymous brokers.</summary>
    public string? Username { get; init; }

    /// <summary>MQTT password (secret), or null for anonymous brokers.</summary>
    public string? Password { get; init; }

    /// <summary>Home Assistant's MQTT discovery prefix (HA default is "homeassistant").</summary>
    public string DiscoveryPrefix { get; init; } = "homeassistant";

    /// <summary>
    /// Root of this controller's state and command topics. The PV system's id follows it, so two
    /// installations on one broker publish under <c>gleanvolt/home-roof/…</c> and
    /// <c>gleanvolt/other-roof/…</c> and cannot overwrite each other (issue #111).
    /// </summary>
    public string BaseTopic { get; init; } = "gleanvolt";

    /// <summary>
    /// The root of every entity's <c>unique_id</c>, and the discovery node id.
    ///
    /// <para><b>Changing this on a live installation costs the history.</b> Home Assistant keys an MQTT
    /// entity to its <c>unique_id</c>; a new one is a new entity, created as <c>sensor.…_2</c> because
    /// the old entity id is still taken, and every graph starts again. Nothing else here is like that —
    /// topics, names and device identity can all be changed freely — which is exactly why this is the
    /// one value that does <b>not</b> follow <c>Pv:Id</c> automatically.</para>
    ///
    /// <para>Empty means "take <c>Pv:Id</c>", which is what a fresh installation wants: one id,
    /// configured once. An installation that already has history in Home Assistant must keep the id it
    /// was created with — <c>deploy/docker-compose.yml</c> pins it for exactly that reason.</para>
    /// </summary>
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>
    /// Discovery configs to blank on connect: former <see cref="DeviceId"/> values whose retained
    /// configs are still on the broker.
    ///
    /// <para>Without this, Home Assistant re-creates yesterday's entities from the broker's retained
    /// messages after every restart, and a rename leaves two devices where there should be one. Only
    /// needed by someone who has deliberately changed <see cref="DeviceId"/> and accepted what that
    /// costs.</para>
    /// </summary>
    public string[] RetireDeviceIds { get; init; } = [];

    /// <summary>
    /// Topic prefixes — <c>{BaseTopic}/{id}</c> — whose retained state messages should be cleared on
    /// connect.
    ///
    /// <para>Renaming a topic leaves the old retained payloads sitting on the broker for ever. They
    /// feed nothing once the discovery configs point elsewhere, but anyone reading
    /// <c>mosquitto_sub -t '#' -v</c> afterwards sees two of everything and has no way to tell which is
    /// live. Set this to the old prefix once, after a rename.</para>
    /// </summary>
    public string[] RetireTopicPrefixes { get; init; } = [];

    /// <summary>How often the status is republished to MQTT.</summary>
    public TimeSpan StatusInterval { get; init; } = TimeSpan.FromSeconds(15);
}
