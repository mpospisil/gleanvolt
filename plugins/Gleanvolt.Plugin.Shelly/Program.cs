using Gleanvolt.Plugin.Shelly.Components;
using Gleanvolt.Plugin.Shelly.TopAc;
using Gleanvolt.Providers.Client;
using Gleanvolt.Providers.Client.Charger;
using Gleanvolt.Providers.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;

// The Shelly TopAC Portable EV Charger as a charger plugin for Gleanvolt (#429, #435), with its own page for the
// charger's address, which Gleanvolt links from Settings → External providers. Run it with "External plugin"
// chosen as the charger's brand on Gleanvolt's Settings. See README.md.

var builder = WebApplication.CreateBuilder(args);

// User secrets however it is started, as the other plugins do; environment variables and the command line win.
builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

// Where the page listens, and the address the owner's browser opens it at, as for the OpenWeather plugin.
var listen = builder.Configuration["Urls"] ?? "http://localhost:8097";
builder.WebHost.UseUrls(listen);
var pageUrl = builder.Configuration["Gleanvolt:PageUrl"]
    ?? listen.Split(';')[0].Replace("0.0.0.0", "localhost", StringComparison.Ordinal).Replace("+", "localhost", StringComparison.Ordinal);

var shelly = builder.Configuration.GetSection(ShellyOptions.SectionName).Get<ShellyOptions>() ?? new ShellyOptions();
builder.Services.AddSingleton(shelly);
builder.Services.AddSingleton(TimeProvider.System);

// The address, through the SDK's store beside the page's keys.
builder.Services.AddGleanvoltProviderSecrets(shelly.DataDirectory);
builder.Services.AddSingleton<ChargerAddressStore>();
builder.Services.AddHttpClient<ShellyRpcClient>();

var gleanvolt = new Uri(builder.Configuration["Gleanvolt:Endpoint"] ?? "http://localhost:8090");
var frameAncestors = "'self' " + ProviderPageFraming.Sources(gleanvolt, builder.Configuration["Gleanvolt:FrameAncestors"]);

builder.Services.AddGleanvoltChargerProvider<ShellyCharger>(options =>
{
    options.Endpoint = gleanvolt;
    options.Token = builder.Configuration["Gleanvolt:Token"]
        ?? throw new InvalidOperationException("Set Gleanvolt:Token to the token from Gleanvolt's Settings → External providers.");
    options.ProviderId = "shelly-topac";
    options.Name = "Shelly TopAC";
    options.PageUrl = new Uri(pageUrl);
});

// The charger the SDK commands is the one the page shows.
builder.Services.AddSingleton(provider => (ShellyCharger)provider.GetRequiredService<IChargerSource>());
builder.Services.AddHostedService<StatusReporter>();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// As the OpenWeather plugin: framed by Gleanvolt on another port, so the frame-ancestors policy below decides.
builder.Services.AddAntiforgery(options => options.SuppressXFrameOptionsHeader = true);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(Path.GetFullPath(shelly.DataDirectory), "keys")))
    .SetApplicationName("gleanvolt-plugin-shelly");

var app = builder.Build();

app.UseRequestLocalization(options =>
{
    options.SetDefaultCulture("en").AddSupportedCultures("en", "cs").AddSupportedUICultures("en", "cs");
    options.RequestCultureProviders = [new QueryStringRequestCultureProvider(), new CookieRequestCultureProvider()];
});

// Everything after it answers only a browser Gleanvolt sent, with a ticket: the page changes where a charger is.
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
