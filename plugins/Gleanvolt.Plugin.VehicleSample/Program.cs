using Gleanvolt.Plugin.VehicleSample;
using Gleanvolt.Providers.Client;
using Gleanvolt.Providers.Client.Vehicle;

// A sample car provider for Gleanvolt (#408). It reports a pretend car charging from 40 % to 80 %, and
// serves a small page Gleanvolt shows on its Car page (#410). Do not run it against a live installation
// with the external provider chosen as the car's feed: a percentage target converts from this fake SOC.
// See README.md.

// A switch without a value, which the configuration's command-line reader would not take as one.
const string LoginExpiry = "--simulate-login-expiry";
var simulateLoginExpiry = args.Contains(LoginExpiry);
args = [.. args.Where(arg => arg != LoginExpiry)];

var builder = WebApplication.CreateBuilder(args);

// The host reads user secrets only in Development, and not every debug launcher sets that. Read them
// always, so a token stored with `dotnet user-secrets set Gleanvolt:Token gvp_...` is found however the
// sample is started. Command-line arguments and environment variables still take precedence.
builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

// The page: where the sample listens, and the address the owner's browser opens it at. The two differ
// when the sample listens on every interface: Gleanvolt is told the address a browser can reach.
var listen = builder.Configuration["Urls"] ?? "http://localhost:8096";
builder.WebHost.UseUrls(listen);
var pageUrl = builder.Configuration["Gleanvolt:PageUrl"] ?? listen.Split(';')[0].Replace("0.0.0.0", "localhost", StringComparison.Ordinal).Replace("+", "localhost", StringComparison.Ordinal);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new SampleOptions(simulateLoginExpiry));
builder.Services.AddGleanvoltVehicleProvider<SimulatedCar>(options =>
{
    options.Endpoint = new Uri(builder.Configuration["Gleanvolt:Endpoint"] ?? "http://localhost:8090");
    // Optional (#464, #470): without it the provider asks to be paired, and the owner accepts it on Settings → Plugins.
    options.Token = builder.Configuration["Gleanvolt:Token"] ?? string.Empty;
    options.ProviderId = "vehicle-sample";
    options.Name = "Vehicle sample";
    options.PageUrl = new Uri(pageUrl);
});

// The same car the provider reports, for the publisher to step and the page to show.
builder.Services.AddSingleton(provider => (SimulatedCar)provider.GetRequiredService<IVehicleSource>());
builder.Services.AddSingleton<SampleLogin>();
builder.Services.AddHostedService<StepPublisher>();

var app = builder.Build();

// Everything after it answers only a browser Gleanvolt sent, with a ticket.
app.UseGleanvoltProviderPage();
app.MapSamplePage();

app.Run();
