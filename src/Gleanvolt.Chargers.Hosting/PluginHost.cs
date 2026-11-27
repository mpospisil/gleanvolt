using System.Globalization;
using System.Reflection;
using Gleanvolt.Chargers.Components;
using Gleanvolt.Providers.Client;
using Gleanvolt.Providers.Client.Charger;
using Gleanvolt.Providers.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gleanvolt.Chargers;

/// <summary>What a plugin is.</summary>
/// <param name="ProviderId">Its id in the hello, stable for good: Gleanvolt remembers the first it accepts.</param>
/// <param name="Name">What Gleanvolt and the page show, e.g. "SolaX X3-EVC".</param>
/// <param name="Section">Its configuration section, e.g. <c>Solax</c>: <c>Solax:Host</c>, <c>Solax:DataDirectory</c>.</param>
/// <param name="Port">The page's port when <c>Urls</c> is not set.</param>
/// <param name="DefaultDevicePort">The charger's own port when an address names none: 502 for Modbus, 80 for HTTP.</param>
public sealed record PluginInfo(string ProviderId, string Name, string Section, int Port, int DefaultDevicePort)
{
    /// <summary>The plugin's version, from its assembly: the release tag's, or 0.0.0-dev.</summary>
    public string Version { get; init; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0-dev";

    /// <summary>The assembly with the plugin's page.</summary>
    public Assembly PageAssembly { get; init; } = Assembly.GetEntryAssembly()!;
}

/// <summary>The settings a plugin's page saves: the charger's address, and whatever else the plugin adds.</summary>
public sealed class PluginSettings
{
    private readonly IProviderSecretStore _files;
    private readonly IConfiguration _configuration;
    private readonly string _section;
    private readonly Dictionary<string, SavedSetting> _settings = new(StringComparer.Ordinal);

    public PluginSettings(IProviderSecretStore files, IConfiguration configuration, PluginInfo plugin)
    {
        _files = files;
        _configuration = configuration;
        _section = plugin.Section;
        Address = Get("host");
    }

    /// <summary>The charger's address: <c>&lt;Section&gt;:Host</c>, or what the page saved.</summary>
    public SavedSetting Address { get; }

    /// <summary>The setting <paramref name="name"/>: <c>&lt;Section&gt;:&lt;Name&gt;</c>, or what the page saved under that name.</summary>
    public SavedSetting Get(string name)
    {
        lock (_settings)
        {
            if (!_settings.TryGetValue(name, out var setting))
            {
                var key = $"{_section}:{char.ToUpperInvariant(name[0])}{name[1..]}";
                setting = new SavedSetting(_files, "setting-" + name, _configuration, key);
                _settings[name] = setting;
            }

            return setting;
        }
    }
}

/// <summary>
/// The host every plugin runs in: its configuration (user secrets, the environment, the command line), the page
/// that Gleanvolt frames and admits only with its ticket, the data directory (owner-only, the settings and the page's
/// keys), the connection to Gleanvolt, the status reporter, and the recording for testers.
/// </summary>
public static class PluginHost
{
    /// <summary>
    /// A builder for the plugin <paramref name="plugin"/> whose charger is <typeparamref name="TCharger"/>. The plugin
    /// adds its own services (its transport, its search) before <see cref="Build"/>.
    /// </summary>
    public static WebApplicationBuilder CreateBuilder<TCharger>(string[] args, PluginInfo plugin)
        where TCharger : PluginCharger
    {
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
        builder.Services.AddSingleton<DeviceRecorder>();
        builder.Services.AddSingleton<GleanvoltSession>();
        builder.Services.AddTransient<RecordingHandler>();

        // Neither is needed (#459): without a token the plugin asks Gleanvolt to pair it, and the owner's Accept hands
        // over the token and the address it is at. A token set here still wins, as before pairing existed.
        var gleanvolt = builder.Configuration["Gleanvolt:Endpoint"] is { Length: > 0 } endpoint ? new Uri(endpoint) : null;
        var frameAncestors = builder.Configuration["Gleanvolt:FrameAncestors"];
        builder.Services.AddSingleton(provider => new PageFraming(provider.GetRequiredService<ProviderCredentials>(), frameAncestors));

        builder.Services.AddGleanvoltChargerProvider<TCharger>(options =>
        {
            options.Endpoint = gleanvolt;
            options.Token = builder.Configuration["Gleanvolt:Token"] ?? string.Empty;
            options.ProviderId = plugin.ProviderId;
            options.Name = plugin.Name;
            options.Version = plugin.Version;
            options.PageUrl = new Uri(pageUrl);
        });

        // The charger the SDK commands is the one the page shows.
        builder.Services.AddSingleton(provider => (TCharger)provider.GetRequiredService<IChargerSource>());
        builder.Services.AddSingleton<PluginCharger>(provider => provider.GetRequiredService<TCharger>());
        builder.Services.AddHostedService<StatusReporter>();

        builder.Services.AddRazorComponents().AddInteractiveServerComponents();

        // Framed by Gleanvolt on another port, so the frame-ancestors policy decides, not X-Frame-Options.
        builder.Services.AddAntiforgery(options => options.SuppressXFrameOptionsHeader = true);
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(data, "keys")))
            .SetApplicationName("gleanvolt-plugin-" + plugin.ProviderId);

        return builder;
    }

    /// <summary>The plugin's application: the page, guarded by Gleanvolt's ticket, and the recording's download.</summary>
    public static WebApplication Build(WebApplicationBuilder builder)
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

        // Everything after it answers only a browser Gleanvolt sent, with a ticket: the page changes what charger is driven.
        var pairing = app.Services.GetRequiredService<ProviderPairing>();
        app.UseGleanvoltProviderPage(options => options.Unpaired = context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            return context.Response.WriteAsync(WaitingPage.Html(plugin, pairing.Status, AddressesOrNone(LanScan.OwnAddresses), CultureInfo.CurrentUICulture));
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

        // No extension in the path: the page guard admits a GET with one without the ticket, as a static file.
        app.MapGet("/recording", (DeviceRecorder recorder, PluginSettings settings) =>
            Results.File(
                System.Text.Encoding.UTF8.GetBytes(recorder.ToJson(plugin.ProviderId, plugin.Version, new { address = settings.Address.Current })),
                "application/json",
                $"{plugin.ProviderId}-recording.json"));

        // Null: Blazor's own policy is fixed at start-up, and the middleware above already writes one that follows the pairing.
        app.MapRazorComponents<App>()
            .AddAdditionalAssemblies(plugin.PageAssembly)
            .AddInteractiveServerRenderMode(options => options.ContentSecurityFrameAncestorsPolicy = null);

        return app;
    }

    // The waiting view is all a plugin that waits to be paired shows: a machine whose interfaces cannot be listed (a
    // sandbox, a container without netlink) shows it without its addresses rather than answering 500.
    internal static IReadOnlyList<System.Net.IPAddress> AddressesOrNone(Func<IReadOnlyList<System.Net.IPAddress>> read)
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
