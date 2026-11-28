using Gleanvolt.Core.Models;

namespace Gleanvolt.Web;

/// <summary>
/// The MQTT link this controller keeps, as the UI should display it (issue #143): the Home Assistant integration,
/// which publishes state and takes commands. The car's own telemetry topic was the second link until #486.
///
/// <para>Handed over by the host exactly as <see cref="WebBuildInfo"/> and <see cref="VehicleDisplayOptions"/> are:
/// the <c>"HomeAssistant"</c> section is bound in <c>Gleanvolt.Hosting</c>, an assembly this one must not reference,
/// and the topic strings belong to <c>HaDiscovery</c> over there.</para>
/// </summary>
public sealed record MqttDisplayOptions(HomeAssistantMqttDisplay HomeAssistant)
{
    /// <summary>The link not configured — what an installation that speaks no MQTT at all is handed.</summary>
    public static MqttDisplayOptions None { get; } = new(new HomeAssistantMqttDisplay(MqttConnectionDisplay.Off));

    /// <summary>Whether the link is on, which is what decides between a section and one line saying it is off.</summary>
    public bool AnyEnabled => HomeAssistant.Connection.Enabled;
}

/// <summary>
/// What one MQTT connection dials and as whom. The address here is the one <em>the controller</em>
/// dials, not one a browser can reach — on the reference install it is <c>mosquitto:1883</c>, a
/// compose network name — and the page must not blur the two.
/// </summary>
/// <param name="Enabled">Whether the worker connects at all. Off by default.</param>
/// <param name="BrokerHost">Broker hostname as configured.</param>
/// <param name="BrokerPort">Broker port as configured.</param>
/// <param name="Username">The broker account, which is not a credential on its own and is half of every "why is the broker refusing us".</param>
/// <param name="Password">
/// The broker password, or <see langword="null"/> when this page may not print it — which is
/// whenever the UI is not behind a login. Null rather than a flag the markup has to remember to
/// check: the page cannot disclose what it was never given.
/// </param>
/// <param name="HasPassword">
/// Whether a password is configured at all, so "anonymous broker" and "configured but withheld" do
/// not render identically.
/// </param>
/// <param name="ClientId">
/// The client id the worker connects with — what the broker's own log and its ACL file name this
/// controller as, and therefore the string to search for when the broker is the suspect.
/// </param>
public sealed record MqttConnectionDisplay(
    bool Enabled,
    string BrokerHost = "",
    int BrokerPort = 1883,
    string Username = "",
    string? Password = null,
    bool HasPassword = false,
    string ClientId = "")
{
    /// <summary>The link is switched off, which is the default and has to read as one.</summary>
    public static MqttConnectionDisplay Off { get; } = new(Enabled: false);

    /// <summary>The broker as one address, the way every other address on this page is shown.</summary>
    public string Broker => $"{BrokerHost}:{BrokerPort}";

    /// <summary>Whether the broker is being dialled anonymously.</summary>
    public bool IsAnonymous => string.IsNullOrWhiteSpace(Username) && !HasPassword;

    /// <summary>A password is configured, but this page is not allowed to print it.</summary>
    public bool PasswordWithheld => HasPassword && Password is null;
}

/// <summary>One topic worth naming on a configuration page, and which way it flows.</summary>
/// <param name="Purpose">What it carries, in words — the column that makes the topic list readable.</param>
/// <param name="Topic">The full topic, ready to paste into a subscription.</param>
/// <param name="Inbound">
/// Whether the controller <em>subscribes</em> to it. True means a message on it can change what the
/// charger is doing, which is the difference between a reporting topic and a control one.
/// </param>
/// <param name="PurposePhrase">The purpose for the page to say in the viewer's language (#290).</param>
public sealed record MqttTopicDisplay(string Purpose, string Topic, bool Inbound, Phrase? PurposePhrase = null);

/// <summary>The Home Assistant link: what it publishes under, as what device, and how often.</summary>
/// <param name="Connection">Broker, credentials and client id.</param>
/// <param name="DiscoveryPrefix">Where the retained discovery configs go; Home Assistant's own default is <c>homeassistant</c>.</param>
/// <param name="DeviceId">
/// The root of every entity's <c>unique_id</c>. The one value here that is expensive to change — a new
/// one is a new entity in Home Assistant, and every graph starts again — which is why the page says so.
/// </param>
/// <param name="TopicPrefix">
/// <c>{HomeAssistant:BaseTopic}/{Pv:Id}</c>. The single most useful string in the section and the one
/// nothing else prints: it is composed at startup from two separate settings.
/// </param>
/// <param name="Topics">The well-known topics under the prefix. Every other entity follows the pattern the page states.</param>
/// <param name="StatusInterval">How often the status is republished — usually the answer to "why is the dashboard stale?".</param>
/// <param name="RetiredDeviceIds">Former device ids whose retained discovery configs are blanked on connect. Rare; shown only when set.</param>
/// <param name="RetiredTopicPrefixes">Former topic prefixes whose retained state is cleared on connect. Rare; shown only when set.</param>
public sealed record HomeAssistantMqttDisplay(
    MqttConnectionDisplay Connection,
    string DiscoveryPrefix = "",
    string DeviceId = "",
    string TopicPrefix = "",
    IReadOnlyList<MqttTopicDisplay>? Topics = null,
    TimeSpan StatusInterval = default,
    IReadOnlyList<string>? RetiredDeviceIds = null,
    IReadOnlyList<string>? RetiredTopicPrefixes = null)
{
    /// <summary>The well-known topics, never null so the markup can enumerate without a guard.</summary>
    public IReadOnlyList<MqttTopicDisplay> Topics { get; init; } = Topics ?? [];

    public IReadOnlyList<string> RetiredDeviceIds { get; init; } = RetiredDeviceIds ?? [];

    public IReadOnlyList<string> RetiredTopicPrefixes { get; init; } = RetiredTopicPrefixes ?? [];

    /// <summary>Whether anything is being retired, which is the uncommon case and the only one worth showing.</summary>
    public bool IsRetiringAnything => RetiredDeviceIds.Count > 0 || RetiredTopicPrefixes.Count > 0;
}
