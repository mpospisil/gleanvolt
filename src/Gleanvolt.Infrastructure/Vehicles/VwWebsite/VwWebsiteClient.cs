using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Infrastructure.Vehicles.VwWebsite;

/// <summary>
/// volkswagen.de's authproxy — the <b>live</b> view of the car (issue #170).
///
/// <para>Why it exists beside the EU Data Act portal: one reading, 2026-09-05, same car, within a
/// minute of each other. This endpoint reported a state of charge the car captured at 08:39 that
/// morning; the portal was still serving one captured at 21:16 the evening before. The portal's
/// publication pipeline runs 1h48m to 7h16m behind the car, so a charging session ends before its
/// first in-charge reading appears there.</para>
///
/// <para><b>One cookie jar for the whole chain, always.</b> The Auth0 flow is a sequence of redirects
/// whose session lives in cookies set part-way through. Splitting the credential POST from following
/// its redirect — two requests, two jars — loses it outright: the identity provider answers
/// <i>"we couldn't find your session"</i> and the one-time code is burnt. Verified the hard way.</para>
/// </summary>
public sealed class VwWebsiteClient : IDisposable
{
    private const string UserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 "
        + "(KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36";

    private readonly VwWebsiteOptions _options;
    private readonly VwWebsiteSessionStore _session;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private HttpClientHandler _handler;
    private HttpClient _http;
    private CookieContainer _jar;

    private string? _pendingCodeUrl;
    private string? _pendingCodeState;
    private string? _gdc;
    private DateTimeOffset _lastLoginAttempt = DateTimeOffset.MinValue;

    public VwWebsiteClient(
        VwWebsiteOptions options,
        VwWebsiteSessionStore session,
        ILogger<VwWebsiteClient>? logger = null,
        TimeProvider? time = null)
    {
        _options = options;
        _session = session;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        _jar = session.Load();
        (_handler, _http) = Build(_jar, options.Timeout);
    }

    /// <summary>Whether a one-time code is what the client is waiting for.</summary>
    public bool AwaitingCode => _pendingCodeUrl is not null;

