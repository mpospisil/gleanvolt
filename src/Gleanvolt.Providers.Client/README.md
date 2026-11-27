# Gleanvolt.Providers.Client

Write an external data provider for [Gleanvolt](https://github.com/mpospisil/gleanvolt). A provider is
a separate process that connects to Gleanvolt's web port and supplies data Gleanvolt would otherwise
get from a built-in source: the low tariff, the solar forecast, or the car's telemetry.

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddGleanvoltTariffProvider<MyTariff>(options =>
{
    options.Endpoint = new Uri("http://gleanvolt.local:8090");
    options.Token = builder.Configuration["Gleanvolt:Token"] ?? string.Empty;   // optional: empty, the provider pairs
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

`options.Token` is optional. A provider with no token does not stop: it finds Gleanvolt (the
configured `Endpoint`, then `http://localhost:8090`, then an mDNS browse for `_gleanvolt._tcp`), asks to be
paired, shows a six-digit code on its page and in its log, and gets the token while it runs when the owner
presses **Accept** in Gleanvolt, on Settings → External providers or in the setup Wizard. No restart, and
the owner never copies the token. The paired token and the address that accepted it are kept in
`IProviderSecretStore` (register it with `AddGleanvoltProviderSecrets`; without it the pairing lasts until
the provider restarts). A token in `options.Token` always wins and the provider never knocks; with no
`Endpoint` it keeps `http://localhost:8090`. A configured `Endpoint` also wins over the paired address, so
setting it is how a paired provider follows a Gleanvolt that moved.

Two classes carry it, both registered by the `AddGleanvolt*Provider` methods:
`ProviderCredentials` holds the token and the address in use (`Token`, `Endpoint`, `Source`:
`None`, `Configuration` or `Paired`, `Changed`, `WaitForTokenAsync`), and `ProviderPairing` is the
background service that knocks and collects, with `Status` (`PairingPhase`, the code, the Gleanvolt asked)
for a page to show. `GleanvoltFinder` is the search, replaceable through `IGleanvoltFinder`. A paired token
that Gleanvolt refuses (the owner made a new one) is forgotten and the provider waits to be paired again.
Anyone who can open Gleanvolt can accept, without its login; setting a password closes that. See
[Pairing](https://github.com/mpospisil/gleanvolt/blob/main/docs/PROVIDERS.md#pairing).

A provider that needs its owner (to sign in, enter an emailed code, pair a device) can serve a page of
its own, which Gleanvolt shows: set `options.PageUrl`, and guard the page with
`app.UseGleanvoltProviderPage()`, which admits only a browser Gleanvolt sent with a short-lived
ticket. The SDK references the ASP.NET Core shared framework for that, so a provider runs on the
ASP.NET Core runtime. `ProviderPageFraming.Sources(endpoint, configured)` gives the page's
`frame-ancestors`; `ProviderPageFraming.Sources(credentials, configured)` does the same for the Gleanvolt in
use, and allows any host on a paired address's port. On Gleanvolt's own machine, a `PageUrl` of `http://localhost:<port>/` is enough:
Gleanvolt sends the browser to it under the name the browser opened Gleanvolt at.

A car provider that reads the car on a clock can follow Gleanvolt's charging session:
`VehicleProviderConnection.Session` says whether one is open, and `SessionChanged` is raised when that
changes. Read more often while a session is open. With a Gleanvolt older than vehicle 1.2 it is
`null`; go by the car's own charge state then.

A provider that holds an account keeps it in `IProviderSecretStore`, registered with
`services.AddGleanvoltProviderSecrets(directory)`: owner-only (`0600`) files on Linux and macOS,
DPAPI-encrypted files on Windows, every write atomic.

Gleanvolt shows a provider's status in each viewer's language: report it with a code from
`ProviderStatusCodes`, which Gleanvolt words itself, and the detail in `ProviderStatus.Messages`, one
text per language.

A charger provider uses `AddGleanvoltChargerProvider<T>` and an `IChargerSource`. Test it without Gleanvolt
with `Gleanvolt.Providers.Testing`, a charger hub on a loopback port; talk to a Modbus charger with
`Gleanvolt.Modbus`. Both are MIT too.

The protocol, the tokens and the limits on the data are described in
[docs/PROVIDERS.md](https://github.com/mpospisil/gleanvolt/blob/main/docs/PROVIDERS.md).

MIT licence. A provider built on it can use any licence.
