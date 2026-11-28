using System.Globalization;
using System.Net;
using System.Reflection;
using Gleanvolt.Plugins.Components;
using Gleanvolt.Providers.Client;
using Gleanvolt.Providers.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gleanvolt.Plugins;

/// <summary>
/// The host every plugin runs in, whatever its kind (#472): its configuration (user secrets, the environment, the
/// command line), the page that Gleanvolt frames and admits only with its ticket, the waiting view until the owner
/// accepts it, the data directory (owner-only, the settings and the page's keys) and the connection to Gleanvolt. A
/// kind's layer registers its provider through <c>addProvider</c> and adds its own services.
/// </summary>
public static class PluginWebHost
{
    /// <summary>
    /// A builder for the plugin <paramref name="plugin"/>. <paramref name="addProvider"/> registers the kind's provider
    /// with the options this host fills in, e.g.
    /// <c>(services, configure) =&gt; services.AddGleanvoltVehicleProvider&lt;MyCar&gt;(configure)</c>.
    /// </summary>
    public static WebApplicationBuilder CreateBuilder(
        string[] args, PluginInfo plugin, Action<IServiceCollection, Action<ProviderOptions>> addProvider)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(addProvider);

        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddUserSecrets(plugin.PageAssembly, optional: true);
        builder.Configuration.AddEnvironmentVariables();
        builder.Configuration.AddCommandLine(args);

        // Where the page listens, and the address the owner's browser opens it at: Gleanvolt:PageUrl when the plugin
        // runs elsewhere than the browser can reach by localhost.
        var listen = builder.Configuration["Urls"] ?? $"http://localhost:{plugin.Port}";
        builder.WebHost.UseUrls(listen);
        var pageUrl = builder.Configuration["Gleanvolt:PageUrl"]
            ?? listen.Split(';')[0].Replace("0.0.0.0", "localhost", StringComparison.Ordinal).Replace("+", "localhost", StringComparison.Ordinal);

        // <Section>:DataDirectory, or DataDirectory alone, as the image sets it for every plugin.
        var data = Path.GetFullPath(builder.Configuration[$"{plugin.Section}:DataDirectory"] ?? builder.Configuration["DataDirectory"] ?? "data");
        builder.Services.AddSingleton(plugin);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddGleanvoltProviderSecrets(data);
        builder.Services.AddSingleton<PluginSettings>();

        // Neither is needed (#459): without a token the plugin asks Gleanvolt to pair it, and the owner's Accept hands
        // over the token and the address it is at. A token set here still wins, as before pairing existed.
        var gleanvolt = builder.Configuration["Gleanvolt:Endpoint"] is { Length: > 0 } endpoint ? new Uri(endpoint) : null;
        var frameAncestors = builder.Configuration["Gleanvolt:FrameAncestors"];
        builder.Services.AddSingleton(provider => new PageFraming(provider.GetRequiredService<ProviderCredentials>(), frameAncestors));

        addProvider(builder.Services, options =>
        {
            options.Endpoint = gleanvolt;
            options.Token = builder.Configuration["Gleanvolt:Token"] ?? string.Empty;
            options.ProviderId = plugin.ProviderId;
            options.Name = plugin.Name;
            options.Version = plugin.Version;
            options.PageUrl = new Uri(pageUrl);
        });

        builder.Services.AddRazorComponents().AddInteractiveServerComponents();

        // Framed by Gleanvolt on another port, so the frame-ancestors policy decides, not X-Frame-Options.
        builder.Services.AddAntiforgery(options => options.SuppressXFrameOptionsHeader = true);
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(data, "keys")))
            .SetApplicationName("gleanvolt-plugin-" + plugin.ProviderId);

        return builder;
    }

    /// <summary>
    /// The plugin's application: the page, guarded by Gleanvolt's ticket, with the kind's own <paramref name="endpoints"/>
    /// mapped before the page and <paramref name="extraAssemblies"/> searched for routable components beside the plugin's.
    /// </summary>
    public static WebApplication Build(WebApplicationBuilder builder, Action<WebApplication>? endpoints = null, params Assembly[] extraAssemblies)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var app = builder.Build();
        var plugin = app.Services.GetRequiredService<PluginInfo>();

        // On every response, the waiting view and the refusals and the static files too, and read per request: pairing
        // can change the Gleanvolt in use while the plugin runs.
        var framing = app.Services.GetRequiredService<PageFraming>();
        app.Use((context, next) =>
        {
            context.Response.Headers.ContentSecurityPolicy = "frame-ancestors " + framing.FrameAncestors();
            return next(context);
        });

        app.UsePluginCulture();

        // Everything after it answers only a browser Gleanvolt sent, with a ticket: the page changes what the plugin does.
        var pairing = app.Services.GetRequiredService<ProviderPairing>();
        app.UseGleanvoltProviderPage(options => options.Unpaired = context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            return context.Response.WriteAsync(WaitingPage.Html(plugin, pairing.Status, AddressesOrNone(NetworkAddresses.OwnAddresses), CultureInfo.CurrentUICulture));
        });

        app.Use((context, next) =>
        {
            if (context.Request.Query.ContainsKey(ProviderPage.CultureParameter)
                && context.Features.Get<IRequestCultureFeature>() is { } culture)
            {
                context.Response.Cookies.Append(
                    CookieRequestCultureProvider.DefaultCookieName,
                    CookieRequestCultureProvider.MakeCookieValue(culture.RequestCulture),
                    new CookieOptions { HttpOnly = true, IsEssential = true, SameSite = SameSiteMode.Lax, Path = "/" });
            }

            return next(context);
        });
        app.UseAntiforgery();
        app.MapStaticAssets();

        endpoints?.Invoke(app);

        // Null: Blazor's own policy is fixed at start-up, and the middleware above already writes one that follows the pairing.
        app.MapRazorComponents<App>()
            .AddAdditionalAssemblies([plugin.PageAssembly, .. extraAssemblies])
            .AddInteractiveServerRenderMode(options => options.ContentSecurityFrameAncestorsPolicy = null);

        return app;
    }

    // The waiting view is all a plugin that waits to be paired shows: a machine whose interfaces cannot be listed (a
    // sandbox, a container without netlink) shows it without its addresses rather than answering 500.
    internal static IReadOnlyList<IPAddress> AddressesOrNone(Func<IReadOnlyList<IPAddress>> read)
    {
        try
        {
            return read();
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return [];
        }
    }
}

/// <summary>
/// The page's frame-ancestors policy: its own origin and Gleanvolt's, or the ones the owner configured. Gleanvolt's is
/// the one in use, so it moves to the paired one when the owner accepts the plugin (#459); a paired one allows any host
/// on its port, since the browser may open Gleanvolt by a name other than the address the plugin found.
/// </summary>
/// <param name="credentials">Where the Gleanvolt in use is.</param>
/// <param name="configured"><c>Gleanvolt:FrameAncestors</c>, which wins when set.</param>
public sealed class PageFraming(ProviderCredentials credentials, string? configured)
{
    /// <summary>The <c>frame-ancestors</c> sources now, e.g. <c>'self' http://192.168.1.5:8090</c>.</summary>
    public string FrameAncestors() => "'self' " + ProviderPageFraming.Sources(credentials, configured);
}
