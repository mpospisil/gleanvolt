namespace Solax.Web;

/// <summary>
/// Configuration for the self-hosted web UI. Bound from the <c>"Web"</c> section. Disabled by
/// default, exactly like the Home Assistant integration: the two are alternative control surfaces
/// over the same seam, and neither is required for the controller to run.
/// </summary>
public sealed class WebOptions
{
    public const string SectionName = "Web";

    /// <summary>
    /// Master on/off switch. While false the host binds no socket at all — the UI is not merely
    /// unreachable, there is nothing listening to reach.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>The TCP port the UI listens on, on every interface. Plain HTTP; this is a LAN appliance.</summary>
    public int Port { get; init; } = 8080;

    /// <summary>
    /// Whether every page requires a signed-in session. Defaults to true: the UI is about to gain
    /// controls that write to a real inverter and EV charger, so an unauthenticated port is not the
    /// safe default the way it might be for a read-only page. Turning it off is a deliberate,
    /// logged-at-startup act, not something that can happen by omission.
    /// </summary>
    public bool RequireAuthentication { get; init; } = true;

    /// <summary>
    /// The single shared password, hashed with <see cref="Microsoft.AspNetCore.Identity.PasswordHasher{TUser}"/>.
    /// A secret, supplied out-of-band like the MQTT broker credentials — never checked into
    /// <c>appsettings.json</c>. Empty by default; with <see cref="RequireAuthentication"/> true and no
    /// hash configured, the host refuses to start rather than silently serving an unprotected UI.
    /// </summary>
    public string PasswordHash { get; init; } = "";
}
