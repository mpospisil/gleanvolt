using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gleanvolt.Infrastructure.Vehicles.VwGroup;

/// <summary>
/// The first of the two classes that touch the network: the OIDC sign-in, driven as HTML forms
/// (issue #139, step 1).
///
/// <para>Authorization code against <c>identity.vwgroup.io/oidc/v1/authorize</c>, scope
/// <c>openid cars profile</c>, <b>no PKCE</b>, and a <c>redirect_uri</c> fixed to the portal's own
/// <c>/login</c>. That last part is why this is a form replay rather than a shortcut past a proper
/// flow: the redirect target is not ours and there is no "register your own OAuth app" route, so
/// replaying the forms <em>is</em> the flow.</para>
///
/// <para><b>The consent screen is not handled here and never will be.</b> If one appears — or an OTP,
/// or a CAPTCHA — this fails as <see cref="VwGroupFailure.OwnerActionRequired"/> and does not retry.
/// A program cannot answer it, and looping on it risks the account for nothing. That failure is what
/// the dashboard's "sign-in required" is built on: a distinct state, not a stale reading.</para>
///
/// <para>All the judgement lives in <see cref="VwGroupLoginForm"/>, which is pure and tested. What is
/// here is transport and the order of the steps.</para>
/// </summary>
public sealed class VwGroupSignIn
{
    /// <summary>Name a configured <see cref="HttpClient"/> would be registered under.</summary>
    public const string HttpClientName = "VwGroupPortal";

    /// <summary>
    /// How many form pages the flow may take before it is treated as a loop. Two are expected —
    /// identifier, then password — and the margin exists because the identity provider has interposed
    /// an extra page before and may again.
    /// </summary>
    private const int MaxPages = 5;

    private readonly HttpClient _http;
    private readonly VwGroupPortalOptions _options;
    private readonly ILogger _logger;

    public VwGroupSignIn(HttpClient http, VwGroupPortalOptions options, ILogger? logger = null)
    {
        _http = http;
        _options = options;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// A handler configured the way this flow needs: a cookie jar, and redirects followed
    /// automatically.
    ///
    /// <para>Offered as a factory because getting it wrong is silent — without a
    /// <see cref="CookieContainer"/> every request is a fresh anonymous one, and the flow simply never
    /// completes while looking like a credential problem.</para>
    /// </summary>
    public static HttpClientHandler CreateHandler(CookieContainer? cookies = null) => new()
    {
        CookieContainer = cookies ?? new CookieContainer(),
        UseCookies = true,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 20,
    };

    /// <summary>
    /// Signs in, leaving a usable session in the handler's cookie jar.
    /// </summary>
    /// <exception cref="VwGroupPortalException">
    /// With <see cref="VwGroupFailure.OwnerActionRequired"/> for anything only a browser can answer,
    /// <see cref="VwGroupFailure.SignInRejected"/> for a refused password, and
    /// <see cref="VwGroupFailure.Transient"/> for the network.
    /// </exception>
    public async Task SignInAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
        {
            throw new VwGroupPortalException(
                VwGroupFailure.NotConfigured,
                $"the VW portal client needs {_options.DescribeWhatIsMissing()}");
        }

        // The portal first, and its answer is discarded. It sets the AEM cookies its own
        // /services/callbacklogin needs; without them the code comes back to a session that does not
        // exist. Failure here is not fatal -- the flow may still work -- so it is logged, not thrown.
        try
        {
            await GetAsync(_options.PortalBaseUrl.TrimEnd('/') + "/", cancellationToken).ConfigureAwait(false);
        }
        catch (VwGroupPortalException failure)
        {
            _logger.LogDebug("Priming the portal failed, continuing anyway: {Reason}", failure.Message);
        }

        var page = await GetAsync(AuthorizeUrl(), cancellationToken).ConfigureAwait(false);

        for (var attempt = 0; attempt < MaxPages; attempt++)
        {
            if (IsPortal(page.Url))
            {
                _logger.LogInformation("Signed in to the VW portal after {Pages} form page(s).", attempt);
                return;
            }

            var form = VwGroupLoginForm.Parse(page.Body);

            // Checked before posting rather than after failing: putting a password into a consent
            // screen tells the identity provider nothing and tells us nothing either.
            //
            // But only when there is nothing to fill. A page we can sign in on is a sign-in page,
            // whatever words are printed on it -- and this client is named "Consent Portal", so its
            // ordinary email form says "consent" six times: in the client id's app name, in the
            // templateModel, and in the visible subtitle "Welcome to Consent Portal". Reading that as
            // a consent screen aborted every sign-in before the first field was filled.
            if (!form.CanSignIn && VwGroupLoginForm.OwnerActionReason(page.Body) is { } reason)
            {
                throw new VwGroupPortalException(
                    VwGroupFailure.OwnerActionRequired,
                    $"the portal is showing {reason}, which only the owner can answer in a browser at "
                    + $"{_options.PortalBaseUrl}. Nothing here will retry until it has been.");
            }

            if (!form.IsPostable)
            {
                // The provider often says exactly what it objected to, and the request can be refused
                // before sign-in ever begins -- a redirect_uri it does not recognise, an unknown client
                // id. Quoting it beats reporting the absence of a form we were never going to get.
                if (VwGroupLoginForm.OAuthError(page.Body) is { } refusal)
                {
                    throw new VwGroupPortalException(
                        VwGroupFailure.SignInRejected,
                        $"the identity provider refused the request at {Where(page.Url)}: {refusal}");
                }

                throw new VwGroupPortalException(
                    VwGroupFailure.SignInRejected,
                    $"the identity provider returned a page with no form to post at {Where(page.Url)}");
            }

            var fields = new Dictionary<string, string>(form.Fields, StringComparer.Ordinal);
            string? filled = null;

            // Password first: once its page is reached, that is the field that moves the flow on.
            if (form.PasswordField is { } passwordField)
            {
                fields[passwordField] = _options.Password;
                filled = "password";
            }
            else if (form.IdentifierField is { } identifierField)
            {
                fields[identifierField] = _options.Username;
                filled = "identifier";
            }

            if (filled is null)
            {
                throw new VwGroupPortalException(
                    VwGroupFailure.SignInRejected,
                    $"the form at {Where(page.Url)} asked for none of identifier, email or password "
                    + $"(fields: {string.Join(", ", form.Fields.Keys)})");
            }

            // The hidden fields -- hmac, _csrf, relayState and whatever else the identity provider's
            // templateModel carried -- go back verbatim. Names are logged, values never: one of them
            // is a CSRF token and the rest are session state.
            var target = TargetFor(form, page.Url);

            _logger.LogDebug(
                "Posting the {Which} form to {Target} with {Fields}.",
                filled, Where(target), string.Join(", ", form.Fields.Keys));

            page = await PostAsync(target, fields, page.Url, cancellationToken).ConfigureAwait(false);
        }

        throw new VwGroupPortalException(
            VwGroupFailure.SignInRejected,
            $"the sign-in flow did not complete within {MaxPages} pages; the last one was "
            + $"{Where(page.Url)}");
    }

