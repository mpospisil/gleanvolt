using Gleanvolt.Plugin.SolarForecastSample;
using Gleanvolt.Providers.Client.SolarForecast;

// A sample solar forecast provider for Gleanvolt (#388). It sends the same fake curve every time.
// Do not run it against a live installation with External selected: Forecasted and Targeted would
// plan the car's charging by a fake forecast. See README.md.

var builder = Host.CreateApplicationBuilder(args);

// The host reads user secrets only in Development, and not every debug launcher sets that. Read them
// always, so a token stored with `dotnet user-secrets set Gleanvolt:Token gvp_...` is found however the
// sample is started. Command-line arguments and environment variables still take precedence.
builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddGleanvoltSolarForecastProvider<SampleForecast>(options =>
{
    options.Endpoint = new Uri(builder.Configuration["Gleanvolt:Endpoint"] ?? "http://localhost:8090");
    options.Token = builder.Configuration["Gleanvolt:Token"]
        ?? throw new InvalidOperationException("Set Gleanvolt:Token to the token from Settings → External providers.");
    options.ProviderId = "solar-forecast-sample";
    options.Name = "Solar forecast sample";
});
builder.Services.AddHostedService<HourlyPublisher>();

builder.Build().Run();
