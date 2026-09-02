using Gleanvolt.Infrastructure.Vehicles.VwGroup;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// The judgement half of signing in (issue #139), which is pure and therefore the half worth testing:
/// what the identity provider wants posted back, and whether the page in front of us is one a program
/// may answer at all.
/// </summary>
public class VwGroupLoginFormTests
{
    private const string IdentifierPage = """
        <html><body>
          <form action="/signin-service/v1/xxx/login/identifier" method="post">
            <input type="hidden" name="_csrf" value="csrf-1">
            <input type="hidden" name="relayState" value="relay-1">
            <input type="hidden" name="hmac" value="hmac-1">
            <input type="email" name="identifier" value="">
          </form>
        </body></html>
        """;

    private const string PasswordPage = """
        <html><body>
          <form action="/signin-service/v1/xxx/login/authenticate" method="POST">
            <input type="hidden" name="_csrf" value="csrf-2">
            <input type="password" name="password">
          </form>
        </body></html>
        """;

    [Fact]
    public void ReadsTheFormItHasToPostBackTo()
    {
        var form = VwGroupLoginForm.Parse(IdentifierPage);

        Assert.True(form.IsPostable);
        Assert.Equal("/signin-service/v1/xxx/login/identifier", form.Action);
        Assert.Equal("post", form.Method);
    }

    [Fact]
    public void CarriesEveryHiddenFieldVerbatim()
    {
        // hmac, _csrf and relayState today; naming that list in code would break silently the day the
        // identity provider adds a fourth, so everything it renders goes back as it came.
        var form = VwGroupLoginForm.Parse(IdentifierPage);

        Assert.Equal("csrf-1", form.Fields["_csrf"]);
        Assert.Equal("relay-1", form.Fields["relayState"]);
        Assert.Equal("hmac-1", form.Fields["hmac"]);
    }

    [Fact]
    public void FallsBackToTheTemplateModelForWhatTheMarkupLeftOut()
    {
        // The identity provider puts the same state in both places and has changed which one is
        // authoritative before.
        const string page = """
            <html><head><script>
              window.templateModel = {"hmac":"hmac-from-model","relayState":"relay-from-model","nested":{"x":1}},
            </script></head>
            <body><form action="/login" method="post"><input type="password" name="password"></form></body></html>
            """;

        var form = VwGroupLoginForm.Parse(page);

        Assert.Equal("hmac-from-model", form.Fields["hmac"]);
        Assert.Equal("relay-from-model", form.Fields["relayState"]);

        // Only strings: everything replayed goes into a form post, where a nested object has no
        // representation.
        Assert.DoesNotContain("nested", form.Fields.Keys);
    }

    [Fact]
    public void ARenderedHiddenInputBeatsTheTemplateModel()
    {
        // It is the value the browser would have posted.
        const string page = """
            <html><head><script>templateModel = {"hmac":"from-model"};</script></head>
            <body><form action="/login" method="post">
              <input type="hidden" name="hmac" value="from-markup">
              <input type="password" name="password">
            </form></body></html>
            """;

        Assert.Equal("from-markup", VwGroupLoginForm.Parse(page).Fields["hmac"]);
    }

    [Fact]
    public void FindsTheIdentifierAndPasswordFieldsByNameOrByType()
    {
        Assert.Equal("identifier", VwGroupLoginForm.Parse(IdentifierPage).IdentifierField);
        Assert.Null(VwGroupLoginForm.Parse(IdentifierPage).PasswordField);
        Assert.Equal("password", VwGroupLoginForm.Parse(PasswordPage).PasswordField);
    }

    [Fact]
    public void SurvivesARenameBecauseTheInputTypeStillSaysWhatItIs()
    {
        const string page = """
            <html><body><form action="/login" method="post">
              <input type="email" name="vwid_email_2026">
            </form></body></html>
            """;

        Assert.Equal("vwid_email_2026", VwGroupLoginForm.Parse(page).IdentifierField);
    }