    private static (HttpClientHandler, HttpClient) Build(CookieContainer jar, TimeSpan timeout)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = jar,
            UseCookies = true,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 30,
        };

        var http = new HttpClient(handler) { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return (handler, http);
    }

    private string LoginUrl =>
        $"{_options.PortalBaseUrl}/app/authproxy/login?fag=vw-de,vwag-weconnect"
        + "&scope-vw-de=profile,address,phone,carConfigurations,dealers,cars,vin,profession"
        + "&scope-vwag-weconnect=openid,mbb&prompt-vwag-weconnect=none"
        + $"&redirectUrl={_options.PortalBaseUrl}/de/besitzer-und-nutzer/myvolkswagen.html"
        + "&sessionTimeout=1800";

    private string Referer => $"{_options.PortalBaseUrl}/de/besitzer-und-nutzer/myvolkswagen.html";

    /// <summary>
    /// Establishes a session, reusing a saved one when it still works.
    ///
    /// <para>Three rungs, cheapest first: the saved jar might already be signed in; if not, the
    /// credentials are replayed and the remembered-browser grant usually carries it; and only then is
    /// a code wanted. Returns which rung it stopped on.</para>
    /// </summary>
    public async Task<VwWebsiteLoginStep> SignInAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var landing = await FollowAsync(LoginUrl, cancellationToken).ConfigureAwait(false);
            var page = VwWebsiteLoginPage.Read(landing.Url, landing.Body);

            if (page.Step == VwWebsiteLoginStep.SignedIn)
            {
                _logger.LogInformation("The saved volkswagen.de session is still signed in.");
                _session.Save(_jar);
                return VwWebsiteLoginStep.SignedIn;
            }

            if (page.Step != VwWebsiteLoginStep.CredentialsRequired)
            {
                return page.Step;
            }

            var since = _time.GetUtcNow() - _lastLoginAttempt;

            if (since < _options.MinimumTimeBetweenLogins)
            {
                // Never hammer a real identity provider: that is how an account gets locked, and the
                // failure being retried is rarely one a retry fixes.
                _logger.LogWarning(
                    "Not replaying volkswagen.de credentials again after {Since:g}; the floor is {Floor:g}.",
                    since, _options.MinimumTimeBetweenLogins);

                return VwWebsiteLoginStep.Failed;
            }

            _lastLoginAttempt = _time.GetUtcNow();

            var afterCredentials = await PostAsync(
                $"{_options.IdentityBaseUrl}/u/login?state={Uri.EscapeDataString(page.State ?? string.Empty)}",
                new Dictionary<string, string>
                {
                    ["state"] = page.State ?? string.Empty,
                    ["username"] = _options.Username,
                    ["password"] = _options.Password,
                },
                landing.Url,
                cancellationToken).ConfigureAwait(false);

            var next = VwWebsiteLoginPage.Read(afterCredentials.Url, afterCredentials.Body);

            if (next.Step == VwWebsiteLoginStep.OneTimeCodeRequired)
            {
                _pendingCodeUrl = afterCredentials.Url;
                _pendingCodeState = next.State;
                _session.Save(_jar);

                _logger.LogInformation(
                    "volkswagen.de wants a one-time code; it has been emailed to the account owner.");
            }
            else if (next.Step == VwWebsiteLoginStep.SignedIn)
            {
                _session.Save(_jar);
                _logger.LogInformation("Signed in to volkswagen.de without a code.");
            }

            return next.Step;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Answers the code challenge, asking to be remembered.
    ///
    /// <para><c>rememberBrowser=true</c> is the whole point: the grant it returns is what stops the
    /// next restart asking again. A rejected code leaves the challenge open, so the owner can read the
    /// email more carefully rather than starting over and being sent a third one.</para>
    /// </summary>
    public async Task<VwWebsiteLoginStep> SubmitCodeAsync(
        string code, CancellationToken cancellationToken = default)
    {
        if (_pendingCodeUrl is null)
        {
            return VwWebsiteLoginStep.Failed;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var result = await PostAsync(
                _pendingCodeUrl,
                new Dictionary<string, string>
                {
                    ["state"] = _pendingCodeState ?? string.Empty,
                    ["code"] = code.Trim(),
                    ["rememberBrowser"] = "true",
                },
                _pendingCodeUrl,
                cancellationToken).ConfigureAwait(false);

            if (VwWebsiteLoginPage.IsRejectedCode(result.Body))
            {
                // The challenge survives a wrong code; only the state rotates.
                _pendingCodeState = VwWebsiteLoginPage.Read(result.Url, result.Body).State ?? _pendingCodeState;
                return VwWebsiteLoginStep.OneTimeCodeRequired;
            }

            var page = VwWebsiteLoginPage.Read(result.Url, result.Body);

            if (page.Step == VwWebsiteLoginStep.SignedIn)
            {
                _pendingCodeUrl = null;
                _pendingCodeState = null;

                if (!_session.Save(_jar))
                {
                    _logger.LogWarning(
                        "Signed in to volkswagen.de but could not save the session to {Path}; the next "
                        + "restart will need another code.", _session.Path);
                }
            }

            return page.Step;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The car, live. Signs in first when the saved session has lapsed.</summary>
    public async Task<VehicleState?> GetVehicleStateAsync(CancellationToken cancellationToken = default)
    {
        var body = await GetChargingStatusAsync(cancellationToken).ConfigureAwait(false);

        if (body is null)
        {
            return null;
        }

        var state = VwWebsiteChargingStatus.Parse(body, "vw-website", out var error);

        if (state is null)
        {
            _logger.LogWarning("volkswagen.de answered with something unusable: {Reason}", error);
        }

        return state;
    }

    /// <summary>The raw payload, for a caller that wants more of it than <see cref="VehicleState"/> holds.</summary>
    public async Task<string?> GetChargingStatusAsync(CancellationToken cancellationToken = default)
    {
        var gdc = await ResolveRoutingAsync(cancellationToken).ConfigureAwait(false);

        var path = $"/app/authproxy/vwag-weconnect/proxy/vehicles/{Uri.EscapeDataString(_options.Vin)}"
            + $"/charging/status?gdc=myvw-{gdc}-prod&resourceHost=myvw-vcf-prod";

        var (status, body) = await ApiGetAsync(path, "*/*", cancellationToken).ConfigureAwait(false);

        if (status == HttpStatusCode.OK)
        {
            return body;
        }

        _logger.LogWarning("volkswagen.de charging/status answered {Status}.", (int)status);
        return null;
    }

    /// <summary>
    /// Which authproxy cluster this car is routed to.
    ///
    /// <para>Per vehicle, not fixed: VW's own front end looks it up, and a hardcoded value answers
    /// <c>412 Precondition Failed</c> for a car routed elsewhere however valid the session is. The
    /// relation record names the backend; the prefix before the underscore, lower-cased, is it.
    /// Cached for the life of the client — it does not change.</para>
    /// </summary>
    private async Task<string> ResolveRoutingAsync(CancellationToken cancellationToken)
    {
        if (_gdc is not null)
        {
            return _gdc;
        }

        var path = $"/app/authproxy/vw-de/proxy/v2/users/me/relations/{Uri.EscapeDataString(_options.Vin)}"
            + "?resourceHost=myvw-vum-prod";

        var (status, body) = await ApiGetAsync(path, "application/json", cancellationToken).ConfigureAwait(false);

        // "wcar" is what every caller used before the lookup existed, so a missing field or a changed
        // shape falls back to the value that already worked rather than blocking every data call.
        var gdc = "wcar";

        if (status == HttpStatusCode.OK && body is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(body);

                if (document.RootElement.TryGetProperty("relation", out var relation)
                    && relation.TryGetProperty("vehicle", out var vehicle)
                    && vehicle.TryGetProperty("modBackend", out var backend)
                    && backend.GetString() is { Length: > 0 } name)
                {
                    var prefix = name.Split('_', 2)[0];

                    if (prefix.Length > 0)
                    {
                        gdc = prefix.ToLowerInvariant();
                    }
                }
            }
            catch (JsonException)
            {
                // Fall through to the default rather than failing the read.
            }
        }

        _gdc = gdc;
        return gdc;
    }

    /// <summary>
    /// A data call, signing in again once if the session has lapsed.
    ///
    /// <para>Exactly once: a session that will not stick is a fault to report, not one to loop on.</para>
    /// </summary>
    private async Task<(HttpStatusCode Status, string? Body)> ApiGetAsync(
        string path, string accept, CancellationToken cancellationToken)
    {
        var result = await SendApiAsync(path, accept, cancellationToken).ConfigureAwait(false);

        if (!IsSessionGone(result.Status))
        {
            return result;
        }

        _logger.LogInformation("volkswagen.de bounced us ({Status}); signing in again.", (int)result.Status);

        if (await SignInAsync(cancellationToken).ConfigureAwait(false) != VwWebsiteLoginStep.SignedIn)
        {
            return result;
        }

        return await SendApiAsync(path, accept, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsSessionGone(HttpStatusCode status) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            or HttpStatusCode.PreconditionFailed or HttpStatusCode.PreconditionRequired;

    private async Task<(HttpStatusCode Status, string? Body)> SendApiAsync(
        string path, string accept, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _options.PortalBaseUrl + path);
        request.Headers.Accept.ParseAdd(accept);
        request.Headers.AcceptLanguage.ParseAdd("de-DE");
        request.Headers.Referrer = new Uri(Referer);
        request.Headers.TryAddWithoutValidation("x-csrf-token", CsrfToken() ?? string.Empty);
        request.Headers.TryAddWithoutValidation("user-id", "__userId__");
        request.Headers.TryAddWithoutValidation("traceId", Guid.NewGuid().ToString("N"));

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, body);
    }

    /// <summary>The CSRF token the portal expects back on every data call. It rides in a cookie.</summary>
    private string? CsrfToken() => _jar
        .GetAllCookies()
        .FirstOrDefault(cookie => cookie.Name.Equals("csrf_token", StringComparison.OrdinalIgnoreCase))
        ?.Value;

    private async Task<(string Url, string Body)> FollowAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return (response.RequestMessage?.RequestUri?.ToString() ?? url, body);
    }

    private async Task<(string Url, string Body)> PostAsync(
        string url, Dictionary<string, string> fields, string referer, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields),
        };

        request.Headers.Referrer = new Uri(referer);
        request.Headers.TryAddWithoutValidation("Origin", _options.IdentityBaseUrl);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return (response.RequestMessage?.RequestUri?.ToString() ?? url, body);
    }

    /// <summary>Forgets the session so the next attempt starts cold. For a sign-out control.</summary>
    public void Forget()
    {
        _session.Clear();
        _jar = new CookieContainer();
        _pendingCodeUrl = null;
        _pendingCodeState = null;
        _gdc = null;

        _http.Dispose();
        _handler.Dispose();
        (_handler, _http) = Build(_jar, _options.Timeout);
    }

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
        _gate.Dispose();
    }
}
