using Gleanvolt.Infrastructure.Vehicles.VwGroup;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// That naming a brand is enough to sign in, which is the whole point of the table: an owner knows
/// what they drive and does not know a GUID.
///
/// <para>These pin the <b>resolution rules</b>, not the id values. The ids are reverse-engineered and
/// VW can change one without telling anybody; a test asserting a literal GUID would then fail while
/// saying nothing useful about this code. What must not drift is the precedence, the two shared
/// clients, and the refusal to treat a typo as an absence.</para>
/// </summary>
public class VwGroupBrandsTests
{
    [Theory]
    [InlineData("vw")]
    [InlineData("audi")]
    [InlineData("skoda")]
    [InlineData("seat")]
    [InlineData("cupra")]
    [InlineData("bentley")]
    public void Every_listed_brand_resolves(string brand)
    {
        Assert.False(string.IsNullOrWhiteSpace(VwGroupBrands.Resolve(brand)));
        Assert.Contains(brand, VwGroupBrands.Known);
    }

    [Theory]
    [InlineData("VW")]
    [InlineData("Skoda")]
    [InlineData("  cupra  ")]
    public void Brand_names_are_case_and_space_insensitive(string brand) =>
        Assert.False(string.IsNullOrWhiteSpace(VwGroupBrands.Resolve(brand)));

