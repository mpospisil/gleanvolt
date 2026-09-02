using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Gleanvolt.Core.Enums;
using Gleanvolt.Infrastructure.Vehicles.VwGroup;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// The portal on a clock (issue #140): one held session across many fetches, a re-sign-in that is
/// <b>timed</b>, and failures that become either a backoff or a full stop.
///
/// <para>No network. A fake portal answers the whole flow — the identity provider's two forms, the
/// vehicle list, the data request, the delivery list and a real ZIP built from the committed
/// fixtures — so the thing under test is the service's policy rather than a mocked client's.</para>
///
/// <para>The session's lifetime is the measurement #138 could not make, because nothing had ever
/// kept one. These tests pin the instrumentation: the log line that carries the answer is asserted,
/// not assumed.</para>
/// </summary>
public class VwGroupUpdateServiceTests
{
    private const string Portal = "https://portal.test";
    private const string Identity = "https://identity.test";
    private const string Vin = "WVWZZZE2ZMP012345";

    private static VwGroupPortalOptions Options(string password = "hunter2") => new()
    {
        PortalBaseUrl = Portal,
        IdentityBaseUrl = Identity,
        ClientId = "brand-client-id",
        Username = "owner@example.com",
        Password = password,
        Timeout = TimeSpan.FromSeconds(5),
    };

