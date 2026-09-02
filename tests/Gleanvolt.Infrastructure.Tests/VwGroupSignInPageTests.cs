using Gleanvolt.Infrastructure.Vehicles.VwGroup;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// The identity provider's real first page, captured from `identity.vwgroup.io` and sanitised — the
/// only fixture here that is not synthetic.
///
/// <para>It exists because of the bug it pins. The EU Data Act client is registered as
/// <b>"GIS Consent Portal"</b>, so its perfectly ordinary email form says "consent" six times: in the
/// client application's name, in the <c>templateModel</c> blob, and in the visible subtitle
/// <i>"Welcome to Consent Portal - Volkswagen Group Info Services AG"</i>. A consent-screen detector
/// that searched the page for the word aborted <b>every</b> sign-in before the first field was
/// filled, and reported it as something only the owner could fix in a browser.</para>
///
/// <para>The lesson, and the rule the fix encodes: <b>a page you can sign in on is a sign-in
/// page</b>, whatever words are printed on it.</para>
/// </summary>
public class VwGroupSignInPageTests
{
    private static string Page() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "VwGroup", "signin-identifier.html"));

    [Fact]
    public void The_real_email_form_is_parsed()
    {
        var form = VwGroupLoginForm.Parse(Page());

        Assert.True(form.IsPostable);
        Assert.Contains("/login/identifier", form.Action);
        Assert.Equal("post", form.Method);
    }

    [Fact]
    public void It_carries_the_three_hidden_fields_the_provider_wants_back()
    {
        var form = VwGroupLoginForm.Parse(Page());

        Assert.Contains("_csrf", form.Fields.Keys);
        Assert.Contains("relayState", form.Fields.Keys);
        Assert.Contains("hmac", form.Fields.Keys);
    }

    [Fact]
    public void The_email_field_is_found_so_the_flow_can_move_on()
    {
        var form = VwGroupLoginForm.Parse(Page());

        Assert.Equal("email", form.IdentifierField);
        Assert.True(form.CanSignIn);

        // Emphatically null, and the guard that makes it so matters: this page's templateModel also
        // carries an emailPasswordForm, and the caller fills a password wherever it finds one. Taking
        // the model's names here would post the password to login/identifier -- wrong endpoint, and a
        // step too early. Markup wins where markup exists.
        Assert.Null(form.PasswordField);
    }

    /// <summary>
    /// The regression itself. The page still says "consent" — it cannot not say it, the application
    /// is called that — so the detector may well still fire on the text. What must never happen again
    /// is that firing while there is a field to fill.
    /// </summary>
    [Fact]
    public void A_page_with_somewhere_to_sign_in_is_never_an_owner_action_screen()
    {
        var page = Page();

        Assert.Contains("Consent Portal", page);
        Assert.True(VwGroupLoginForm.Parse(page).CanSignIn);
    }

    /// <summary>A real consent screen has nothing to fill, which is how the two stay apart.</summary>
    [Fact]
    public void A_page_with_nothing_to_fill_still_reports_an_owner_action()
    {
        const string consent =
            "<html><body><h1>Consent</h1><form action='/consent'><button>Agree</button></form></body></html>";

        Assert.False(VwGroupLoginForm.Parse(consent).CanSignIn);
        Assert.Equal("a consent screen", VwGroupLoginForm.OwnerActionReason(consent));
    }

    /// <summary>Machine data is not prose: a client application's own name must not read as a screen.</summary>
    [Fact]
    public void Words_inside_a_script_block_are_not_evidence_of_anything()
    {
        const string html =
            "<html><head><script>var templateModel = {\"clientAppName\":\"GIS Consent Portal\"};</script>"
            + "</head><body><p>Nothing to see</p></body></html>";

        Assert.Null(VwGroupLoginForm.OwnerActionReason(html));
    }
}

/// <summary>
/// The identity provider's <b>password</b> page, captured and sanitised. It renders no form and no
/// inputs at all — <c>useClientRendering: true</c>, zero <c>&lt;form&gt;</c> tags, zero
/// <c>&lt;input&gt;</c> tags — and puts everything the browser needs in its <c>templateModel</c>
/// instead.
///
/// <para>Three separate bugs met on this one page: a lazy <c>{.*?}</c> regex that truncated the model
/// to unparseable JSON, an <c>IsPostable</c> that required an HTML form's action, and a relative
/// <c>postAction</c> resolved against the page instead of the sign-in service's client root. The flow
/// stopped one step short of signing in and called it a refused password.</para>
/// </summary>
public class VwGroupPasswordPageTests
{
    private const string ClientId = "9b58543e-1c15-4193-91d5-8a14145bebb0@apps_vw-dilab_com";

