using Gleanvolt.Plugin.OpenWeather.Components;
using Gleanvolt.Plugin.OpenWeather.OpenWeather;
using Gleanvolt.Providers.Client;
using Gleanvolt.Providers.Client.Weather;
using Gleanvolt.Providers.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;

// OpenWeatherMap as an external weather provider for Gleanvolt (#424): a copy of the built-in OpenWeather
// integration, with its own page for the API key, which Gleanvolt shows on its Settings. Run it with
// "External" chosen as the weather source on Settings. See README.md.

var builder = WebApplication.CreateBuilder(args);

// User secrets however it is started, as the samples do; environment variables and the command line win.
builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

// Where the page listens, and the address the owner's browser opens it at. They differ when it listens on
// every interface (a container): Gleanvolt must be told an address a browser can reach.
var listen = builder.Configuration["Urls"] ?? "http://localhost:8096";
builder.WebHost.UseUrls(listen);
var pageUrl = builder.Configuration["Gleanvolt:PageUrl"]
    ?? listen.Split(';')[0].Replace("0.0.0.0", "localhost", StringComparison.Ordinal).Replace("+", "localhost", StringComparison.Ordinal);

var openWeather = builder.Configuration.GetSection(OpenWeatherOptions.SectionName).Get<OpenWeatherOptions>() ?? new OpenWeatherOptions();
builder.Services.AddSingleton(openWeather);
builder.Services.AddSingleton(TimeProvider.System);

// The key, through the SDK's secret store (#416): a 0600 file on Linux, DPAPI on Windows.
builder.Services.AddGleanvoltProviderSecrets(openWeather.DataDirectory);
builder.Services.AddSingleton<ApiKeyStore>();
builder.Services.AddHttpClient<OpenWeatherClient>(client => client.BaseAddress = new Uri(openWeather.BaseUrl, UriKind.Absolute));

var gleanvolt = new Uri(builder.Configuration["Gleanvolt:Endpoint"] ?? "http://localhost:8090");

// Who may show the page in a frame: Gleanvolt, at the address the browser opens it at. Its endpoint by
// default, or any name on its port when Gleanvolt is on this machine (#426); set Gleanvolt:FrameAncestors
// when the browser reaches Gleanvolt by another name.
var frameAncestors = "'self' " + ProviderPageFraming.Sources(gleanvolt, builder.Configuration["Gleanvolt:FrameAncestors"]);

builder.Services.AddGleanvoltWeatherProvider<OpenWeatherFeed>(options =>
{
    options.Endpoint = gleanvolt;
    options.Token = builder.Configuration["Gleanvolt:Token"]
        ?? throw new InvalidOperationException("Set Gleanvolt:Token to the token from Gleanvolt's Settings → External providers.");
    options.ProviderId = "openweather";
    options.Name = "OpenWeather";
    options.PageUrl = new Uri(pageUrl);
});

// The feed the SDK asks is the one the page shows.
builder.Services.AddSingleton(provider => (OpenWeatherFeed)provider.GetRequiredService<IWeatherSource>());
builder.Services.AddHostedService<StatusReporter>();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Antiforgery sends X-Frame-Options: SAMEORIGIN, and Gleanvolt on another port is another origin: the
// frame would stay empty. The frame-ancestors policy below says who may frame the page instead.
builder.Services.AddAntiforgery(options => options.SuppressXFrameOptionsHeader = true);

// Blazor's own keys (circuits, antiforgery), beside the key in the data directory rather than in a home
// directory a container forgets.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(Path.GetFullPath(openWeather.DataDirectory), "keys")))
    .SetApplicationName("gleanvolt-plugin-openweather");

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
