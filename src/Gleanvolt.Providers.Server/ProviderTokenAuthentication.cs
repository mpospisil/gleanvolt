using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gleanvolt.Providers.Server;

/// <summary>
/// Authenticates a provider by the provider token (#386, one for every kind since #406), separately
/// from the web password and the API keys. The token comes as <c>Authorization: Bearer</c>, or as
/// <c>access_token</c> in the query, which is the only way a browser-style WebSocket can carry it. The
/// identity says which kind's hub was asked for; the hub checks that against its own kind.
/// </summary>
internal sealed class ProviderTokenAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ExternalProviderRegistry registry)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "GleanvoltProvider";

    public const string KindClaim = "gleanvolt-provider-kind";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = Request.Headers.Authorization.ToString() is { } header && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : Request.Query["access_token"].ToString();

        if (string.IsNullOrEmpty(token))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // The kind is the one whose hub is asked for; the token is the same for all of them.
        if (registry.KindAt(Request.Path) is not { } kind || !registry.Tokens.Matches(token))
        {
            return Task.FromResult(AuthenticateResult.Fail("Not the provider token."));
        }

        var identity = new ClaimsIdentity([new Claim(KindClaim, kind.Id)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
