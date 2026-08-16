namespace Solax.Hosting.Configuration;

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

    /// <summary>Root topic for this controller's state/command topics.</summary>
    public string BaseTopic { get; init; } = "solax";

    /// <summary>Stable id used for the HA device and entity unique ids (must not change across restarts).</summary>
    public string DeviceId { get; init; } = "solax_controller";

    /// <summary>Friendly device name shown in Home Assistant.</summary>
    public string DeviceName { get; init; } = "SolaX Local Controller";

    /// <summary>How often the status is republished to MQTT.</summary>
    public TimeSpan StatusInterval { get; init; } = TimeSpan.FromSeconds(15);
}
