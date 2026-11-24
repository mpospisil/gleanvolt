# External data providers

An external data provider is a program outside Gleanvolt that supplies data Gleanvolt would
otherwise get from a built-in source. It runs in its own process, on the same machine or another
one, connects to Gleanvolt's web port, and sends its data over a public, versioned contract. A
provider that crashes or hangs can only make its own data stale. It cannot slow the controller down.

There are five **kinds** of provider:

| Kind | Replaces | Hub | Turned on by |
| --- | --- | --- | --- |
| `tariff` | The tariff table | `/providers/tariff` | Choosing **External** under **Settings → Tariff** and saving |
| `solar-forecast` | Solcast | `/providers/solar-forecast` | Choosing **External** under **Settings → Solar forecast** and saving |
| `vehicle` | The car's built-in feeds (volkswagen.de, MyŠkoda, Data Act) | `/providers/vehicle` | Choosing **An external provider** on the **Car** page, saving and restarting |
| `weather` | The built-in OpenWeatherMap, removed in #425 | `/providers/weather` | Choosing **External** under **Settings → Weather** and saving |
| `charger` | The charger drivers built into Gleanvolt, removed in 1.4 (#445) | `/providers/charger` | Always on |

Gleanvolt holds one active provider per kind. The owner sees each kind's session on Settings, and a
program can read it at [`GET /api/v1/providers`](HTTP_API.md).

## Writing one in .NET

Reference `Gleanvolt.Providers.Client` from nuget.org, which brings `Gleanvolt.Providers.Contracts` with it.
Both are MIT, so the provider can use any licence. A provider is an ordinary worker service:

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddGleanvoltTariffProvider<MyTariff>(options =>
{
    options.Endpoint = new Uri("http://gleanvolt.local:8090");
    options.Token = builder.Configuration["Gleanvolt:Token"]!;
    options.ProviderId = "my-tariff";
    options.Name = "My tariff";
});
builder.Build().Run();

sealed class MyTariff : ITariffSource
{
    public Task<TariffData> GetTariffAsync(TariffWelcome site, CancellationToken cancellationToken) => ...;
}
```

The SDK connects with a backoff (2 seconds, doubling to 1 minute) until Gleanvolt answers, and does
the same whenever the connection is lost, for as long as the provider runs. So the provider can start
before Gleanvolt, and Gleanvolt can restart or be updated under it, with nothing to do on either side.
It says hello on every new connection, publishes once after every accepted hello, answers every
refresh request, and waits longer after a refusal.
To publish at other times, for example when day-ahead prices come out, resolve
`TariffProviderConnection` and call `PublishNowAsync`.

A solar forecast provider is the same, with `AddGleanvoltSolarForecastProvider<T>` and an
`ISolarForecastSource` whose `GetForecastAsync(PvSite site, …)` returns a `SolarForecastData`. Its
connection is `SolarForecastProviderConnection`.

A car provider uses `AddGleanvoltVehicleProvider<T>` and an `IVehicleSource` whose
`GetReadingAsync(VehicleInfo car, …)` returns a `VehicleReading`, or null when it has nothing yet. Its
connection is `VehicleProviderConnection`: call `PublishNowAsync` whenever the car reports something
new, since Gleanvolt doesn't poll a car provider.

A weather provider uses `AddGleanvoltWeatherProvider<T>` and an `IWeatherSource` whose
`GetReadingAsync(WeatherSite site, …)` returns a `WeatherData`, or null when it has nothing (no key yet,
the service down: say why with `ReportStatusAsync`). Its connection is `WeatherProviderConnection`.

A charger provider uses `AddGleanvoltChargerProvider<T>` and an `IChargerSource`. Its `Capabilities`
are the charger's current range and phases, sent in every hello (#440). `ReadAsync` returns a
`ChargerReading` (throw when the charger can't be read). `SetCurrentAsync(amps, …)` and
`SetChargingAllowedAsync(allowed, …)` carry out Gleanvolt's commands (throw when the charger refuses).
The SDK answers every request, says how each command ended, and runs one call to the charger at a time.
Its connection is `ChargerProviderConnection`.

Three complete providers send fixed data:
[`plugins/Gleanvolt.Plugin.TariffSample`](../plugins/Gleanvolt.Plugin.TariffSample) a tariff pattern,
[`plugins/Gleanvolt.Plugin.SolarForecastSample`](../plugins/Gleanvolt.Plugin.SolarForecastSample)
a sunny bell curve, and [`plugins/Gleanvolt.Plugin.VehicleSample`](../plugins/Gleanvolt.Plugin.VehicleSample)
a car charging from 40 % to 80 %, with a small page of its own. Don't run any of them against a live
installation: Gleanvolt acts on what they send.

[`plugins/Gleanvolt.Plugin.Volkswagen`](../plugins/Gleanvolt.Plugin.Volkswagen) is a real one: a copy
of the built-in volkswagen.de feed, run as a plugin, with its own page for the account and the sign-in
(#410). It is a test of moving a car integration out of Gleanvolt, not part of the release.

**The charger plugins live in a repository of their own**, [gleanvolt-chargers](https://github.com/mpospisil/gleanvolt-chargers) (#443), MIT, each
released on its own tag with an image and self-contained zips: the Shelly TopAC (moved from here, #429),
and SolaX, Huawei and go-e, ported from the drivers built into Gleanvolt. Each is a folder of its own there,
with its own solution and tests, laid out as a plugin in its own repository would be.

**A charger plugin in your own repository** (#443) references **`Gleanvolt.Chargers.Hosting`** (MIT, on
nuget.org with the SDK): the host, the page Gleanvolt frames and guards, the address and your own settings, a LAN
search, and the recording for testers. The plugin implements a `PluginCharger` (its capabilities, a read, the two
commands), a page in the shared `PluginFrame`, and a `Program.cs` that names it. Its README has a plugin in four
files. `Gleanvolt.Providers.Testing` adds `PluginHarness` and a `FakeModbusServer` to test one without Gleanvolt.

[`plugins/Gleanvolt.Plugin.OpenWeather`](../plugins/Gleanvolt.Plugin.OpenWeather) is another (#424):
OpenWeatherMap as a weather provider, with its own page for the key, which Gleanvolt shows under
**Settings → Weather**. It began as a copy of the built-in integration, and its README lists what
changed. It is the first [shipped plugin](#shipped-plugins) (#426), and since #425 the only way to get
OpenWeatherMap's weather: the built-in is gone.

**Testing a charger plugin without Gleanvolt** (#439). `Gleanvolt.Providers.Testing` (MIT) is a charger hub
on a loopback port that speaks the charger kind as Gleanvolt does. The plugin connects to it with the
ordinary SDK (`hub.Configure(options)` gives it the address and the token), and the test reads and commands
it:

```csharp
await using var hub = await ChargerTestHub.StartAsync();
// ... start the plugin with hub.Configure(options) ...
await hub.WaitForHelloAsync();
var reading = await hub.ReadAsync();          // RefreshRequested → Publish
var done = await hub.SetCurrentAsync(10);     // SetCurrent → CommandDone
```

Every reading is checked by `ChargerReadingRules` in the contracts, the same limits Gleanvolt refuses a
reading by, and a reading that breaks them fails the read with each problem listed. The hello is checked
as Gleanvolt checks it (#440): the capabilities against the hub's `Limits` by `ChargerLimitsRules`, and the
first plugin is remembered until `hub.Forget()`. `hub.LastRefusal` says why a hello was refused. Gleanvolt's own
safeguards (the threshold, the clamp, dry-run) are not in the test hub: they are Gleanvolt's, and a plugin
carries out what it is sent.

**Talking Modbus.** `Gleanvolt.Modbus` (MIT) is the Modbus TCP client Gleanvolt's own drivers use: one
request at a time, spaced, the connection torn down after any failed exchange, late replies read past, and
a reconnect backoff. A charger plugin for a Modbus charger uses it rather than NModbus directly.

**Versions.** The SDK packages carry Gleanvolt's version and are released with it, from the same tag.
Whether a plugin and a Gleanvolt work together is decided by the kind's contract major (below), not by the
package version: a plugin built against an older SDK keeps working with a newer Gleanvolt of the same
major, and the other way round.

**Secrets.** A provider that holds an account (a password, a session's cookies, an API key) keeps it
with `services.AddGleanvoltProviderSecrets(directory)` and `IProviderSecretStore` (#416): `Read`,
`Write` and `Delete` by name, one text value each. On Linux and macOS a value is a file readable and
writable by the provider's user alone (`0600`), in a directory only that user can enter (`0700`), and
is not encrypted. On Windows it is encrypted with DPAPI for the current user. Every write replaces the
old value in one step. A name is a plain file name (letters, digits, `.`, `-`, `_`), so no name reaches
outside the directory. `IsEncrypted` says which protection applies, so a provider can tell its owner.
Give the directory a volume of its own, and keep it out of backups and shares you wouldn't give the
password to.

## The token

Every provider connects with one token, made on Settings under **External providers** with **Make a
token**. Gleanvolt keeps it in the secret store, like the API keys, and Settings shows it masked, with
**Reveal** and **Copy**, to a signed-in owner. Without a web login it is never shown. A new token
disconnects every provider until each is given the new one.

The token is separate from the web password and the API keys. The hub a provider connects to decides
which kind's data it can publish; the token lets it connect to any of them. Before #406 each kind had
its own token: the first start after the upgrade keeps the tariff's (or, without one, the solar
forecast's) as the shared token, so a provider already set up keeps connecting.

The token goes in `Authorization: Bearer <token>`. A WebSocket from a browser-style client can't set
headers, so the token is also accepted as `access_token` in the query string. A wrong or missing
token gets `401` before any connection is made.

## The session

1. The provider connects to the kind's hub.
2. It calls `Hello` with its id, name, version and the contract version it was built for.
3. Gleanvolt answers with a `Welcome`. Until a hello is accepted, every other call on the connection
   is refused.
4. The provider publishes its data, whenever it likes and in reply to Gleanvolt's requests.
5. When the connection drops, or Gleanvolt isn't running yet or is restarting, the provider keeps
   trying, and says hello again once connected: a reconnect is a new connection to Gleanvolt.

**One session per kind.** The same provider id reconnecting replaces its old session, since a
reconnect can arrive before the old connection has timed out. A different provider id is refused
while one is connected. The charger kind goes further and remembers its plugin (see
[The charger kind](#the-charger-kind)).

**Refusals.** A refused hello comes back as a `Welcome` with `accepted: false`, a `reasonCode` and an
English `reason`, and Gleanvolt closes the connection. Wait before trying again: a refusal only
changes when somebody changes a setting. The SDK waits 5 minutes.

| `reasonCode` | Why |
| --- | --- |
| `disabled` | The owner hasn't turned the kind on. |
| `wrong-kind` | The connection was authenticated for another kind's hub. Not sent since one token serves every kind (#406). |
| `contract-major` | The provider speaks another major version of the contract. |
| `another-provider` | A different provider is connected for this kind. For the charger, also any plugin other than the one Gleanvolt remembers, connected or not. |
| `limits` | Charger 1.1: the charger can't do what `ChargeControl` is set to. The reason names the setting. |
| `invalid` | The id or the name is missing or malformed, or the charger's capabilities make no sense. |

**Goodbye.** Before Gleanvolt closes a session itself, it sends `Goodbye(reasonCode, reason)`: the kind
was turned off (`disabled`), a newer connection of the same provider replaced this one (`replaced`),
or the owner made a new token (`token-replaced`), which closes every kind's session. For the charger,
the owner pressed **Forget** (`forgotten`).

**Settings → External providers** shows the last refused hello of each kind since the start, with its
reason, until one is accepted. It is also in the API's `GET /api/providers` as `lastRefusal`.

**Requests.** Gleanvolt never waits for a provider inside a call. When it wants fresh data, it sends a
request with an id, and the provider answers with an ordinary publish that quotes that id. A request
that gets no answer within 2 minutes shows as "did not answer" on Settings. It changes nothing else.

**Status.** `ReportStatus({ healthy, message, needsOwner })` tells the owner how the provider is
doing, for example that its own source is down. It is shown next to the session. Set `needsOwner`
with `healthy: false` when only the owner can fix it: an expired login, a consent screen, a code
sent by email. For the car, that shows the sign-in band on every page and sets the Home Assistant
**Car feed** entity to `NeedsOwner`. `needsOwner` was added in tariff and solar-forecast 1.1; an
older host ignores it.

**The status in the viewer's language** (#415; tariff, solar-forecast and vehicle 1.3). One status
goes to every viewer, and Gleanvolt is in English or Czech for each of them, so a provider can't
translate it for whoever reads it. Say what happened with a `code`, which Gleanvolt words itself, and
put any detail in `messages`, one text per language:

```json
{
  "healthy": false, "needsOwner": true,
  "code": "needs-sign-in",
  "messages": { "en": "The account changed.", "cs": "Účet se změnil." },
  "message": "The account changed. Sign in again on the plugin's page."
}
```

| `code` | What Gleanvolt says (English) |
| --- | --- |
| `not-set-up` | the account isn't entered yet; enter it on the provider's page |
| `needs-sign-in` | sign in on the provider's page |
| `needs-code` | enter the code you were sent, on the provider's page |
| `unreachable` | its service can't be reached |
| `rate-limited` | its service is limiting requests; it tries again later |
| `refused` | its service refused the account or the request |
| `service-changed` | its service answered in a way it doesn't understand; it may need an update |

- **With a code Gleanvolt knows,** it shows its own words, then the detail from `messages` in the
  viewer's language. `message` isn't shown: it is the whole English sentence for an older Gleanvolt,
  which ignores `code` and `messages`.
- **Without one** (or with a code Gleanvolt doesn't know), it shows `messages` in the viewer's
  language, or `message`.
- `messages` is keyed by language tag: `en`, `cs`, or with a region, `cs-CZ`. Gleanvolt picks the
  viewer's tag, then its language, then `en`, then `message`. It keeps at most 16 languages of 1 000
  characters each, and drops a key that isn't a language tag or an empty text, without refusing the
  status.
- `GET /api/v1/providers` returns `statusCode` and `statusText` (the English as Gleanvolt words it)
  beside `statusMessage`.

## The provider's own page

A provider that needs the owner (to sign in to an account, enter a code sent by email, pair a device)
serves its own web page, and Gleanvolt shows it (#410). Gleanvolt draws nothing of it, and the protocol
carries no sign-in messages: the page is entirely the provider's.

**Saying where it is.** The hello's optional `pageUrl` is the page's address, absolute, `http` or
`https`. With the SDK, set `options.PageUrl`. An address that is not `http` or `https` is refused as
`invalid`. `pageUrl` was added in tariff 1.2, solar-forecast 1.2 and vehicle 1.1; an older host
ignores it, and an older provider simply doesn't send it.

**A provider on Gleanvolt's machine** (#426) may say `http://localhost:<port>/`. When it connected
from a loopback address and its `pageUrl` is a loopback one, Gleanvolt's redirect puts the host name the
browser opened Gleanvolt at in its place, keeping the page's port: `http://gleanvolt.local:8096/` for a
browser at `http://gleanvolt.local:8090`, `http://192.168.2.7:8096/` for one at the address. `localhost`
would be the viewer's own machine, and another name than Gleanvolt's makes the page a third party in the
frame (below). The status and the API still show the `pageUrl` as sent. A loopback `pageUrl` from a
provider on another machine is left alone: it means that machine's browser.

**Where the owner sees it.** For a car provider, the Car page shows the page in a frame under
*External provider*, with an **Open in a new tab** link beside it. **Settings → External providers**
links to the page of each kind's provider that has one. Both show only while the provider is
connected.

**The ticket.** The browser reaches the provider directly, so anyone on the network could open the
page's address, and on a page with a *Sign in* button, pressing it over and over is how a car account
gets locked. So the frame and the link point at Gleanvolt's own `/external/<kind>/page`, behind the
web login, which redirects to `pageUrl` with three query parameters added:

| Parameter | Value |
| --- | --- |
| `gvticket` | `<expiry>.<signature>`: the expiry in Unix seconds, and HMAC-SHA256 of `gleanvolt-page-ticket/v1\n<providerId>\n<expiry>` under the provider token, in base64url without padding. Good for 2 minutes. |
| `theme` | `auto` (follow the browser's `prefers-color-scheme`, as Gleanvolt does), `light` or `dark`. Gleanvolt has no theme switch today, so it sends `auto`. |
| `culture` | The viewer's language in Gleanvolt, e.g. `en` or `cs`. |

The ticket is never in a Gleanvolt page's HTML, and a ticket for one provider opens no other's page.
`ProviderPage.CreateTicket` and `VerifyTicket` in `Gleanvolt.Providers.Contracts` implement it for a
.NET provider.

**Checking it, with the SDK.** `app.UseGleanvoltProviderPage()` in the provider's ASP.NET Core app
guards everything after it. A request with a valid ticket is let through and given the provider's own
cookie, `gleanvolt-page-<providerId>`, good for 8 hours, so a reload works without Gleanvolt. A request
with that cookie is let through. Anything else gets `403`. The page needs no login of its own.

Some paths need no ticket, since nothing of the owner's is in them: files with an extension
(stylesheets, scripts, images) fetched with `GET`, and Blazor's `/_framework`, `/_content` and
`/_blazor`. The Blazor circuit is safe without one because an interactive component can only start from
markers the server rendered into a page, and that page needed the ticket. A provider whose page posts
forms keeps the ticket in the form's address, since the cookie may not be sent in the frame (below).

**Known limits:**

- The page's address must be reachable from the **browser**, not just from Gleanvolt. A provider in a
  container publishes its port to the LAN and sets `pageUrl` to the LAN address.
- If Gleanvolt is opened over HTTPS, an `http://` page is blocked as mixed content, in the frame and
  through the redirect. Use **Open in a new tab**, or serve the page over HTTPS too.
- A page that wants to be framed must not send `X-Frame-Options` or a `frame-ancestors` that leaves
  Gleanvolt out, and another port is another origin. ASP.NET Core sends both by default on a Blazor
  page (`SAMEORIGIN` from antiforgery, `frame-ancestors 'self'` from interactive server rendering):
  the Volkswagen and OpenWeather plugins turn off the first and set the second with the SDK's
  `ProviderPageFraming.Sources(endpoint, configured)`: Gleanvolt's address, or, for a Gleanvolt on the
  same machine, any host on Gleanvolt's port (`http://*:8090 https://*:8090`), since the browser may
  reach it by any name. Gleanvolt sends no CSP today, so nothing on its side blocks the frame.
- In the frame, a page on another host than Gleanvolt's (another IP address, not just another port)
  is a third party. Browsers send its cookie there only over HTTPS (`SameSite=None; Secure`), so an
  `http` page on another host works for as long as the ticket does, and a reload inside the frame may
  answer `403`. Opening the page again from Gleanvolt brings a new ticket. On the same host, the
  cookie works over `http`.

## Shipped plugins

Most plugins are built with the solution and run by hand. A **shipped** one is released with Gleanvolt
(#426), from the same tag and with the same version, wherever Gleanvolt is:

| Plugin | Kind | Image | Compose profile | Port | Release zip |
| --- | --- | --- | --- | --- | --- |
| [OpenWeather](../plugins/Gleanvolt.Plugin.OpenWeather) | weather | `ghcr.io/mpospisil/gleanvolt-plugin-openweather` | `openweather` | 8096 | `gleanvolt-plugin-openweather-<version>-<rid>.zip` |
| [SolaX](https://github.com/mpospisil/gleanvolt-chargers/tree/main/solax) | charger | `ghcr.io/mpospisil/gleanvolt-plugin-solax` | `solax` | 8098 | on its own release |
| [Huawei SCharger](https://github.com/mpospisil/gleanvolt-chargers/tree/main/huawei) | charger | `ghcr.io/mpospisil/gleanvolt-plugin-huawei` | `huawei` | 8099 | on its own release |
| [go-e](https://github.com/mpospisil/gleanvolt-chargers/tree/main/goe) | charger | `ghcr.io/mpospisil/gleanvolt-plugin-goe` | `goe` | 8100 | on its own release |
| [Shelly TopAC](https://github.com/mpospisil/gleanvolt-chargers/tree/main/shelly) | charger | `ghcr.io/mpospisil/gleanvolt-plugin-shelly` | `shelly` | 8097 | on its own release |

Only a plugin that replaces or stands in for a built-in ships. The samples and the Volkswagen copy don't. **The charger
plugins ship** (#444): they replaced the built-in charger drivers, which 1.4 removed (#445). They are released from gleanvolt-chargers on their own tags (`solax-v1.0.0`), each with its image and zips,
so a charger fix needs no Gleanvolt release; the compose files and the deploy scripts run them like the OpenWeather
plugin, at their own version (`SOLAX_PLUGIN_TAG`; unset is the newest). At most one charger plugin runs, and it is
the only way the controller reaches the charger.

- **In Docker** (the Pi, a NAS): the image, `linux/amd64` and `linux/arm64`, with Gleanvolt's tags:
  `latest`, the version, `sha-…`. The compose files run it under the plugin's profile, on the host's
  network beside the controller, with `Gleanvolt:Endpoint` at `http://localhost:<WEB_PORT>`, its data in
  `plugins/<name>` (uid 1654, mode 700), and the provider token from `GLEANVOLT_PROVIDER_TOKEN` in `.env`.
  On the Pi, `GLEANVOLT_PLUGINS=openweather` in `.env` turns it on; both deploy scripts pull and recreate
  it with the controller, from the same `IMAGE_TAG`, and remove a plugin taken off the list. See
  [deploy/README.md](../deploy/README.md#shipped-plugins).
- **Without Docker** (Windows, a Linux host with the `.deb` or a zip): a self-contained zip per platform,
  attached to the release beside Gleanvolt's, started by hand like Gleanvolt's zip. Not in the winget
  package and not in the `.deb`. The plugin's README has the commands.

Its page needs no address of its own in either case: the plugin says `http://localhost:<port>/`, and
Gleanvolt sends the browser to it under the name the browser opened Gleanvolt at
([above](#the-providers-own-page)).

A plugin's contract is Gleanvolt's at that version, so the two are upgraded together: an image pinned
to another version than the controller's may be refused at the hello (`contract-major`).

## Contract versions

Each kind has a major and a minor version. A provider sends the version it was built for in its hello.
A different major is refused. A different minor is accepted: a host ignores what it doesn't know,
and the `Welcome` says which version the host speaks.

| Kind | Major | Minor |
| --- | --- | --- |
| `tariff` | 1 | 3 |
| `solar-forecast` | 1 | 3 |
| `vehicle` | 1 | 3 |
| `weather` | 1 | 0 |
| `charger` | 1 | 1 |

The minors grew with `pageUrl` in the hello (#410). Vehicle 1.2 added the charging session (#414).
All three went up by one with the status's `code` and `messages` (#415). The weather kind (#424) and the
charger kind (#435) started at 1.0 with all of these. Charger 1.1 added the charger's capabilities in the
hello, the `limits` refusal and the remembered plugin (#440).

## The tariff kind

Hub: `/providers/tariff`.

**`Hello(hello)` → `TariffWelcome`.** On top of the common fields, the welcome carries the
installation's IANA `timeZone` (e.g. `Europe/Prague`) and its `currency`, the ISO 4217 code every
price must be in (`Tariff:Currency`).

**`Publish(data)` → `PublishResult`.** Replaces the tariff Gleanvolt holds, if the data passes every
check. If any check fails, nothing changes, and the result lists every problem in English.

```json
{
  "issuedAt": "2026-11-15T09:00:00Z",
  "inReplyTo": { "value": "6f1c…" },
  "currency": "CZK",
  "windows": [
    { "start": "2026-11-14T23:00:00Z", "end": "2026-11-15T05:00:00Z", "rate": "low",  "pricePerKwh": 2.10 },
    { "start": "2026-11-15T05:00:00Z", "end": "2026-11-15T12:00:00Z", "rate": "high", "pricePerKwh": null }
  ]
}
```

- `inReplyTo` quotes the request this answers, or is `null` when sent unprompted.
- `rate` is `low` or `high`. `pricePerKwh` is that window's own price, or `null` for the owner's
  shared low or high price from Settings.

The data must meet these rules:

- At least one window, and at most 1000.
- `currency` equals the welcome's `currency`.
- Every time is in UTC (`Z` or `+00:00`).
- Each window ends at least 5 minutes after it starts.
- Windows are in order and touch: each starts where the one before ends, with no gaps and no
  overlaps. Gleanvolt doesn't guess a rate for a gap.
- The windows cover the moment they are published, and end no more than 7 days ahead.
- Prices may be negative.
- Neighbouring windows on the same rate **at the same price** are joined, so sending hourly or
  quarter-hour slots is fine. Neighbours on the same rate **at different prices** are refused. Every
  tariff source in Gleanvolt promises that no two neighbours share a rate, so per-slot spot prices
  need that rule to change first.

**`RefreshRequested(request)`**, from Gleanvolt, asks for fresh data: answer with `Publish`, with
`inReplyTo` set to `request.id`. Gleanvolt asks once an hour while External is selected. Publish
unprompted whenever your data changes.

**What Gleanvolt does with it.** The windows become the tariff: the Overview's tariff line, the
`Low tariff`, `Tariff change` and `Grid price` entities in Home Assistant, `GET /api/v1/tariff`, and
the *low tariff only* options of the charging modes. They are saved in
`external-tariff.json` beside `tariff.json`, so a restart of Gleanvolt keeps them. After a disconnect
or a restart, the windows still count until the last one ends. After that, there is no tariff
restriction until the provider publishes again.

## The solar forecast kind

Hub: `/providers/solar-forecast`.

**`Hello(hello)` → `SolarForecastWelcome`.** On top of the common fields, the welcome carries the
installation as `site`, so it is not configured twice. Anything the owner has not entered is `null`.

```json
"site": {
  "latitude": 50.08, "longitude": 14.42,
  "installedKwp": 9.6,
  "azimuthDegrees": 180, "tiltDegrees": 35,
  "timeZone": "Europe/Prague"
}
```

`azimuthDegrees` is the compass bearing the array faces: 0 north, 90 east, 180 south, 270 west.
`tiltDegrees` is from horizontal.

**`Publish(data)` → `PublishResult`.** Replaces the forecast Gleanvolt holds, if the data passes every
check. If any check fails, nothing changes, and the result lists the problems in English.

```json
{
  "issuedAt": "2026-11-15T09:00:00Z",
  "inReplyTo": null,
  "periods": [
    { "end": "2026-11-15T09:30:00Z", "length": "00:30:00", "powerWatts": 2100, "powerWattsP10": 1400, "powerWattsP90": 2600 },
    { "end": "2026-11-15T10:00:00Z", "length": "00:30:00", "powerWatts": 2600 }
  ]
}
```

- A period is the expected **average** power over the `length` before its `end`, as Solcast stamps
  its periods. `length` is a .NET `TimeSpan`: `"hh:mm:ss"`.
- `powerWatts` is the median (P50). `powerWattsP10` and `powerWattsP90` are optional. Forecasted
  and Targeted plan their guarantees on P10, and use P50 where it is missing.
- `issuedAt` is when the forecast was made. Gleanvolt judges its age by it, as it judges Solcast's by
  the fetch: a forecast older than `ChargeControl:Forecast:StaleForecastAfter` is not planned on.

The data must meet these rules:

- At least one period, and at most 5000.
- Every time is in UTC (`Z` or `+00:00`).
- Each period is 5 to 60 minutes long.
- Periods are in order and don't overlap. Gaps are allowed, for example a night left out.
- Every power is a number of at least 0, and P10 ≤ P50 ≤ P90 where the bands are given.
- The last period ends after the moment of publishing, and no more than 14 days ahead.
- The highest P50 is at most 1.2 × the installed kWp, when Gleanvolt knows it.

Gleanvolt lists up to ten problems with single periods and counts the rest.

**`RefreshRequested(request)`**, from Gleanvolt, asks for a fresh forecast: answer with `Publish`,
with `inReplyTo` set to `request.id`. Gleanvolt asks on the forecast refresh schedule
(`Solcast:RefreshInterval`, 3 hours by default), not through the night, and once when External is
chosen. A request with no answer in 2 minutes shows on Settings. Publish unprompted whenever your
forecast changes.

**`SiteChanged(site)`**, from Gleanvolt, says the installation changed while connected. The
installation is read at start, so for now a change arrives as a new welcome after the restart.

**What Gleanvolt does with it.** The forecast replaces Solcast's for everything: the Forecasted and
Targeted plans, the Overview's forecast table and the Forecast page, the forecast entities in Home
Assistant, and `GET /api/v1/forecast`. The line under the Overview's table and `forecast` in
`GET /api/v1/status` name where it came from. While External is selected, Solcast is not called.
After a disconnect the last forecast stays in use, and the Overview says it is from a disconnected
provider. There is no automatic fallback to Solcast. Unlike the tariff, the forecast is not kept
across a restart of Gleanvolt: the provider's publish after its hello replaces it.

## The vehicle kind

Hub: `/providers/vehicle`. A program outside Gleanvolt reads the car and publishes what it says, so any
car can be read without new code in Gleanvolt: a Tesla over BLE, a Hyundai or Kia cloud, an OBD
dongle, a provider's own scraper. It takes the place of the built-in feeds: an installation reads its
car from one feed, so External can't be combined with `Vehicle:Website` or `Vehicle:Skoda` (startup
refuses), and sets the Data Act portal aside. The owner's MQTT topic may still run next to it.

Turned on by `Vehicle:External:Enabled`, which the Car page writes. Like every feed choice, it takes a
restart.

**`Hello(hello)` → `VehicleWelcome`.** On top of the common fields, the welcome carries the car as
Gleanvolt knows it, as `car`. A provider that serves one of several cars on an account picks the right
one by it. Anything the owner hasn't entered is empty or `null`.

```json
"car": {
  "id": "id4", "name": "Rosie",
  "make": "Volkswagen", "model": "ID.4",
  "batteryCapacityKWh": 77,
  "timeZone": "Europe/Prague"
}
```

The pack (capacity, efficiency, phases, currents) stays in Gleanvolt's `Ev:Vehicles[0]`. A provider
never supplies it.

From vehicle 1.2 the welcome also carries `session`: whether Gleanvolt has a charging session open
as the provider says hello. It is `null` from an older Gleanvolt.

```json
"session": { "open": true, "since": "2026-11-16T22:53:55+00:00", "mode": "FastNoBattery" }
```

**`Publish(reading)` → `PublishResult`.** Offers what the car last said about itself. It is stored if
it passes every check; if any check fails, nothing changes, and the result lists the problems in
English.

```json
{
  "capturedAt": "2026-11-15T10:12:40+01:00",
  "inReplyTo": null,
  "socPercent": 64,
  "rangeMetres": 281000,
  "chargeTimeRemaining": "01:30:00",
  "chargeState": "charging",
  "plugState": "connected"
}
```

- `capturedAt` is required, with its offset. It is when **the car** produced the reading, not when the
  provider sent it: Gleanvolt judges the reading's age by it.
- Everything else is optional, and absent means unknown, never zero. `chargeState` is `unknown`,
  `idle`, `charging` or `complete`; `plugState` is `unknown`, `disconnected` or `connected`.
  `chargeTimeRemaining` is a .NET `TimeSpan`: `"hh:mm:ss"`.

The reading must meet these rules:

- `capturedAt` is no more than 5 minutes ahead of Gleanvolt's clock and no older than 30 days.
- `capturedAt` isn't older than the reading Gleanvolt holds, from any feed. The same time is
  accepted: a car that hasn't reported since is a valid answer to a refresh.
- `socPercent` is 0 to 100, `rangeMetres` 0 to 2 000 000 (2 000 km), `chargeTimeRemaining` 0 to 48
  hours.
- `chargeState` and `plugState` are values Gleanvolt knows.
- At least one of `socPercent`, `rangeMetres`, `chargeState` or `plugState` is there.

**Push, not poll.** Publish whenever the car has something new: plugging in, a charge starting or
ending, a step in SOC. Gleanvolt doesn't ask on a clock.

**`RefreshRequested(request)`**, from Gleanvolt, comes only when somebody presses *Ask the car* or a
plan wants a fresh reading. Answer with `Publish`, with `inReplyTo` set to `request.id`, within 60
seconds. Gleanvolt waits that long, on its own side, and then shows the feed as *didn't answer* and
keeps the last reading.

**`VehicleChanged(car)`**, from Gleanvolt, says the car changed while connected. The car is read at
start, so for now a change arrives as a new welcome after the restart.

**`ChargingSessionChanged(session)`**, from Gleanvolt (vehicle 1.2, #414), says a charging session
opened or closed, or an open session's mode changed. A session is open while a charge mode other than
Off runs, the charger sees the car, and the mode hasn't ended. The built-in car feeds use the same
rule. `since` is when Gleanvolt saw it open; `mode` is the mode's name, for logs and display. New
modes appear without a contract change, so don't branch on it. Closed is `{"open": false}`.

It is sent only on a change, and once more when a provider attaches, so the state is never missed
between the welcome and the attach. It goes only to a provider that said hello with vehicle 1.2 or
later.

What it is for: **a provider that reads the car on a clock reads more often while a session is
open.** The car's own `charging` arrives only once the car has reported it, often minutes after the
plug-in. The built-in volkswagen.de feed reads every 5 minutes while a session is open and every 15
otherwise. The Volkswagen plugin does the same, also reads at once when a session opens, and falls
back to the car's own charge state with an older Gleanvolt. In the .NET SDK,
`VehicleProviderConnection.Session` holds it, and `SessionChanged` is raised when it changes.

**Read-only.** The kind carries no commands to the car: no start or stop, no target SOC, no wake-up.

**What Gleanvolt does with it.** The reading goes where every feed's goes: the Overview's vehicle card
("via" the provider's name), *Ask the car*, the Health page, the Home Assistant vehicle entities and
**Car feed**, `GET /api/v1/vehicle`, and the conversion of a percentage target. `vehicleSource` in
`GET /api/v1/status` says `external` and the provider's name. The reading stays advisory: nothing that
writes to the charger or the inverter reads it, and `Vehicle:MaxAge` and the stale refusal for
percentage targets apply unchanged.

The feed's health, as the Overview, Health and **Car feed** show it:

| Situation | Health |
| --- | --- |
| Connected, and the last reading was accepted | `Ok` |
| Connected, nothing sent yet; or the last reading was refused | `Degraded` |
| The provider didn't answer a refresh within 60 seconds | `Degraded`, until its next accepted reading |
| `ReportStatus` with `healthy: false` | `Degraded` |
| `ReportStatus` with `healthy: false, needsOwner: true` | `NeedsOwner` |
| Waiting for a provider, or disconnected | `Degraded`; the last reading stays and ages |

There is no fallback to another feed while the provider is away.

## The weather kind

Hub: `/providers/weather`. A program outside Gleanvolt reads the weather at the site and publishes it.
It is the **current conditions**, not a forecast: Gleanvolt records them on a charging session when the
session opens and when it closes, and nothing in charge control reads them. Gleanvolt has no weather source of its
own (#425). The OpenWeather plugin (`plugins/Gleanvolt.Plugin.OpenWeather`) is the reference provider,
and the one released with Gleanvolt: OpenWeatherMap, with its own page for the key.

**`Hello(hello)` → `WeatherWelcome`.** On top of the common fields, the welcome carries where the site
is, as `site`, so the location is not configured twice. `latitude` and `longitude` are `null` when the
owner has not entered them: report `not-set-up` rather than guess a place.

```json
"site": { "latitude": 50.08, "longitude": 14.42, "timeZone": "Europe/Prague" }
```

**`Publish(data)` → `PublishResult`.** Offers a reading. It is held if it passes every check. If any
check fails, nothing changes, and the result lists the problems in English.

```json
{
  "observedAt": "2026-11-17T08:50:00+00:00",
  "inReplyTo": null,
  "temperatureCelsius": 4.5,
  "pressurePascals": 101300,
  "humidityPercent": 80,
  "cloudsPercent": 40,
  "visibilityMetres": 10000,
  "condition": "Clouds",
  "conditionDescription": "scattered clouds",
  "sunrise": "2026-11-17T06:05:00+00:00",
  "sunset": "2026-11-17T15:12:00+00:00"
}
```

- Everything but `observedAt` is optional. Absent means unknown, never zero: a session records a
  missing cloud cover as unknown, not as a clear sky.
- **SI units**: °C, Pa (hPa × 100), %, metres. There is no unit option.
- `observedAt` is when the weather service observed it, not when the provider sent it.
- `condition` and `conditionDescription` are in English, as the service gives them. Gleanvolt shows
  them as they are.
- `sunrise` and `sunset` are for the site's day.

The data must meet these rules:

- `observedAt` is no more than 5 minutes ahead of Gleanvolt's clock and no older than 3 hours.
- It is not older than the reading Gleanvolt holds. The same time is accepted: a service that has
  not observed again since is a valid answer.
- Temperature −90 to 60 °C. Pressure 85 000 to 110 000 Pa. Humidity and cloud cover 0 to 100 %.
  Visibility 0 to 100 km.
- `condition` at most 100 characters, `conditionDescription` at most 200.
- `sunset` is after `sunrise` when both are given.
- At least a temperature or a condition.

**`RefreshRequested(request)`**, from Gleanvolt, asks for the weather now: answer with `Publish`, with
`inReplyTo` set to `request.id`, within 15 seconds (`Weather:ExternalRequestTimeout`). Gleanvolt asks
when a charging session opens or closes, and when `GET /api/v1/forecast?weather=true` is called, unless
it holds a reading it received in the last 10 minutes (`Weather:ExternalReadingMaxAge`). A provider that
read the weather a few minutes ago may answer with that reading: the OpenWeather plugin reads
OpenWeatherMap at most once every 10 minutes. A request with no answer shows on Settings.

**`SiteChanged(site)`**, from Gleanvolt, says the site moved while connected. The location is read at
start, so for now a change arrives as a new welcome after the restart.

**What Gleanvolt does with it.** The reading is what a session is recorded with: `WeatherAtStart`,
`WeatherAtEnd`, sunrise and sunset, on the Sessions pages and in `GET /api/v1/sessions`.
`weatherSource` in `GET /api/v1/status` says `external`, and `weatherConfigured` is `true` whether or
not the provider is connected. A provider that is disconnected, silent or refused means a session
recorded without the weather; there is nothing to fall back to.

## The charger kind

Hub: `/providers/charger` (#435). A program outside Gleanvolt **is** the EV charger's driver: it reads the
charger when asked and carries out Gleanvolt's two commands, so a charger Gleanvolt has no driver for can
be controlled without a new Gleanvolt. Unlike every other kind, this one writes to a device. Always on: since
1.4 (#445) every charger is behind a plugin, and Gleanvolt has no address for it; the plugin has it.

**Gleanvolt keeps the safeguards on its side.** The driver contract's rules hold whatever the plugin does:

- A setpoint is sent only when it moved by `ChargeControl:CurrentChangeThresholdAmps` from what the last
  reading said. It is never above the hello's `maxCurrentAmps`, and never above a reading's `maxCurrentAmps`
  when the reading gives one.
- In `ChargeControl:DryRun` **no command is sent**. What would have been sent stands in for the charger's
  own values, as for every driver. The welcome says `dryRun`, for the plugin's page.
- A read that isn't answered in time, or is refused, is a charger Gleanvolt couldn't read: the poll
  reports **unknown**, never a car that is gone, and the control loop makes no decision on it.

**`Hello(hello)` → `ChargerWelcome`.** Since 1.1 (#440) the hello carries what the charger can do, in
`charger`:

```json
{
  "providerId": "huawei-scharger",
  "name": "Huawei SCharger 7KS",
  "version": "1.0.0",
  "contractMajor": 1,
  "contractMinor": 1,
  "charger": { "minCurrentAmps": 6, "maxCurrentAmps": 32, "phases": 1, "pauseCurrentAmps": 0, "canSwitchPhases": false }
}
```

`minCurrentAmps` 1 to 80, `maxCurrentAmps` from the minimum to 80, `phases` 1 to 3, `pauseCurrentAmps` 0
or more and below the minimum. Anything else is refused as `invalid`. A 1.0 plugin, which sends no
`charger`, is taken as 6 to 32 A on three phases, as 1.0 assumed.

Gleanvolt checks `ChargeControl` against it before it accepts the hello, by the rule it checks a
built-in driver by at startup. A minimum below the charger's, a maximum above it, or more phases than it
supplies is refused with **`limits`**, and the reason names the setting:
`ChargeControl:Phases (3) is more than the Huawei SCharger 7KS charger supplies (1).` Change the setting
(or the plugin's capabilities) and let the plugin reconnect. `ChargerLimitsRules` in the contracts words
the same check.

**The first plugin is remembered.** The first charger plugin Gleanvolt accepts is remembered in the
overrides file as `ExternalCharger:PluginId`. From then on any other plugin id is refused with
`another-provider`, whether the remembered one is connected or not: a second plugin started by mistake,
a sample, must not write to the charger. The same id reconnecting is accepted as always. To change
plugins, stop the old one and press **Forget** beside it on **Settings → External providers**. That
closes its session if it is still connected, and the next plugin to say hello is remembered. A refused
plugin tries again 5 minutes later with the SDK's defaults. Setting `ExternalCharger__PluginId` in the
environment pins a plugin; Forget then lasts only until the next start.

The `ChargerWelcome` adds to the common fields: the charge control's `limits` (`minCurrentAmps`,
`maxCurrentAmps`, `phases`, `nominalVoltage`), `dryRun`, and the installation's `timeZone`.

**`RefreshRequested(request)`**, from Gleanvolt, asks for the charger's state now: on every poll of the
control loop, every few seconds while it runs. Answer with `Publish`, with `inReplyTo` set to
`request.id`, within 5 seconds (`ExternalCharger:ReadTimeout`). Read the charger for each request; don't
answer from a cache. Publish unprompted too, for example when a car is plugged in. A reading received in
the last 2 seconds (`ExternalCharger:ReadingMaxAge`) serves the next read too, unless a command was sent
since.

**`Publish(reading)` → `PublishResult`.**

```json
{
  "readAt": "2026-11-22T10:15:02+01:00",
  "inReplyTo": { "value": "6f1c…" },
  "status": "charging",
  "powerWatts": 6900,
  "currentAmps": 10,
  "chargingAllowed": true,
  "maxCurrentAmps": 16,
  "mode": null
}
```

- `status` is OCPP 1.6's: `unknown`, `available`, `preparing`, `charging`, `suspendedEv`, `suspendedEvse`,
  `finishing`, `faulted`, `unavailable`. **`suspendedEv` and `finishing` mean the car is done**, and are
  read that way. A charger that stopped delivering because of a pause or a stop, Gleanvolt's or its own,
  is `suspendedEvse`.
- `powerWatts` is what it delivers, all phases together, in **watts**.
- `currentAmps` is the active setpoint as Gleanvolt last set it: **0 while the plugin holds a pause**.
  `chargingAllowed` is whether the charger is in the state a start puts it in, a pause under a start
  included. Without either, Gleanvolt makes no decision that needs it.
- `maxCurrentAmps` is the highest current the charger takes now, when it says.
- `mode` is the charger's own name for its mode, for display only.

The rules: `readAt` no more than 5 minutes ahead of Gleanvolt's clock and no older than 1 minute; a known
`status`; `powerWatts` 0 to 100 000; `currentAmps` 0 to 80; `maxCurrentAmps` 6 to 80; `mode` at most 50
characters.

**`SetCurrent(command)`**, from Gleanvolt: `{ "id": {…}, "amps": 10, "reason": "…" }`. Below the charger's
minimum (0, in practice) is a **pause**: the session stays open. A charger that takes nothing below 6 A
pauses some other way (its start/stop switch) and remembers it, so the next setpoint of 6 A or more
resumes, the setpoint first.

**`SetChargingAllowed(command)`**, from Gleanvolt: `{ "id": {…}, "allowed": false, "reason": "…" }`. Lets
the charger charge, or stops it, whatever it was doing. It never touches the setpoint, and a start while
paused stays paused.

**`CommandDone(result)`**, to Gleanvolt, after every command: `{ "inReplyTo": {…}, "succeeded": true }`,
or `"succeeded": false` with an English `error`. Gleanvolt waits 10 seconds for it
(`ExternalCharger:CommandTimeout`); a command not answered by then failed. Carry out commands one at a
time and in order: a stop must not overtake the setpoint before it.

**Status.** Report anything that keeps the plugin from controlling the charger with `ReportStatus`, with
`needsOwner` when only the owner can fix it: no address, a password the plugin can't send, a setting on
the charger that fights the control loop.

**When the charger is unavailable** (#441). No plugin connected, a refused hello, `healthy: false`, or reads
that fail make the charger unavailable once it has lasted 2 minutes (`ChargeControl:ChargerUnavailableAfter`).
Gleanvolt then suspends a running mode, releases the battery hold, and starts no mode until the charger is
back, when the mode resumes by itself. So report `healthy: false` only for something that stops the charger
being controlled, and `healthy: true` again as soon as it is fixed. See
[When the charger is unavailable](CHARGE_CONTROL.md#when-the-charger-is-unavailable).

**What Gleanvolt does with it.** Everything: the Overview, the charging modes, sessions, Home Assistant. **Settings → External providers** shows the session, the remembered
plugin with **Forget**, the last refusal, and links to the plugin's page. There is no register snapshot.

## The protocol, for a client in another language

The hubs are [ASP.NET Core SignalR](https://learn.microsoft.com/aspnet/core/signalr/introduction)
hubs on Gleanvolt's web port, using the JSON hub protocol. Any SignalR client library works: there
are official ones for JavaScript and Java, and community ones for Python, Go and others. Without one:

1. **Negotiate.** `POST http://<host>:8090/providers/tariff/negotiate?negotiateVersion=1` with the
   `Authorization: Bearer <token>` header. The answer contains a `connectionToken`.
2. **Connect.** Open a WebSocket to
   `ws://<host>:8090/providers/tariff?id=<connectionToken>&access_token=<token>`.
3. **Handshake.** Send `{"protocol":"json","version":1}` followed by the record separator `0x1E`.
   The host answers `{}` + `0x1E`.
4. **Messages.** Every message is a JSON object followed by `0x1E`.
   - Call a hub method:
     `{"type":1,"invocationId":"1","target":"Hello","arguments":[{"providerId":"my-tariff","name":"My tariff","version":"1.0","contractMajor":1,"contractMinor":0}]}`
   - Its result comes back as `{"type":3,"invocationId":"1","result":{…}}`, or with `"error"` instead
     of `"result"`.
   - Gleanvolt calls the provider with `{"type":1,"target":"RefreshRequested","arguments":[{"id":{"value":"…"},"requestedAt":"…"}]}`,
     and `Goodbye` the same way, with no `invocationId`: no answer is expected.
   - Pings are `{"type":6}`. Send one at least every 15 seconds while idle, and expect one from the
     host. A connection that hears nothing for 30 seconds is closed.

Property names are camelCase. Enums are strings (`"low"`), though numbers are read too. Times are
ISO 8601. One message may be up to 1 MiB; a larger one closes the connection.

The hello may carry `"pageUrl":"http://…"`; see [the provider's own page](#the-providers-own-page).
A provider in another language checks the ticket itself, as described there.

The method names: `Hello`, `ReportStatus`, `Publish` and `CommandDone` (charger only) on the hub;
`RefreshRequested`, `SiteChanged` (solar forecast and weather), `VehicleChanged` and
`ChargingSessionChanged` (vehicle only), `SetCurrent` and `SetChargingAllowed` (charger only) and `Goodbye`
on the provider.
