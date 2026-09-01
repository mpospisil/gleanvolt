namespace Gleanvolt.Web;

/// <summary>
/// The HTTP API as the UI should display it (issue #142): whether it is on, where it is, and which
/// keys may call it.
///
/// <para>Handed over by the host exactly as <see cref="WebBuildInfo"/> and
/// <see cref="MqttDisplayOptions"/> are — the <c>"Api"</c> section is bound in
/// <c>Gleanvolt.Hosting</c>, an assembly this one must not reference — and the paths come from
/// <c>GleanvoltApi</c>'s own constants rather than from literals repeated here, so a route that moves
/// cannot leave the page pointing at where it used to be.</para>
///
/// <para>Read-only, and not merely for now: <c>ApiOptions</c> is bound once at startup,
/// <c>ValidateKeyConfig</c> has already refused the one combination that cannot be honoured, and
/// <c>MapGleanvoltApi</c> has already mapped — or not mapped — the routes from it. A control that
/// generated a key here would be editing a copy of a decision that has been made.</para>
/// </summary>
/// <param name="Enabled">
/// Whether the API is mapped at all. False by default, so "off" is the common case and has to read as
/// a deliberate default rather than a fault.
/// </param>
/// <param name="BasePath">Where every route sits — <c>/api/v1</c> — which is also the index.</param>
/// <param name="DocumentPath">Where the OpenAPI document is served.</param>
/// <param name="Port">
/// <c>Web:Port</c>, the port the appliance actually listens on. Not what the address below is built
/// from — see <see cref="AddressFrom"/> — but the answer when the browser's own address carries no
/// port to show, which is what a reverse proxy on 80 or 443 leaves behind.
/// </param>
/// <param name="Keys">
/// The configured keys, as name → secret. Never empty while <paramref name="Enabled"/> is true: the
/// host refuses to start an API with no key.
/// </param>
public sealed record ApiDisplayOptions(
    bool Enabled,
    string BasePath = "",
    string DocumentPath = "",
    int Port = 0,
    IReadOnlyList<ApiKeyDisplay>? Keys = null)
{
    /// <summary>The API switched off, which is the default and has to read as one.</summary>
    public static ApiDisplayOptions Off { get; } = new(Enabled: false);

    /// <summary>The keys, never null so the markup can enumerate without a guard.</summary>
    public IReadOnlyList<ApiKeyDisplay> Keys { get; init; } = Keys ?? [];

    /// <summary>
    /// Whether any key's secret may be printed. All of them are withheld together — the host decides
    /// once, for the whole page — so the section can say why in one place rather than per row.
    /// </summary>
    public bool SecretsWithheld => Keys.Count > 0 && Keys.All(key => key.Secret is null);

    /// <summary>
    /// The API's address, built from the address the browser actually used to reach this page rather
    /// than from <see cref="Port"/>.
    ///
    /// <para>The whole point of the line is that it can be pasted into an MCP server's configuration or
    /// into a <c>curl</c> on another machine. A hostname or a reverse proxy in front means the
    /// configured port is not necessarily the one that works, while the address that just delivered
    /// this page demonstrably is — so that is what it is built from, port and all.</para>
    /// </summary>
    /// <param name="browserBaseUri">
    /// The document's base URI, as the browser sees it (<c>NavigationManager.BaseUri</c>).
    /// </param>
    public ApiAddressDisplay AddressFrom(string? browserBaseUri)
    {
        // The scheme check is not belt-and-braces: on a Unix host `Uri.TryCreate("/pv-system",
        // UriKind.Absolute, ...)` succeeds, as a *file* URI, and would put "file:///api/v1" on the page.
        if (!Uri.TryCreate(browserBaseUri, UriKind.Absolute, out var browser)
            || (browser.Scheme != Uri.UriSchemeHttp && browser.Scheme != Uri.UriSchemeHttps))
        {
            // Nothing to build an absolute URL from. The relative paths are still true, and a page that
            // printed a guessed host would be worse than one that printed a path.
            return new ApiAddressDisplay(BasePath, DocumentPath, PortIsImplicit: true);
        }

        var authority = browser.GetLeftPart(UriPartial.Authority);

        // A default port is omitted from the authority, which is correct for a URL and useless as an
        // answer to "which port?" -- so the page is told to state Web:Port beside it instead.
        return new ApiAddressDisplay(authority + BasePath, authority + DocumentPath, browser.IsDefaultPort);
    }
}

/// <summary>
/// One configured key. The <paramref name="Name"/> earns its place: it is not a credential, it is what
/// reaches the log and the recorded charging session as the source of an action, so this is where you
/// find out that an action will be attributed to <c>client</c> rather than to something you would
/// recognise later.
/// </summary>
/// <param name="Name">The key's name, always shown.</param>
/// <param name="Secret">
/// The key itself, or <see langword="null"/> when this page may not print it — which is whenever the
/// UI is not behind a login. Null rather than a flag the markup has to remember to check: the page
/// cannot disclose what it was never given.
/// </param>
public sealed record ApiKeyDisplay(string Name, string? Secret);

/// <summary>Where the API is, as an address that can be pasted somewhere else and still work.</summary>
/// <param name="BaseUrl">Absolute, with the port when the browser's own address carried one.</param>
/// <param name="DocumentUrl">The OpenAPI document, absolute on the same terms.</param>
/// <param name="PortIsImplicit">
/// Whether the address carries no explicit port — a proxy answering on 80 or 443 — in which case the
/// direct LAN address is <c>host:Web:Port</c> whatever the proxy is doing, and the page says so.
/// </param>
public sealed record ApiAddressDisplay(string BaseUrl, string DocumentUrl, bool PortIsImplicit);
