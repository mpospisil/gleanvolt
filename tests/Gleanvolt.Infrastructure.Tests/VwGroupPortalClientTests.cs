using System.Net;
using Gleanvolt.Infrastructure.Vehicles.VwGroup;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// The transport half (issue #139): that the consent screen and an expired session fail
/// <b>distinguishably</b>, and that neither turns into a loop.
///
/// <para>No network: a stub <see cref="HttpMessageHandler"/> answers, so these run in milliseconds
/// and say the same thing on a machine with no internet. What they cannot say is how VW's identity
/// provider really behaves — that is what the Phase 0 spike and the console harness are for.</para>
/// </summary>
public class VwGroupPortalClientTests
{
    private const string Portal = "https://portal.test";
    private const string Identity = "https://identity.test";

    private static VwGroupPortalOptions Options() => new()
    {
        PortalBaseUrl = Portal,
        IdentityBaseUrl = Identity,
        ClientId = "brand-client-id",
        Username = "owner@example.com",
        Password = "hunter2",
        Timeout = TimeSpan.FromSeconds(5),
    };

    /// <summary>Answers whatever the test says, and remembers what it was asked.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");

            var response = respond(request);

            // Only when the test did not set one: a response whose RequestMessage points somewhere
            // else is how a redirect is expressed here, and that is the whole point of several of
            // these cases.
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private static HttpResponseMessage Html(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "text/html") };

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task AConsentScreenFailsAsSomethingOnlyTheOwnerCanDo()
    {
        // #137's unattended design degrades here rather than looping. This is what Phase 2's
        // "sign-in required" -- a state distinct from "stale" -- will be built on.
        var handler = new StubHandler(_ => Html(
            "<html><body>Please give consent to continue</body></html>"));

        using var http = new HttpClient(handler);
        var signIn = new VwGroupSignIn(http, Options());

        var error = await Assert.ThrowsAsync<VwGroupPortalException>(() => signIn.SignInAsync());

        Assert.Equal(VwGroupFailure.OwnerActionRequired, error.Failure);
        Assert.False(error.IsWorthRetrying);
        Assert.Contains("consent screen", error.Message);
        Assert.Contains(Portal, error.Message);

        // And it stopped at once: posting a password into a consent screen tells nobody anything.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ACaptchaIsNamedSoTheMessageIsActionable()
    {
        var handler = new StubHandler(_ => Html("<html><body><div class=\"g-recaptcha\"></div></body></html>"));

        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<VwGroupPortalException>(
            () => new VwGroupSignIn(http, Options()).SignInAsync());

        Assert.Contains("CAPTCHA", error.Message);
    }

    [Fact]
    public async Task WalksTheIdentifierAndPasswordPagesAndStopsOnThePortal()
    {
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.Contains("authorize", StringComparison.Ordinal))
            {
                return Html("""
                    <form action="https://identity.test/login/identifier" method="post">
                      <input type="hidden" name="hmac" value="h"><input type="email" name="identifier">
                    </form>
                    """);
            }

            if (path.Contains("identifier", StringComparison.Ordinal))
            {
                return Html("""
                    <form action="https://identity.test/login/authenticate" method="post">
                      <input type="hidden" name="_csrf" value="c"><input type="password" name="password">
                    </form>
                    """);
            }

            // The redirect_uri is the portal's own /login; landing anywhere else on the portal is
            // what "signed in" looks like.
            var landed = Html("<html><body>dashboard</body></html>");
            landed.RequestMessage = new HttpRequestMessage(HttpMethod.Get, $"{Portal}/de/en/dashboard");
            return landed;
        });

        using var http = new HttpClient(handler);
        await new VwGroupSignIn(http, Options()).SignInAsync();

        Assert.Equal(
            ["GET /oidc/v1/authorize", "POST /login/identifier", "POST /login/authenticate"],
            handler.Requests);
    }

    [Fact]
    public async Task NothingIsAttemptedWhenNothingIsConfigured()
    {
        using var http = new HttpClient(new StubHandler(_ => Json("{}")));

        var error = await Assert.ThrowsAsync<VwGroupPortalException>(
            () => new VwGroupSignIn(http, new VwGroupPortalOptions()).SignInAsync());

        Assert.Equal(VwGroupFailure.NotConfigured, error.Failure);
        Assert.Contains("brand client id", error.Message);
    }

    [Fact]
    public async Task AnExpiredSessionSignsInAgainExactlyOnce()
    {
        // The 401 is expected and recoverable; the loop it must not become is what the "once" pins.
        var vehiclesCalls = 0;

        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/vehicles", StringComparison.Ordinal))
            {
                return ++vehiclesCalls == 1
                    ? Json("{\"error\":\"unauthorized\"}", HttpStatusCode.Unauthorized)
                    : Json("[{\"vin\":\"WVWZZZE2ZMP012345\",\"requestId\":\"req-1\"}]");
            }

            var landed = Html("<html><body>dashboard</body></html>");
            landed.RequestMessage = new HttpRequestMessage(HttpMethod.Get, $"{Portal}/de/en/dashboard");
            return landed;
        });

        using var http = new HttpClient(handler);
        var vehicles = await new VwGroupPortalClient(http, Options()).GetVehiclesAsync();

        Assert.Equal("WVWZZZE2ZMP012345", Assert.Single(vehicles).Vin);
        Assert.Equal(2, vehiclesCalls);
    }

    [Fact]
    public async Task ASecondBounceIsReportedRatherThanRetriedForever()
    {
        var handler = new StubHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/vehicles", StringComparison.Ordinal)
                ? Json("{}", HttpStatusCode.Unauthorized)
                : PortalPage());

        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<VwGroupPortalException>(
            () => new VwGroupPortalClient(http, Options()).GetVehiclesAsync());

        Assert.Equal(VwGroupFailure.SessionExpired, error.Failure);
        Assert.True(error.IsWorthRetrying);
    }

    [Fact]
    public async Task HtmlWhereJsonWasExpectedIsAnExpiredSessionToo()
    {
        // One of the three shapes the Phase 0 spike was built to tell apart, and the least obvious:
        // the status is 200 and the body is a login page. Proved by the client signing in and trying
        // again, which is what it does for a 401.
        var vehiclesCalls = 0;

        var handler = new StubHandler(request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/vehicles", StringComparison.Ordinal))
            {
                return PortalPage();
            }

            return ++vehiclesCalls == 1
                ? Html("<html><body>Sign in</body></html>")
                : Json("[{\"vin\":\"WVWZZZE2ZMP012345\",\"requestId\":\"req-1\"}]");
        });

        using var http = new HttpClient(handler);

        Assert.Single(await new VwGroupPortalClient(http, Options()).GetVehiclesAsync());
        Assert.Equal(2, vehiclesCalls);
    }

    [Fact]
    public async Task ASignInThatCannotCompleteIsReportedAsItselfRatherThanAsAStaleSession()
    {
        // The bounce was handled; it is the sign-in that failed, and saying so is the difference
        // between "wait and try again" and "something about this flow has changed".
        var handler = new StubHandler(_ => Html("<html><body>nothing to post here</body></html>"));

        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<VwGroupPortalException>(
            () => new VwGroupPortalClient(http, Options()).GetVehiclesAsync());

        Assert.Equal(VwGroupFailure.SignInRejected, error.Failure);
        Assert.False(error.IsWorthRetrying);
    }

    [Fact]
    public async Task AServerErrorIsTransientAndSaysSo()
    {
        // Whatever Phase 0 finds about how sticky these are becomes Phase 2's backoff; what Phase 1
        // owes is a failure a caller can tell apart from a dead session.
        var handler = new StubHandler(_ => Json("{}", HttpStatusCode.BadGateway));

        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<VwGroupPortalException>(
            () => new VwGroupPortalClient(http, Options()).GetVehiclesAsync());

        Assert.Equal(VwGroupFailure.Transient, error.Failure);
        Assert.True(error.IsWorthRetrying);
    }

    [Fact]
    public async Task AnAccountWithSeveralCarsAndNoConfiguredVinRefusesToPick()
    {
        var handler = new StubHandler(_ => Json("""
            [{"vin":"WVWZZZE2ZMP012345","requestId":"r1"},{"vin":"WVWZZZE2ZMP067890","requestId":"r2"}]
            """));

        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<VwGroupPortalException>(
            () => new VwGroupPortalClient(http, Options()).GetVehicleAsync());

        Assert.Equal(VwGroupFailure.VehicleNotFound, error.Failure);

        // Masked, because a VIN identifies a car and, through it, its owner.
        Assert.Contains("…2345", error.Message);
        Assert.DoesNotContain("WVWZZZE2ZMP012345", error.Message);
    }

    [Fact]
    public async Task ACarWithNoDataRequestSaysWhoHasToCreateOne()
    {
        // Nobody can create a continuous data request from here -- it is a browser step in the portal
        // -- so this must not read like a bug.
        var handler = new StubHandler(_ => Json("[{\"vin\":\"WVWZZZE2ZMP012345\"}]"));

        using var http = new HttpClient(handler);
        var client = new VwGroupPortalClient(http, Options());

        var error = await Assert.ThrowsAsync<VwGroupPortalException>(async () =>
            await client.GetNewestDatasetUrlAsync(await client.GetVehicleAsync()));

        Assert.Equal(VwGroupFailure.NoDataAvailable, error.Failure);
        Assert.Contains("by hand", error.Message);
    }

    [Fact]
    public void TheAuthorizeUrlAsksForWhatThePortalsOwnClientAsksFor()
    {
        using var http = new HttpClient(new StubHandler(_ => Json("{}")));
        var url = new VwGroupSignIn(http, Options()).AuthorizeUrl();

        Assert.StartsWith($"{Identity}/oidc/v1/authorize?", url);
        Assert.Contains("client_id=brand-client-id", url);
        Assert.Contains("scope=openid%20cars%20profile", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains(Uri.EscapeDataString($"{Portal}/de/en/login"), url);

        // No PKCE in this flow: adding a challenge changes what the identity provider expects back.
        Assert.DoesNotContain("code_challenge", url);
    }

    private static HttpResponseMessage PortalPage()
    {
        var page = Html("<html><body>dashboard</body></html>");
        page.RequestMessage = new HttpRequestMessage(HttpMethod.Get, $"{Portal}/de/en/dashboard");
        return page;
    }
}
