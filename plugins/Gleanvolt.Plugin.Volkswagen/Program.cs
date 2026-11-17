using Gleanvolt.Plugin.Volkswagen.Components;
using Gleanvolt.Plugin.Volkswagen.Volkswagen;
using Gleanvolt.Providers.Client;
using Gleanvolt.Providers.Client.Vehicle;
using Gleanvolt.Providers.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;

// The volkswagen.de car feed as an external provider for Gleanvolt (#410): a copy of the built-in
// Vehicle:Website feed, with its own page for the account, the sign-in and the emailed code, which
// Gleanvolt shows on its Car page. Run it only with "An external provider" chosen on the Car page, so it
// and the built-in feed never use the VW account at the same time. See README.md.

var builder = WebApplication.CreateBuilder(args);

// User secrets however it is started, as the samples do; environment variables and the command line win.
builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

// Where the page listens, and the address the owner's browser opens it at. They differ when it listens on
// every interface (a container): Gleanvolt must be told an address a browser can reach.
var listen = builder.Configuration["Urls"] ?? "http://localhost:8095";
builder.WebHost.UseUrls(listen);
var pageUrl = builder.Configuration["Gleanvolt:PageUrl"]
    ?? listen.Split(';')[0].Replace("0.0.0.0", "localhost", StringComparison.Ordinal).Replace("+", "localhost", StringComparison.Ordinal);

var volkswagen = builder.Configuration.GetSection(VolkswagenOptions.SectionName).Get<VolkswagenOptions>() ?? new VolkswagenOptions();
builder.Services.AddSingleton(volkswagen);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DataFiles>();
builder.Services.AddSingleton<AccountStore>();
builder.Services.AddSingleton<VwWebsiteSessionStore>();
builder.Services.AddSingleton(provider => new VwWebsiteClient(
    provider.GetRequiredService<VolkswagenOptions>(),
    provider.GetRequiredService<AccountStore>(),
    provider.GetRequiredService<VwWebsiteSessionStore>(),
    provider.GetRequiredService<ILogger<VwWebsiteClient>>(),
    provider.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<VwWebsiteSignIn>();

var gleanvolt = new Uri(builder.Configuration["Gleanvolt:Endpoint"] ?? "http://localhost:8090");

// Who may show the page in a frame: Gleanvolt, at the address the browser opens it at. Its endpoint by
// default; set Gleanvolt:FrameAncestors when the browser reaches Gleanvolt by another name.
var frameAncestors = "'self' " + (builder.Configuration["Gleanvolt:FrameAncestors"] ?? gleanvolt.GetLeftPart(UriPartial.Authority));

builder.Services.AddGleanvoltVehicleProvider<VolkswagenFeed>(options =>
{
    options.Endpoint = gleanvolt;
    options.Token = builder.Configuration["Gleanvolt:Token"]
        ?? throw new InvalidOperationException("Set Gleanvolt:Token to the token from Gleanvolt's Settings → External providers.");
    options.ProviderId = "volkswagen";
    options.Name = "Volkswagen";
    options.PageUrl = new Uri(pageUrl);
});

// The feed the SDK asks is the one the page and the poller use.
builder.Services.AddSingleton(provider => (VolkswagenFeed)provider.GetRequiredService<IVehicleSource>());
builder.Services.AddSingleton<VolkswagenPoller>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<VolkswagenPoller>());

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Antiforgery sends X-Frame-Options: SAMEORIGIN, and Gleanvolt on another port is another origin: the
// frame would stay empty. The frame-ancestors policy below says who may frame the page instead.
builder.Services.AddAntiforgery(options => options.SuppressXFrameOptionsHeader = true);

// Blazor's own keys (circuits, antiforgery), beside the account in the data directory rather than in a
// home directory a container forgets.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(Path.GetFullPath(volkswagen.DataDirectory), "keys")))
    .SetApplicationName("gleanvolt-plugin-volkswagen");

var app = builder.Build();

// The page in the viewer's language (#415): Gleanvolt opens it with ?culture=en or cs. The query decides
// the first request; a cookie carries it to Blazor's circuit, whose requests have no query of their own,
// and to a reload in the frame.
app.UseRequestLocalization(options =>
{
    options.SetDefaultCulture("en").AddSupportedCultures("en", "cs").AddSupportedUICultures("en", "cs");
    options.RequestCultureProviders = [new QueryStringRequestCultureProvider(), new CookieRequestCultureProvider()];
});

// Everything after it answers only a browser Gleanvolt sent, with a ticket.
app.UseGleanvoltProviderPage();

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
app.MapRazorComponents<App>().AddInteractiveServerRenderMode(options =>
    options.ContentSecurityFrameAncestorsPolicy = frameAncestors);

app.Run();
