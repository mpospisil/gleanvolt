using Gleanvolt.Web.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Gleanvolt.Web;

/// <summary>
/// Holds the UI closed until a password exists.
///
/// <para>The default used to be an open control surface: the UI is on, it binds every interface over
/// plain HTTP, and it required no sign-in unless somebody had already thought to configure one.
/// Anyone who could reach the port could read the household's energy and charging history and change
/// how the charger is driven. Being warned about it in a log line is not the same as being protected
/// from it.</para>
///
/// <para>So a build with no password serves exactly one thing — the page that sets one — and nothing
/// else. Not a lockout and not a generated secret fished out of a log: the first person to open the
/// UI is asked to choose a password, and from then on it behaves as it always did.</para>
/// </summary>
public static class SetupGate
{
    /// <summary>Where an un-set-up UI sends every request.</summary>
    public const string Path = "/setup";

    /// <summary>Where setup mode sends a page that needs the controller (#301).</summary>
    public const string SetupModePath = "/settings";

    /// <summary>
    /// Serves only the setup page while no password is set. Must sit before authentication: with no
    /// password there is nothing to authenticate against, so the login page would be a dead end.
    ///
    /// <para>Framework and static assets are let through because the setup page is unusable without
    /// its stylesheet, and they carry nothing about the household.</para>
    /// </summary>
    public static void UseGleanvoltSetupGate(this WebApplication app, WebOptions web)
    {
        // Setup mode (#301): the controller is not running, so a page that shows or drives it has
        // nothing to show. Those go to Settings, where the missing values are entered; the pages that
        // set up, sign in, reset a password or report health still answer. Ahead of the password
        // check below, and regardless of it: setup mode is about the controller, not the login.
        app.Use(async (context, next) =>
        {
            var setup = context.RequestServices.GetRequiredService<Core.Interfaces.ISetupState>();

            // With no password yet, the password gate below sends everything to /setup, and that is
            // the first step anyway: one redirect, not two.
            var passwordFirst = web.RequireAuthentication != false
                && !context.RequestServices.GetRequiredService<WebPasswordState>().IsSet;

            if (setup.SetupMode
                && !passwordFirst
                && !IsAllowedInSetupMode(context.Request.Path)
                && !context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.Redirect(SetupModePath);
                return;
            }

            await next(context);
        });

        // An operator who has said Web:RequireAuthentication=false has chosen an open UI knowingly,
        // and gets one. The gate exists to close the *default*, not to remove the choice -- being
        // sent to a setup page you deliberately opted out of would be the tool overriding you.
        if (web.RequireAuthentication == false)
        {
            return;
        }

        app.Use(async (context, next) =>
        {
            var state = context.RequestServices.GetRequiredService<WebPasswordState>();

            if (state.IsSet || IsAllowedWhileUnconfigured(context.Request.Path))
            {
                await next(context);
                return;
            }

            // The API is not redirected: it guards itself with a key, refuses to start without one,
            // and answers a program rather than a browser. A 307 to an HTML page would be a confusing
            // answer to a JSON client, and an unnecessary one.
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                await next(context);
                return;
            }

            context.Response.Redirect(Path);
        });
    }

    private static bool IsAllowedWhileUnconfigured(PathString path) =>
        path.StartsWithSegments(Path)
        || path.StartsWithSegments(Components.Pages.ForgotPassword.Path)
        || path.StartsWithSegments(WebUiCultures.Path)
        // Blazor's own script and the static assets the page is rendered with.
        || path.StartsWithSegments("/_framework")
        || path.StartsWithSegments("/_content")
        || path.StartsWithSegments("/css")
        || path.StartsWithSegments("/lib")
        || path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase);

    // Everything that works without the controller: setting up, signing in and out, resetting the
    // password, Settings and its Health half, the car's editor -- and the circuit those interactive
    // pages run on, without which none of them would.
    private static bool IsAllowedInSetupMode(PathString path) =>
        IsAllowedWhileUnconfigured(path)
        || path.StartsWithSegments("/login")
        || path.StartsWithSegments("/logout")
        || path.StartsWithSegments("/settings")
        || path.StartsWithSegments("/pv-system")
        || path.StartsWithSegments("/health")
        || path.StartsWithSegments("/car")
        || path.StartsWithSegments("/_blazor");
}
