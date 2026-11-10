using Gleanvolt.Web.Auth;

namespace Gleanvolt.Web;

/// <summary>
/// Configuration for the self-hosted web UI. Bound from the <c>"Web"</c> section.
///
/// <para>Every property has a default that produces a working UI, so a deployment that configures
/// nothing at all still gets one: the UI is what the controller is normally operated through, and
/// the Home Assistant integration — which needs a broker, credentials and an onboarding flow — is
/// the opt-in surface instead. Setting a password is an advanced option (see
/// <see cref="PasswordHash"/>), not a precondition for the thing starting.</para>
/// </summary>
public sealed class WebOptions
{
    public const string SectionName = "Web";

    /// <summary>
    /// Master on/off switch, on by default. While false the host binds no socket at all — the UI is
    /// not merely unreachable, there is nothing listening to reach.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The TCP port the UI listens on, on every interface. Plain HTTP; this is a LAN appliance.
    ///
    /// <para>The HTTP API shares it — one appliance, one socket — so this is the port for both, and
    /// either surface being enabled is what makes the host listen at all.</para>
    /// </summary>
    public int Port { get; init; } = 8090;

    /// <summary>
    /// Whether the UI announces itself on the LAN over multicast DNS (issue #363), so a phone or a laptop
    /// reaches it as <c>http://gleanvolt.local:8090</c> and finds it in any Bonjour/DNS-SD browser without
    /// knowing the machine's address. On by default; ignored while <see cref="Enabled"/> is false, since
    /// there is nothing to point at then.
    /// </summary>
    public bool Advertise { get; init; } = true;

    /// <summary>
    /// The name announced: <c>gleanvolt</c> gives <c>gleanvolt.local</c>. Gleanvolt's own name, never the
    /// machine's — that one belongs to avahi or Bonjour on the host, and two responders defending one name
    /// fight. A second instance on the LAN with the same name becomes <c>gleanvolt-2.local</c>. Letters,
    /// digits and hyphens; anything else falls back to the default, with a warning.
    /// </summary>
    public string AdvertiseName { get; init; } = DefaultAdvertiseName;

    /// <summary>The name announced when <see cref="AdvertiseName"/> is not set.</summary>
    public const string DefaultAdvertiseName = "gleanvolt";

    /// <summary>
    /// Whether every page requires a signed-in session. Three-valued on purpose:
    /// <list type="bullet">
    /// <item><description><c>null</c> (unset, the default) — follow <see cref="PasswordHash"/>:
    /// configuring a password turns the login on, and configuring nothing leaves it off. This is
    /// what makes the password an advanced option rather than a required one, and it means a
    /// password can never be set and then silently not enforced.</description></item>
    /// <item><description><c>true</c> — required. With no <see cref="PasswordHash"/> this is a
    /// misconfiguration and the host refuses to start, rather than serving an unprotected UI to
    /// someone who asked for a protected one.</description></item>
    /// <item><description><c>false</c> — never required, even if a hash is configured. Logged as a
    /// warning at startup.</description></item>
    /// </list>
    /// Read <see cref="AuthenticationRequired"/>, not this, when deciding whether to enforce.
    /// </summary>
    public bool? RequireAuthentication { get; init; }

    /// <summary>
    /// The single shared password, hashed with <see cref="Microsoft.AspNetCore.Identity.PasswordHasher{TUser}"/>.
    /// A secret, supplied out-of-band like the MQTT broker credentials — never checked into
    /// <c>appsettings.json</c>. Generate one with <c>dotnet Gleanvolt.Worker.dll hash-password &lt;password&gt;</c>
    /// (or <c>docker run --rm &lt;image&gt; hash-password &lt;password&gt;</c>) and set it via
    /// <c>Web__PasswordHash</c>.
    ///
    /// <para>Empty by default, which means an open UI on the LAN — appropriate for the appliance
    /// this is, and the reason the UI can start with no configuration at all. Setting it is what
    /// turns the login on.</para>
    /// </summary>
    public string PasswordHash { get; init; } = "";

    /// <summary>
    /// The hash of the recovery key that can reset <see cref="PasswordHash"/> from the login page
    /// (#301). Written beside the password whenever one is set from the web UI, and replaced with every
    /// reset; empty for a password set by hand, which then resets with a one-time code instead.
    /// </summary>
    public string RecoveryKeyHash { get; init; } = "";

    /// <summary>
    /// Whether a login is actually enforced: <see cref="RequireAuthentication"/> when it says
    /// anything, otherwise "yes if a <see cref="PasswordHash"/> is configured". The single answer
    /// every consumer should ask for — the authorization policy, the startup warning and the
    /// fail-fast all read this rather than re-deriving it.
    /// </summary>
    public bool AuthenticationRequired =>
        RequireAuthentication ?? !string.IsNullOrWhiteSpace(PasswordHash);

    /// <summary>
    /// The same answer as <see cref="AuthenticationRequired"/>, but for the password in force now
    /// rather than the one configuration held at startup (issue #254). A password chosen on the setup
    /// page lands in <paramref name="password"/> and not in these options, so anything that decides
    /// per request or render -- whether an editor is editable, whether a secret is shown -- asks this,
    /// or it keeps behaving as if there were no login until the next restart.
    /// </summary>
    public bool LoginEnforced(WebPasswordState password) =>
        RequireAuthentication ?? password.IsSet;
}
