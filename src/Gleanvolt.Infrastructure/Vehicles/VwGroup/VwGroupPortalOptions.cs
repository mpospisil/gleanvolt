namespace Gleanvolt.Infrastructure.Vehicles.VwGroup;

/// <summary>
/// What the VW Group portal client needs to know to sign in and find a car (issue #139).
///
/// <para>A plain object constructed by the caller, <b>not</b> bound from configuration here: Phase 1
/// is the client and nothing that knows it is running inside a controller. Phase 2 (#140) binds it,
/// and that is also where it acquires an <c>.env</c> name.</para>
///
/// <para><b>The brand client id is a setting, not a constant.</b> VW passenger and commercial
/// vehicles share one; Audi, Škoda, SEAT/Cupra and Bentley each have their own, and they belong to
/// the portal rather than to us — there is no "register your own OAuth app" route here, so the id is
/// something to be configured rather than owned. Holding it as configuration is what makes an Elroq
/// or a Cupra an <c>.env</c> line rather than a release: #73's rule, applied one layer down.</para>
/// </summary>
public sealed class VwGroupPortalOptions
{
    /// <summary>The portal itself — the EU Data Act interface, and the origin the session belongs to.</summary>
    public string PortalBaseUrl { get; init; } = "https://eu-data-act.drivesomethinggreater.com";

    /// <summary>
    /// The identity provider. Separate from the portal on purpose: landing back on the portal is what
    /// completing the flow looks like, and the two hosts are how the client can tell.
    /// </summary>
    public string IdentityBaseUrl { get; init; } = "https://identity.vwgroup.io";

    /// <summary>
    /// The locale segment the portal's own paths carry. It decides which language the identity
    /// provider renders its forms in, and nothing else — but the forms are what get parsed, so it is
    /// worth being able to pin it.
    /// </summary>
    public string Locale { get; init; } = "de/en";

    /// <summary>
    /// The brand's OIDC client id. No default: a wrong one fails in a way that looks like a broken
    /// password, and a guessed one would be worse than an empty one that says what is missing.
    /// </summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>The VW ID the portal is entered with. A secret in the same sense the password is not.</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>
    /// The VW ID password. Not a read-only credential — the same account unlocks and locates the car —
    /// which is why #137 accepted it knowingly rather than casually. Never logged, never rendered.
    /// </summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>
    /// Which car, when the account can see more than one. Empty takes the only one, and fails when
    /// there is a choice to be made rather than picking for the owner.
    /// </summary>
    public string Vin { get; init; } = string.Empty;

    /// <summary>
    /// How long any single HTTP exchange may take. The portal is a batch delivery rather than a live
    /// API, and a download is a ZIP over a domestic uplink, so this is generous by the standards of
    /// the rest of this codebase.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// What the OIDC scope asks for. <c>openid cars profile</c> is what the portal's own client asks
    /// for; there is no PKCE in this flow, and the redirect_uri is the portal's <c>/login</c>.
    /// </summary>
    public string Scope { get; init; } = "openid cars profile";

    /// <summary>Where the identity provider is told to come back to. The portal's own login route.</summary>
    public string RedirectUri => $"{PortalBaseUrl.TrimEnd('/')}/{Locale.Trim('/')}/login";

    /// <summary>Whether enough is configured to attempt a sign-in at all.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(Username)
        && !string.IsNullOrWhiteSpace(Password);

    /// <summary>What is missing, in a sentence a log line can carry. Empty when nothing is.</summary>
    public string DescribeWhatIsMissing()
    {
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(ClientId))
        {
            missing.Add("a brand client id");
        }

        if (string.IsNullOrWhiteSpace(Username))
        {
            missing.Add("a VW ID");
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            missing.Add("a password");
        }

        return missing.Count == 0 ? string.Empty : string.Join(", ", missing);
    }
}
