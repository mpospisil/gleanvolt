# Gleanvolt.Providers.Client

Write an external data provider for [Gleanvolt](https://github.com/mpospisil/gleanvolt). A provider is
a separate process that connects to Gleanvolt's web port and supplies data Gleanvolt would otherwise
get from a built-in source: the low tariff, the solar forecast, or the car's telemetry.

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddGleanvoltTariffProvider<MyTariff>(options =>
{
    options.Endpoint = new Uri("http://gleanvolt.local:8090");
    options.Token = builder.Configuration["Gleanvolt:Token"]!;   // made on Settings → External providers
    options.ProviderId = "my-tariff";
    options.Name = "My tariff";
});
builder.Build().Run();

sealed class MyTariff : ITariffSource
{
    public Task<TariffData> GetTariffAsync(TariffWelcome site, CancellationToken cancellationToken) => ...;
}
```

The SDK connects, retries with a backoff until Gleanvolt answers, says hello after every reconnect,
publishes once after every accepted hello, answers Gleanvolt's refresh requests, and waits longer
after a refusal. Every refusal and every problem Gleanvolt finds in the data is logged.

A provider that needs its owner (to sign in, enter an emailed code, pair a device) can serve a page of
its own, which Gleanvolt shows: set `options.PageUrl`, and guard the page with
`app.UseGleanvoltProviderPage()`, which admits only a browser Gleanvolt sent with a short-lived
ticket. The SDK references the ASP.NET Core shared framework for that, so a provider runs on the
ASP.NET Core runtime.

A car provider that reads the car on a clock can follow Gleanvolt's charging session:
`VehicleProviderConnection.Session` says whether one is open, and `SessionChanged` is raised when that
changes. Read more often while a session is open. With a Gleanvolt older than vehicle 1.2 it is
`null`; go by the car's own charge state then.

Gleanvolt shows a provider's status in each viewer's language: report it with a code from
`ProviderStatusCodes`, which Gleanvolt words itself, and the detail in `ProviderStatus.Messages`, one
text per language.

The protocol, the tokens and the limits on the data are described in
[docs/PROVIDERS.md](https://github.com/mpospisil/gleanvolt/blob/main/docs/PROVIDERS.md).

MIT licence. A provider built on it can use any licence.