    private static string Fixture(string name) => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "VwGroup", name));

    /// <summary>A clock the test moves by hand, so a session's measured life is stated rather than waited for.</summary>
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>Keeps every message, so the measurement can be asserted rather than eyeballed.</summary>
    private sealed class CapturingLogger : ILogger<VwGroupUpdateService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    /// <summary>
    /// The portal, the identity provider and the delivery, in one handler. It has a session — the one
    /// thing a stub of this flow must model, since holding one is the whole subject.
    /// </summary>
    private sealed class FakePortal : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public int SignIns { get; private set; }

        public bool SignedIn { get; private set; }

        /// <summary>Answers everything with this instead, when a test is about a failure.</summary>
        public Func<HttpRequestMessage, HttpResponseMessage?>? Intercept { get; set; }

        /// <summary>What the delivery list holds. Empty is "the request exists and has not filled yet".</summary>
        public string ListJson { get; set; } =
            """[{"name":"dataset-2.zip","createdOn":"2026-09-01T10:00:00Z"}]""";

        /// <summary>What an expiry looks like from here: the cookie stops working, nothing announces it.</summary>
        public void Expire() => SignedIn = false;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add($"{request.Method} {uri.AbsolutePath}");

            if (Intercept?.Invoke(request) is { } forced)
            {
                forced.RequestMessage ??= request;
                return Task.FromResult(forced);
            }

            return Task.FromResult(uri.Host == new Uri(Identity).Host ? SignIn(request) : Serve(uri));
        }

        private HttpResponseMessage SignIn(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/login/identifier", StringComparison.Ordinal))
            {
                return Page(
                    Fixture("signin-authenticate.html"),
                    $"{Identity}/signin-service/v1/client/login/authenticate?relayState=x");
            }

            if (path.EndsWith("/login/authenticate", StringComparison.Ordinal))
            {
                SignedIn = true;
                SignIns++;

                // Landing back on the portal is what completing the flow looks like.
                return Page("<html><body>dashboard</body></html>", $"{Portal}/de/en/user.html");
            }

            return Page(Fixture("signin-identifier.html"), $"{Identity}/signin-service/v1/client?relayState=x");
        }

        private HttpResponseMessage Serve(Uri uri)
        {
            if (!SignedIn)
            {
                // The portal does not say "your session expired"; it hands back the login page.
                return Page("<html><body>sign in</body></html>", $"{Portal}/login");
            }

            var path = uri.AbsolutePath;

            if (path.Contains("/consent/me/vehicles", StringComparison.Ordinal))
            {
                return Json($$"""[{"vin":"{{Vin}}"}]""");
            }

            if (path.Contains("/datarequest/", StringComparison.Ordinal))
            {
                return Json("""{"Identifier":"request-1"}""");
            }

            if (path.EndsWith("/list", StringComparison.Ordinal))
            {
                return Json(ListJson);
            }

            if (path.EndsWith("/download", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(VwGroupFixtures.Bundle("id4-live-capture.json"))
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip") },
                    },
                };
            }

            return Json("{}", HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Page(string body, string landedAt) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/html"),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, landedAt),
            };

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static VwGroupUpdateService Service(
        FakePortal portal,
        TestClock? clock = null,
        CapturingLogger? logger = null,
        VwGroupPortalOptions? options = null) =>
        new("id4", options ?? Options(), clock, logger, portal);

    [Fact]
    public async Task A_reading_arrives_and_the_feed_reports_itself_healthy()
    {
        using var portal = new FakePortal();
        using var service = Service(portal);

        var state = await service.FetchAsync(CancellationToken.None);

        Assert.NotNull(state);
        Assert.Equal(57, state.SocPercent);
        Assert.Equal(VehicleSourceState.Ok, service.Health.State);
        Assert.Equal(VwGroupUpdateService.Interval, service.NextDelay);

        // Labelled with the manufacturer as well as the car: the dashboard shows this beside the age.
        Assert.StartsWith("vw-group", state.SourceId);
    }

    [Fact]
    public async Task The_session_is_held_across_fetches_rather_than_replayed()
    {
        // The change from the on-demand reader, and the reason this class exists: three reads, one
        // sign-in. Replaying a password at a real identity provider every quarter of an hour is how
        // accounts get locked.
        using var portal = new FakePortal();
        using var service = Service(portal);

        await service.FetchAsync(CancellationToken.None);
        await service.FetchAsync(CancellationToken.None);
        await service.FetchAsync(CancellationToken.None);

        Assert.Equal(1, portal.SignIns);
    }

    [Fact]
    public async Task An_expired_session_is_signed_in_again_and_how_long_it_lasted_is_recorded()
    {
        // The measurement #138 could not make. Nobody knows how long a portal session lives because
        // nothing had ever kept one; this is the instrumentation that answers it from the reference
        // install rather than from a guess.
        var clock = new TestClock(new DateTimeOffset(2026, 9, 2, 6, 0, 0, TimeSpan.Zero));
        var logger = new CapturingLogger();

        using var portal = new FakePortal();
        using var service = Service(portal, clock, logger);

        await service.FetchAsync(CancellationToken.None);

        clock.Advance(TimeSpan.FromHours(3));
        portal.Expire();

        var state = await service.FetchAsync(CancellationToken.None);

        Assert.NotNull(state);
        Assert.Equal(2, portal.SignIns);
        Assert.Contains(logger.Messages, message => message.Contains("lasted at least 03:00:00"));

        // A lower bound, and the clock says so: the session was alive at the last fetch and gone at
        // this one, so three hours is the least it can have been.
        Assert.NotNull(service.SessionAge);
        Assert.Equal(TimeSpan.Zero, service.SessionAge);
    }

    [Fact]
    public async Task A_screen_only_the_owner_can_answer_stops_the_feed_asking()
    {
        using var portal = new FakePortal
        {
            Intercept = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<html><body>Please give consent to continue</body></html>", Encoding.UTF8, "text/html"),
            },
        };

        using var service = Service(portal);

        Assert.Null(await service.FetchAsync(CancellationToken.None));
        Assert.Equal(VehicleSourceState.NeedsOwner, service.Health.State);
        Assert.Contains("browser", service.Health.Message);

        // Never again, and not merely later: an infinite delay is the contract's way of saying the
        // host must stop, and a second call must reach no network even if one is made anyway.
        Assert.Equal(Timeout.InfiniteTimeSpan, service.NextDelay);

        var asked = portal.Requests.Count;
        Assert.Null(await service.FetchAsync(CancellationToken.None));
        Assert.Equal(asked, portal.Requests.Count);
    }

    [Fact]
    public async Task A_portal_that_is_down_is_degraded_and_backs_off()
    {
        using var portal = new FakePortal
        {
            Intercept = request => request.RequestUri!.Host == new Uri(Portal).Host
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                }
                : null,
        };

        using var service = Service(portal);

        Assert.Null(await service.FetchAsync(CancellationToken.None));
        Assert.Equal(VehicleSourceState.Degraded, service.Health.State);
        Assert.Equal(VwGroupUpdateService.Interval, service.NextDelay);

        Assert.Null(await service.FetchAsync(CancellationToken.None));
        Assert.Equal(TimeSpan.FromMinutes(30), service.NextDelay);

        Assert.Null(await service.FetchAsync(CancellationToken.None));
        Assert.Equal(TimeSpan.FromHours(1), service.NextDelay);

        // Capped: an outage that lasts a day must not push the next attempt into next week.
        Assert.Null(await service.FetchAsync(CancellationToken.None));
        Assert.Equal(VwGroupUpdateService.MaxBackoff, service.NextDelay);
    }

    [Fact]
    public async Task Nothing_delivered_yet_keeps_the_natural_cadence()
    {
        // A newly created data request takes hours to fill, and that is not a fault. Backing off
        // would then take hours more to notice that it had.
        using var portal = new FakePortal { ListJson = "[]" };
        using var service = Service(portal);

        Assert.Null(await service.FetchAsync(CancellationToken.None));
        Assert.Equal(VehicleSourceState.Degraded, service.Health.State);
        Assert.Equal(VwGroupUpdateService.Interval, service.NextDelay);
    }

    [Fact]
    public async Task A_recovered_portal_is_healthy_again_and_back_on_its_own_interval()
    {
        var down = true;

        using var portal = new FakePortal();
        portal.Intercept = request => down && request.RequestUri!.Host == new Uri(Portal).Host
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            }
            : null;

        using var service = Service(portal);

        await service.FetchAsync(CancellationToken.None);
        await service.FetchAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromMinutes(30), service.NextDelay);

        down = false;

        Assert.NotNull(await service.FetchAsync(CancellationToken.None));
        Assert.Equal(VehicleSourceState.Ok, service.Health.State);
        Assert.Equal(VwGroupUpdateService.Interval, service.NextDelay);
    }

    [Fact]
    public async Task A_feed_switched_on_without_credentials_asks_nobody()
    {
        using var portal = new FakePortal();
        using var service = Service(portal, options: Options(password: string.Empty));

        Assert.Equal(VehicleSourceState.NeedsOwner, service.Health.State);
        Assert.Contains("a password", service.Health.Message);

        Assert.Null(await service.FetchAsync(CancellationToken.None));
        Assert.Empty(portal.Requests);
    }
}
