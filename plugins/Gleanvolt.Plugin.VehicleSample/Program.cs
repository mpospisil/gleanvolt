using Gleanvolt.Plugin.VehicleSample;
using Gleanvolt.Providers.Client.Vehicle;

// A sample car provider for Gleanvolt (#408). It reports a pretend car charging from 40 % to 80 %.
// Do not run it against a live installation with the external provider chosen as the car's feed: a
// percentage target converts from this fake SOC. See README.md.

// A switch without a value, which the configuration's command-line reader would not take as one.
const string LoginExpiry = "--simulate-login-expiry";
var simulateLoginExpiry = args.Contains(LoginExpiry);
args = [.. args.Where(arg => arg != LoginExpiry)];

var builder = Host.CreateApplicationBuilder(args);

// The host reads user secrets only in Development, and not every debug launcher sets that. Read them
// always, so a token stored with `dotnet user-secrets set Gleanvolt:Token gvp_...` is found however the
// sample is started. Command-line arguments and environment variables still take precedence.
builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new SampleOptions(simulateLoginExpiry));
builder.Services.AddGleanvoltVehicleProvider<SimulatedCar>(options =>
{
    options.Endpoint = new Uri(builder.Configuration["Gleanvolt:Endpoint"] ?? "http://localhost:8090");
    options.Token = builder.Configuration["Gleanvolt:Token"]
        ?? throw new InvalidOperationException("Set Gleanvolt:Token to the token from Settings → External providers.");
    options.ProviderId = "vehicle-sample";
    options.Name = "Vehicle sample";
});

// The same car the provider reports, for the publisher to step.
builder.Services.AddSingleton(provider => (SimulatedCar)provider.GetRequiredService<IVehicleSource>());
builder.Services.AddHostedService<StepPublisher>();

builder.Build().Run();