    /// <summary>
    /// Where this page's credentials go.
    ///
    /// <para>An HTML form's <c>action</c> is resolved against the page, as a browser would. The
    /// template model's <c>postAction</c> is <b>not</b>: it is relative to the sign-in service's own
    /// client root, and resolving <c>login/authenticate</c> against a page already at
    /// <c>.../login/authenticate</c> yields <c>.../login/login/authenticate</c> — a 404 dressed up as
    /// a refused password.</para>
    /// </summary>
    public string TargetFor(VwGroupLoginForm form, string pageUrl) =>
        form.Action is { Length: > 0 } action
            ? new Uri(new Uri(pageUrl), action).ToString()
            : $"{_options.IdentityBaseUrl.TrimEnd('/')}/signin-service/v1/"
              + $"{_options.ResolvedClientId}/{form.PostAction!.TrimStart('/')}";

    /// <summary>
    /// The authorize URL, as the portal's own client builds it. No <c>code_challenge</c>: this flow
    /// has no PKCE, and adding one changes what the identity provider expects back.
    /// </summary>
    public string AuthorizeUrl()
    {
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = _options.ResolvedClientId,
            ["scope"] = _options.Scope,
            ["response_type"] = "code",
            ["redirect_uri"] = _options.RedirectUri,
            // NOT a nonce, despite what the specification would allow: the portal decodes this to
            // route the callback, and a random one leaves it with nothing to match -- verified live as
            // a bounce back to the login page and 401 on every API call afterwards.
            ["state"] = _options.State,

            // The portal's own client sends this, and sending what it sends is the whole strategy.
            ["prompt"] = "login",
        };

        var encoded = string.Join("&", query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value ?? string.Empty)}"));

        return $"{_options.IdentityBaseUrl.TrimEnd('/')}/oidc/v1/authorize?{encoded}";
    }

    // Host *and* port: a session belongs to an origin, and the port is part of one. The real portal
    // and identity provider are different hosts so either comparison would do against them -- but a
    // stub that puts both on localhost is exactly how this flow gets exercised without a car, and
    // host-only matching declares victory on the authorize page there.
    private bool IsPortal(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && Uri.TryCreate(_options.PortalBaseUrl, UriKind.Absolute, out var portal)
        && string.Equals(parsed.Authority, portal.Authority, StringComparison.OrdinalIgnoreCase);

    /// <summary>Host and path only. A sign-in URL's query carries state that has no business in a log.</summary>
    internal static string Where(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? $"{parsed.Host}{parsed.AbsolutePath}" : url;

    private async Task<(string Url, string Body)> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(string Url, string Body)> PostAsync(
        string url, Dictionary<string, string> fields, string referer, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields),
        };

        request.Headers.Referrer = new Uri(referer);
        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(string Url, string Body)> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Timeout);

            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            // An error status is not an exception here: a 4xx from the identity provider is usually a
            // page explaining what it wants, and reading it is how the next step is chosen.
            return (response.RequestMessage?.RequestUri?.ToString() ?? request.RequestUri!.ToString(), body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new VwGroupPortalException(
                VwGroupFailure.Transient, $"the identity provider did not answer within {_options.Timeout}");
        }
        catch (HttpRequestException ex)
        {
            throw new VwGroupPortalException(
                VwGroupFailure.Transient, $"could not reach the identity provider ({ex.Message})", ex);
        }
    }
}
