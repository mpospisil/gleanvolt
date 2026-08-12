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
}
