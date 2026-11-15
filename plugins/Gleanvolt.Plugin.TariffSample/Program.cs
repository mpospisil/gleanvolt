using Gleanvolt.Providers.Client.Tariff;
using Gleanvolt.Plugin.TariffSample;

// A sample tariff provider for Gleanvolt (#401). It sends the same fake pattern every time.
// Do not run it against a live installation with External selected: the low-tariff-only options and
// Targeted would follow its windows. See README.md.

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddGleanvoltTariffProvider<SampleTariff>(options =>
{
    options.Endpoint = new Uri(builder.Configuration["Gleanvolt:Endpoint"] ?? "http://localhost:8090");
    options.Token = builder.Configuration["Gleanvolt:Token"]
        ?? throw new InvalidOperationException("Set Gleanvolt:Token to the token from Settings → Tariff → External.");
    options.ProviderId = "tariff-sample";
    options.Name = "Tariff sample";
});
builder.Services.AddHostedService<MidnightPublisher>();

builder.Build().Run();