    /// <summary>Facts about the portal, not conveniences here: one client each, two brands apiece.</summary>
    [Fact]
    public void Vw_shares_a_client_with_commercial_vehicles_and_seat_with_cupra()
    {
        Assert.Equal(VwGroupBrands.Resolve("vw"), VwGroupBrands.Resolve("vwn"));
        Assert.Equal(VwGroupBrands.Resolve("seat"), VwGroupBrands.Resolve("cupra"));
        Assert.NotEqual(VwGroupBrands.Resolve("vw"), VwGroupBrands.Resolve("seat"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("tesla")]
    [InlineData("vollkswagen")]
    public void Anything_else_resolves_to_nothing(string? brand)
    {
        Assert.Null(VwGroupBrands.Resolve(brand));
        Assert.False(VwGroupBrands.IsKnown(brand));
    }

    [Fact]
    public void A_brand_is_enough_to_be_configured()
    {
        var options = new VwGroupPortalOptions
        {
            Brand = "vw", Username = "owner@example.com", Password = "hunter2",
        };

        Assert.True(options.IsConfigured);
        Assert.Equal(VwGroupBrands.Resolve("vw"), options.ResolvedClientId);
        Assert.Equal(string.Empty, options.DescribeWhatIsMissing());
    }

    [Fact]
    public void An_explicit_client_id_wins_over_the_brand()
    {
        var options = new VwGroupPortalOptions
        {
            Brand = "vw", ClientId = "mine", Username = "owner@example.com", Password = "hunter2",
        };

        Assert.Equal("mine", options.ResolvedClientId);
    }

    /// <summary>
    /// A brand that is missing from the table is still usable — the escape hatch the table's own
    /// staleness makes necessary.
    /// </summary>
    [Fact]
    public void An_unlisted_brand_is_reachable_by_stating_the_id()
    {
        var options = new VwGroupPortalOptions
        {
            Brand = "lamborghini", ClientId = "some-id", Username = "o@e.com", Password = "p",
        };

        Assert.True(options.IsConfigured);
        Assert.Equal("some-id", options.ResolvedClientId);
    }

    /// <summary>A typo and an absence want different fixes, so they must not read the same.</summary>
    [Fact]
    public void A_misspelt_brand_says_so_rather_than_reporting_nothing_configured()
    {
        var typo = new VwGroupPortalOptions { Brand = "vollkswagen", Username = "o@e.com", Password = "p" };
        var absent = new VwGroupPortalOptions { Username = "o@e.com", Password = "p" };

        Assert.False(typo.IsConfigured);
        Assert.Contains("vollkswagen", typo.DescribeWhatIsMissing());
        Assert.DoesNotContain("vollkswagen", absent.DescribeWhatIsMissing());
        Assert.NotEqual(typo.DescribeWhatIsMissing(), absent.DescribeWhatIsMissing());
    }
}

/// <summary>
/// The redirect URI, which is not ours to decorate: the client id is registered against one exact
/// string, and a locale segment spliced into it had the identity provider answering
/// <c>400 invalid_request: Mismatching redirection URI</c> before any credential was sent.
/// </summary>
public class VwGroupRedirectUriTests
{
    [Fact]
    public void The_redirect_uri_is_the_portal_login_route_and_nothing_more()
    {
        var options = new VwGroupPortalOptions { PortalBaseUrl = "https://portal.test" };

        Assert.Equal("https://portal.test/login", options.RedirectUri);
    }

    [Fact]
    public void A_trailing_slash_on_the_portal_does_not_double_up()
    {
        var options = new VwGroupPortalOptions { PortalBaseUrl = "https://portal.test/" };

        Assert.Equal("https://portal.test/login", options.RedirectUri);
    }

    /// <summary>The regression, stated as the thing that must never come back.</summary>
    [Fact]
    public void No_locale_segment_is_spliced_in()
    {
        var uri = new VwGroupPortalOptions().RedirectUri;

        Assert.DoesNotContain("/de/", uri);
        Assert.EndsWith(".com/login", uri);
    }
}

/// <summary>
/// Reading the identity provider's own refusal, which it sends as an OAuth error object with no form
/// in it — the case that used to be reported as "a page with no form to post".
/// </summary>
public class VwGroupOAuthErrorTests
{
    [Fact]
    public void The_providers_own_words_are_carried_through()
    {
        var body = """{"error":"invalid_request","error_description":"Mismatching redirection URI"}""";

        Assert.Equal("invalid_request -- Mismatching redirection URI", VwGroupLoginForm.OAuthError(body));
    }

    [Fact]
    public void An_error_without_a_description_still_reads()
    {
        Assert.Equal("unauthorized_client", VwGroupLoginForm.OAuthError("""{"error":"unauthorized_client"}"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html><body>a real page</body></html>")]
    [InlineData("{ not json at all")]
    [InlineData("""{"something":"else"}""")]
    public void Anything_that_is_not_an_oauth_error_is_left_alone(string? body) =>
        Assert.Null(VwGroupLoginForm.OAuthError(body));
}

/// <summary>
/// The OIDC <c>state</c>, which is not a nonce however much the specification says it may be.
///
/// <para>The portal's <c>/services/callbacklogin</c> decodes it to learn which brand and locale the
/// returning code belongs to. Verified against the live portal: a random state lands back on
/// <c>/de/en/login.html</c> with no session and 401 on every API call; <c>de__en__VOLKSWAGEN_PASSENGER_CARS</c>
/// lands on <c>/de/en/user.html</c> and the vehicle list answers.</para>
/// </summary>
public class VwGroupStateTests
{
    [Fact]
    public void The_state_is_country_language_and_the_portals_brand_key()
    {
        var options = new VwGroupPortalOptions { Brand = "vw", Country = "de", Language = "en" };

        Assert.Equal("de__en__VOLKSWAGEN_PASSENGER_CARS", options.State);
    }

    [Theory]
    [InlineData("skoda", "SKODA")]
    [InlineData("audi", "AUDI")]
    [InlineData("cupra", "CUPRA")]
    [InlineData("vwn", "VOLKSWAGEN_COMMERCIAL_VEHICLES")]
    public void Each_brand_carries_the_portals_own_name(string brand, string expected)
    {
        Assert.Equal(expected, VwGroupBrands.PortalKey(brand));
        Assert.EndsWith(expected, new VwGroupPortalOptions { Brand = brand }.State);
    }

    /// <summary>VW and its commercial arm share a client id but are different brands in the state.</summary>
    [Fact]
    public void A_shared_client_id_does_not_mean_a_shared_brand_key()
    {
        Assert.Equal(VwGroupBrands.Resolve("vw"), VwGroupBrands.Resolve("vwn"));
        Assert.NotEqual(VwGroupBrands.PortalKey("vw"), VwGroupBrands.PortalKey("vwn"));
    }

    [Fact]
    public void The_state_is_never_random()
    {
        var options = new VwGroupPortalOptions { Brand = "vw" };

        Assert.Equal(options.State, options.State);
        Assert.DoesNotContain(" ", options.State);
    }
}

/// <summary>
/// The two "nothing to download" cases, which need opposite advice. Reported alike, an owner is told
/// to keep pressing a button that can never work.
/// </summary>
public class VwGroupNoDataTests
{
    [Fact]
    public void A_missing_data_request_is_not_worth_retrying()
    {
        var failure = new VwGroupPortalException(
            VwGroupFailure.NoDataRequest, "vehicle ...4196 has no continuous data request");

        Assert.False(failure.IsWorthRetrying);
    }

    [Fact]
    public void A_request_that_has_not_filled_yet_is_worth_retrying()
    {
        var failure = new VwGroupPortalException(
            VwGroupFailure.NoDataAvailable, "no dataset to download yet");

        Assert.True(failure.IsWorthRetrying);
    }
}