    private static string Page() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "VwGroup", "signin-authenticate.html"));

    [Fact]
    public void It_really_does_ship_no_form_and_no_inputs()
    {
        var page = Page();

        Assert.DoesNotContain("<form", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<input", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_client_rendered_page_is_still_postable()
    {
        var form = VwGroupLoginForm.Parse(Page());

        Assert.True(form.IsPostable);
        Assert.Null(form.Action);
        Assert.Equal("login/authenticate", form.PostAction);
    }

    [Fact]
    public void The_password_field_is_found_from_the_template_model()
    {
        var form = VwGroupLoginForm.Parse(Page());

        Assert.Equal("password", form.PasswordField);
        Assert.True(form.CanSignIn);
    }

    /// <summary>
    /// The token that is in neither the markup nor the model. Posting without it is answered 400
    /// with an error page carrying no form — which read as "no form to post" and looked, wrongly,
    /// like a refused password.
    /// </summary>
    [Fact]
    public void The_csrf_token_is_taken_from_the_javascript_variable()
    {
        var form = VwGroupLoginForm.Parse(Page());

        Assert.Equal("CSRF-JS-TOKEN-REDACTED", form.Fields["_csrf"]);
    }

    [Fact]
    public void The_nested_model_is_read_whole_rather_than_truncated_at_the_first_brace()
    {
        var form = VwGroupLoginForm.Parse(Page());

        // hmac and relayState sit after the nested clientLegalEntityModel, so a lazy regex never
        // reached them.
        Assert.Equal("HMAC-REDACTED", form.Fields["hmac"]);
        Assert.Equal("RELAY-STATE-REDACTED", form.Fields["relayState"]);
    }

    /// <summary>The model is a view model, not a form: its furniture must not reach the post body.</summary>
    [Fact]
    public void View_model_furniture_is_not_posted_as_a_field()
    {
        var fields = VwGroupLoginForm.Parse(Page()).Fields.Keys;

        Assert.DoesNotContain("template", fields);
        Assert.DoesNotContain("titleKey", fields);
        Assert.DoesNotContain("postAction", fields);
        Assert.DoesNotContain("identifierUrl", fields);
    }

    /// <summary>
    /// The relative-resolution trap: <c>login/authenticate</c> against a page already at
    /// <c>.../login/authenticate</c> gives <c>.../login/login/authenticate</c>.
    /// </summary>
    [Fact]
    public void PostAction_resolves_against_the_sign_in_service_root_not_the_page()
    {
        using var http = new HttpClient();
        var signIn = new VwGroupSignIn(http, new VwGroupPortalOptions
        {
            IdentityBaseUrl = "https://identity.vwgroup.io",
            ClientId = ClientId,
            Username = "owner@example.com",
            Password = "hunter2",
        });

        var pageUrl = $"https://identity.vwgroup.io/signin-service/v1/{ClientId}/login/authenticate?relayState=x";
        var target = signIn.TargetFor(VwGroupLoginForm.Parse(Page()), pageUrl);

        Assert.Equal(
            $"https://identity.vwgroup.io/signin-service/v1/{ClientId}/login/authenticate", target);
        Assert.DoesNotContain("login/login", target);
    }
}

/// <summary>
/// Both real pages, replayed in order through the sign-in itself. The unit tests above prove each
/// page parses; this proves the <b>flow</b> — that the email goes to <c>login/identifier</c> and the
/// password to <c>login/authenticate</c>, each to the right URL, neither a step early.
/// </summary>
public class VwGroupSignInFlowTests
{
    private const string ClientId = "9b58543e-1c15-4193-91d5-8a14145bebb0@apps_vw-dilab_com";
    private const string Identity = "https://identity.vwgroup.io";
    private const string Portal = "https://eu-data-act.drivesomethinggreater.com";

    private static string Fixture(string name) => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "VwGroup", name));

    /// <summary>Replays the captured pages and records what was posted where.</summary>
    private sealed class Replay : HttpMessageHandler
    {
        public List<(string Url, string Body)> Posts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            if (request.Method == HttpMethod.Post)
            {
                Posts.Add((url, await request.Content!.ReadAsStringAsync(cancellationToken)));

                // The identifier post is answered with the password page; the password post lands
                // back on the portal, which is what "signed in" looks like.
                return Page(Posts.Count == 1
                    ? ($"{Identity}/signin-service/v1/{ClientId}/login/authenticate?relayState=x",
                       Fixture("signin-authenticate.html"))
                    : ($"{Portal}/", "<html>signed in</html>"));
            }

            return Page(($"{Identity}/signin-service/v1/signin/{ClientId}?relayState=x",
                         Fixture("signin-identifier.html")));
        }

        private static HttpResponseMessage Page((string Url, string Body) page) =>
            new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(page.Body),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, page.Url),
            };
    }

    [Fact]
    public async Task The_email_then_the_password_each_reach_their_own_endpoint()
    {
        var replay = new Replay();
        using var http = new HttpClient(replay);

        await new VwGroupSignIn(http, new VwGroupPortalOptions
        {
            IdentityBaseUrl = Identity,
            PortalBaseUrl = Portal,
            ClientId = ClientId,
            Username = "owner@example.com",
            Password = "hunter2",
        }).SignInAsync();

        Assert.Equal(2, replay.Posts.Count);

        var (identifierUrl, identifierBody) = replay.Posts[0];
        Assert.EndsWith($"/signin-service/v1/{ClientId}/login/identifier", identifierUrl);
        Assert.Contains("email=owner%40example.com", identifierBody);
        // The credential that must NOT be here: one step early, and at the wrong endpoint.
        Assert.DoesNotContain("password=hunter2", identifierBody);

        var (passwordUrl, passwordBody) = replay.Posts[1];
        Assert.Equal($"{Identity}/signin-service/v1/{ClientId}/login/authenticate", passwordUrl);
        Assert.DoesNotContain("login/login", passwordUrl);
        Assert.Contains("password=hunter2", passwordBody);
        Assert.Contains("hmac=HMAC-REDACTED", passwordBody);
        Assert.Contains("relayState=RELAY-STATE-REDACTED", passwordBody);

        // The one whose absence produced a 400 and a generalErrorBranded page with no form on it.
        // The identifier page renders _csrf as a hidden input; this page renders nothing at all and
        // ships the token as a JavaScript variable instead.
        Assert.Contains("_csrf=CSRF-JS-TOKEN-REDACTED", passwordBody);
    }
}
