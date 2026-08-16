using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web;

/// <summary>
/// Registers and maps the parts of the self-hosted UI that are the same regardless of host: cookie
/// authentication, the <see cref="WebOptions.RequireAuthentication"/> toggle, Razor component
/// routing, and the login/logout endpoints.
///
/// <para>Kestrel's listen address, <c>UseStaticWebAssets</c> and the "nothing is listening" fallback
/// are host concerns particular to how Gleanvolt.Worker runs and stay in its <c>Program.cs</c>; this is
/// everything else, factored out so it is exercised the same way in a test host as it is in
/// production, without booting the worker's Modbus clients, session store or MQTT worker.</para>
/// </summary>
public static class WebUiHost
{
    /// <summary>
    /// Fails fast on the one authentication setting that cannot be honoured: a login explicitly
    /// demanded with no password to check against. Nobody could sign in, so every page would be
    /// permanently unreachable — a stopped host says that immediately, where a running one would
    /// look like a broken UI.
    ///
    /// <para>Leaving <c>Web:RequireAuthentication</c> unset is not this case: it means "follow the
    /// password", and no password simply means no login. Call before <c>Build()</c> — same place in
    /// the pipeline as the timezone fail-fast in Program.cs.</para>
    /// </summary>
    public static void ValidateAuthenticationConfig(this WebOptions web)
    {
        if (web.Enabled && web.RequireAuthentication == true && string.IsNullOrWhiteSpace(web.PasswordHash))
        {
            throw new InvalidOperationException(
                "Web:RequireAuthentication is true but Web:PasswordHash is not set, so nobody could "
                + "ever sign in. Generate a hash with 'dotnet Gleanvolt.Worker.dll hash-password "
                + "<password>' and set it via Web__PasswordHash (never in appsettings.json), or "
                + "leave Web:RequireAuthentication unset to run the UI without a login.");
        }
    }

    /// <summary>Registers the UI's services. Only meaningful when <see cref="WebOptions.Enabled"/> is true.</summary>
    public static void AddGleanvoltWebUi(this IServiceCollection services, WebOptions web)
    {
        // Interactive server rendering: the components run here, beside the services they read, and
        // the browser holds a thin circuit. That is what lets a page update itself as each poll
        // lands without a REST API in between -- WebAssembly would need one, and this project needs
        // it for nothing else.
        services.AddRazorComponents().AddInteractiveServerComponents();

        // The scheme is registered whenever the UI is enabled, whether or not RequireAuthentication
        // is on: the login and logout endpoints need it to exist either way, and it is DefaultPolicy
        // below -- not this registration -- that decides whether [Authorize] actually turns anyone
        // away.
        services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = "/login";
                options.Cookie.Name = "solax_auth";
                options.ExpireTimeSpan = TimeSpan.FromDays(30);
                options.SlidingExpiration = true;
            });

        // Flows the signed-in user down to AuthorizeView and AuthorizeRouteView in both the static
        // and the interactive parts of the component tree -- MainLayout's sign-out control and every
        // page's [Authorize] attribute both depend on this being registered.
        services.AddCascadingAuthenticationState();

        services.AddAuthorization(options =>
        {
            // [Authorize] with no policy name resolves to DefaultPolicy: this one toggle is what
            // makes the login optional rather than merely documented as such. The login page carries
            // [AllowAnonymous], which wins regardless of what this is set to.
            options.DefaultPolicy = web.AuthenticationRequired
                ? new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()
                : new AuthorizationPolicyBuilder().RequireAssertion(_ => true).Build();

            // Separate from DefaultPolicy on purpose: "is somebody actually signed in" has to stay
            // accurate for the sign-out control even when RequireAuthentication is off and
            // DefaultPolicy therefore admits anyone.
            options.AddPolicy("Authenticated", policy => policy.RequireAuthenticatedUser());
        });
    }

    /// <summary>
    /// Maps the UI's middleware and endpoints. Only meaningful when <see cref="WebOptions.Enabled"/>
    /// is true. Deliberately does not call <c>MapStaticAssets()</c> -- that reads a manifest generated
    /// for the *host* project (Gleanvolt.Worker), so it stays a host concern in Program.cs, next to the
    /// matching <c>UseStaticWebAssets()</c> call. A test host with no such manifest can still map
    /// everything else here.
    /// </summary>
    public static void MapGleanvoltWebUi(this WebApplication app, WebOptions web)
    {
        app.UseAuthentication();
        app.UseAuthorization();

        // Antiforgery is a hard requirement of MapRazorComponents, not an optional hardening step.
        app.UseAntiforgery();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

        // A plain endpoint rather than a Blazor component: MainLayout's sign-out form has to work
        // from both the interactive and the static pages it's rendered on (see MainLayout.razor),
        // which a normal HTML form post gives for free and a component event handler would not.
        app.MapPost("/logout", async context =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            context.Response.Redirect("/login");
        });

        // Said out loud on every start, not only when someone opted out: an open control surface is
        // exactly the sort of thing an operator should be able to establish from a log file after
        // the fact, and the default being open is what makes that worth logging rather than assuming.
        if (!web.AuthenticationRequired)
        {
            app.Logger.LogWarning(
                "Web UI has no login: every page is reachable by anyone who can reach the port. "
                + "Keep it to a trusted LAN, or set Web__PasswordHash to require a sign-in "
                + "(hash-password generates one).");
        }
    }
}
