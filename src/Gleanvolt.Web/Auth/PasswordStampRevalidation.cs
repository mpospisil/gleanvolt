using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.Logging;

namespace Gleanvolt.Web.Auth;

/// <summary>
/// Signs an open page out when the password it was signed in under is replaced (#301). The cookie
/// check covers every new request; this covers the interactive circuit, which carries its identity
/// for as long as the tab stays open and never sends the cookie again.
/// </summary>
public sealed class PasswordStampRevalidation(ILoggerFactory loggers, WebPasswordState password)
    : RevalidatingServerAuthenticationStateProvider(loggers)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether <paramref name="principal"/> was signed in under the password in force. True for an
    /// anonymous principal: there is nothing to sign out.
    /// </summary>
    public static bool Matches(ClaimsPrincipal? principal, WebPasswordState password)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (principal?.Identity?.IsAuthenticated != true)
        {
            return true;
        }

        return principal.FindFirst(PasswordStamp.ClaimType)?.Value == password.Stamp;
    }

    protected override Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken) =>
        Task.FromResult(Matches(authenticationState.User, password));
}
