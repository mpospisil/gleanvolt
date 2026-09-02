namespace Gleanvolt.Infrastructure.Vehicles.VwGroup;

/// <summary>
/// What the VW Group portal client needs to know to sign in and find a car (issue #139).
///
/// <para>A plain object constructed by the caller, <b>not</b> bound from configuration here: this
/// assembly is the client and nothing in it knows it is running inside a controller.
/// <c>VwGroupPortalOptionsResolver</c> in the host binds it from <c>Vehicle:DataAct:*</c>, with the
/// shorter <c>VW_*</c> environment names honoured beside it.</para>
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
    /// Which brand's portal to sign in to — <c>vw</c>, <c>audi</c>, <c>skoda</c>, <c>seat</c>,
    /// <c>cupra</c> or <c>bentley</c>. The ordinary way to configure this, because an owner knows what
    /// they drive and does not know a GUID.
    ///
    /// <para>Resolved through <see cref="VwGroupBrands"/> into <see cref="ResolvedClientId"/>.
    /// Ignored when <see cref="ClientId"/> is set explicitly.</para>
    /// </summary>
    public string Brand { get; init; } = string.Empty;

    /// <summary>
    /// The brand's OIDC client id, stated outright. Overrides <see cref="Brand"/>, and exists for the
    /// two cases the table cannot serve: a brand it does not list, and one whose id has changed.
    ///
    /// <para>No default, and no guessing: a wrong id fails in a way that looks exactly like a broken
    /// password, so an empty one that says what is missing is worth more than a hopeful one.</para>
    /// </summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>
    /// The client id actually used: <see cref="ClientId"/> when stated, otherwise whatever
    /// <see cref="Brand"/> resolves to, otherwise empty.
    /// </summary>
    public string ResolvedClientId =>
        !string.IsNullOrWhiteSpace(ClientId) ? ClientId.Trim() : VwGroupBrands.Resolve(Brand) ?? string.Empty;

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

    /// <summary>
    /// The portal locale the session belongs to. Travels in the OIDC <c>state</c>, which the portal
    /// decodes on the callback — it is not a nonce.
    /// </summary>
    public string Country { get; init; } = "de";

    /// <summary>The language half of the same pair. See <see cref="Country"/>.</summary>
    public string Language { get; init; } = "en";

    /// <summary>
    /// The <c>state</c> the portal expects: <c>country__language__BRAND</c>.
    ///
    /// <para><b>Not opaque, and not random.</b> The portal's <c>/services/callbacklogin</c> reads it to
    /// decide which brand and locale the returning code belongs to. Verified live: with a random state
    /// the callback bounces to the login page and every <c>proxy_api</c> call answers 401; with this
    /// one it lands on <c>/de/en/user.html</c> and the API answers.</para>
    /// </summary>
    public string State =>
        $"{Country}__{Language}__{VwGroupBrands.PortalKey(Brand) ?? "VOLKSWAGEN_PASSENGER_CARS"}";

    /// <summary>
    /// Where the identity provider is told to come back to: the portal's own login route, and
    /// <b>exactly</b> that.
    ///
    /// <para>The client id is registered against this one string, so it is not ours to decorate. A
    /// locale segment used to be spliced in here — <c>/de/en/login</c>, matching the paths the portal
    /// itself serves pages under — and the identity provider answered every such request with
    /// <c>400 invalid_request: Mismatching redirection URI</c> before any credential was sent. Verified
    /// against the live provider: with the segment 400 and no form, without it 200 and the sign-in
    /// form. Nothing may be appended to it.</para>
    /// </summary>
    public string RedirectUri => $"{PortalBaseUrl.TrimEnd('/')}/login";

    /// <summary>Whether enough is configured to attempt a sign-in at all.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ResolvedClientId)
        && !string.IsNullOrWhiteSpace(Username)
        && !string.IsNullOrWhiteSpace(Password);

    /// <summary>What is missing, in a sentence a log line can carry. Empty when nothing is.</summary>
    public string DescribeWhatIsMissing()
    {
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(ResolvedClientId))
        {
            // A misspelt brand and an absent one want different fixes, so they are not the same
            // sentence: one is a typo to correct, the other is a setting never supplied.
            missing.Add(
                string.IsNullOrWhiteSpace(Brand)
                    ? $"a brand (one of {VwGroupBrands.Known}) or an explicit client id"
                    : $"a known brand -- '{Brand.Trim()}' is not one of {VwGroupBrands.Known}; "
                      + "set an explicit client id if yours is missing");
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