    [Theory]
    [InlineData("<p>Please complete the captcha</p>", "a CAPTCHA")]
    [InlineData("<p>Enter the verification code we sent you</p>", "a one-time code")]
    [InlineData("<p>Open your authenticator app</p>", "two-factor authentication")]
    [InlineData("<p>Please review and give consent</p>", "a consent screen")]
    public void NamesTheHumanStepWhenOneAppears(string body, string expected)
    {
        // Distinguishable and actionable is the requirement: an OTP ends the unattended design, a
        // consent screen is a one-off in a browser, and a message that says which is the difference
        // between an actionable failure and a shrug.
        Assert.Equal(expected, VwGroupLoginForm.OwnerActionReason($"<html><body>{body}</body></html>"));
    }

    [Fact]
    public void AnOrdinaryLoginPageIsNotAHumanStep()
    {
        Assert.Null(VwGroupLoginForm.OwnerActionReason(IdentifierPage));
        Assert.Null(VwGroupLoginForm.OwnerActionReason(PasswordPage));
    }

    [Theory]
    [InlineData("otp")]
    [InlineData("emailOtp")]
    [InlineData("email_otp")]
    [InlineData("otpCode")]
    [InlineData("smsCode")]
    [InlineData("securityCode")]
    [InlineData("verification-code")]
    public void AFieldForACodeOutOfSomebodysInboxIsRecognisedWhateverItIsCalled(string name)
    {
        var page = $"<html><body><form action=\"/login\" method=\"post\">"
            + $"<input type=\"text\" name=\"{name}\"></form></body></html>";

        Assert.Equal(name, VwGroupLoginForm.Parse(page).OneTimeCodeField);
    }

    [Fact]
    public void AnOtpPageCarryingAHiddenEmailIsStillNotAPageWeCanSignInOn()
    {
        // The regression this exists for. Every rendered input counts towards CanSignIn, hidden ones
        // included -- so an OTP page that re-renders the address in a hidden field used to read as an
        // ordinary sign-in page, and the identifier was posted into a form that wanted six digits. It
        // failed five pages later as a refused password: stopped, correctly, and for the wrong stated
        // reason, which sends the owner to their password instead of to their mailbox.
        const string page = """
            <html><body><form action="/login/authenticate" method="post">
              <input type="hidden" name="email" value="owner@example.com">
              <input type="hidden" name="_csrf" value="token">
              <input type="text" name="emailOtp">
            </form></body></html>
            """;

        var form = VwGroupLoginForm.Parse(page);

        Assert.Equal("emailOtp", form.OneTimeCodeField);
        Assert.False(form.CanSignIn);
    }

    [Fact]
    public void TheOrdinaryPagesAskForNoCode()
    {
        // The other half: a false positive here would abort a sign-in that was working and report
        // "sign-in required" when nothing is wrong at all.
        Assert.Null(VwGroupLoginForm.Parse(IdentifierPage).OneTimeCodeField);
        Assert.Null(VwGroupLoginForm.Parse(PasswordPage).OneTimeCodeField);
        Assert.True(VwGroupLoginForm.Parse(IdentifierPage).CanSignIn);
        Assert.True(VwGroupLoginForm.Parse(PasswordPage).CanSignIn);
    }

    [Fact]
    public void OAuthsOwnCodeParameterIsNotMistakenForOne()
    {
        // "code" is deliberately not in the list: it is OAuth's own parameter name, and a hidden one
        // on a sign-in page must not stop a flow that is working.
        const string page = """
            <html><body><form action="/login" method="post">
              <input type="hidden" name="code" value="abc123">
              <input type="email" name="email">
            </form></body></html>
            """;

        var form = VwGroupLoginForm.Parse(page);

        Assert.Null(form.OneTimeCodeField);
        Assert.True(form.CanSignIn);
    }

    [Fact]
    public void APageWithNoFormIsNotPostable()
    {
        var form = VwGroupLoginForm.Parse("<html><body>Something went wrong</body></html>");

        Assert.False(form.IsPostable);
        Assert.Empty(form.Fields);
    }
}
