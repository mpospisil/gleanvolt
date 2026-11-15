# External data providers

An external data provider is a program outside Gleanvolt that supplies data Gleanvolt would
otherwise get from a built-in source. It runs in its own process, on the same machine or another
one, connects to Gleanvolt's web port, and sends its data over a public, versioned contract. A
provider that crashes or hangs can only make its own data stale. It cannot slow the controller down.

There are three **kinds** of provider:

| Kind | Replaces | Hub | Turned on by |
| --- | --- | --- | --- |
| `tariff` | The tariff table | `/providers/tariff` | Choosing **External** under **Settings → Tariff** and saving |
| `solar-forecast` | Solcast | `/providers/solar-forecast` | Choosing **External** under **Settings → Solar forecast** and saving |
| `vehicle` | The car's built-in feeds (volkswagen.de, MyŠkoda, Data Act) | `/providers/vehicle` | Choosing **An external provider** on the **Car** page, saving and restarting |

Gleanvolt holds one active provider per kind. The owner sees each kind's session on Settings, and a
program can read it at [`GET /api/v1/providers`](HTTP_API.md).

## Writing one in .NET

Reference `Gleanvolt.Providers.Client`, which brings `Gleanvolt.Providers.Contracts` with it. Both
are MIT, so the provider can use any licence. A provider is an ordinary worker service:

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

Three complete providers send fixed data:
[`plugins/Gleanvolt.Plugin.TariffSample`](../plugins/Gleanvolt.Plugin.TariffSample) a tariff pattern,
[`plugins/Gleanvolt.Plugin.SolarForecastSample`](../plugins/Gleanvolt.Plugin.SolarForecastSample)
a sunny bell curve, and [`plugins/Gleanvolt.Plugin.VehicleSample`](../plugins/Gleanvolt.Plugin.VehicleSample)
a car charging from 40 % to 80 %, with a small page of its own. Don't run any of them against a live
installation: Gleanvolt acts on what they send.

[`plugins/Gleanvolt.Plugin.Volkswagen`](../plugins/Gleanvolt.Plugin.Volkswagen) is a real one: a copy
of the built-in volkswagen.de feed, run as a plugin, with its own page for the account and the sign-in
(#410). It is a test of moving a car integration out of Gleanvolt, not part of the release.

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
while one is connected.

**Refusals.** A refused hello comes back as a `Welcome` with `accepted: false`, a `reasonCode` and an
English `reason`, and Gleanvolt closes the connection. Wait before trying again: a refusal only
changes when somebody changes a setting. The SDK waits 5 minutes.

| `reasonCode` | Why |
| --- | --- |
| `disabled` | The owner hasn't turned the kind on. |
| `wrong-kind` | The connection was authenticated for another kind's hub. Not sent since one token serves every kind (#406). |
| `contract-major` | The provider speaks another major version of the contract. |
| `another-provider` | A different provider is connected for this kind. |
| `invalid` | The id or the name is missing or malformed. |

**Goodbye.** Before Gleanvolt closes a session itself, it sends `Goodbye(reasonCode, reason)`: the kind
was turned off (`disabled`), a newer connection of the same provider replaced this one (`replaced`),
or the owner made a new token (`token-replaced`), which closes every kind's session.

**Requests.** Gleanvolt never waits for a provider inside a call. When it wants fresh data, it sends a
request with an id, and the provider answers with an ordinary publish that quotes that id. A request
that gets no answer within 2 minutes shows as "did not answer" on Settings. It changes nothing else.

**Status.** `ReportStatus({ healthy, message, needsOwner })` tells the owner how the provider is
doing, for example that its own source is down. It is shown next to the session. Set `needsOwner`
with `healthy: false` when only the owner can fix it: an expired login, a consent screen, a code
sent by email. For the car, that shows the sign-in band on every page and sets the Home Assistant
**Car feed** entity to `NeedsOwner`. `needsOwner` was added in tariff and solar-forecast 1.1; an
older host ignores it.

## The provider's own page

A provider that needs the owner (to sign in to an account, enter a code sent by email, pair a device)
serves its own web page, and Gleanvolt shows it (#410). Gleanvolt draws nothing of it, and the protocol
carries no sign-in messages: the page is entirely the provider's.

**Saying where it is.** The hello's optional `pageUrl` is the page's address, absolute, `http` or
`https`. With the SDK, set `options.PageUrl`. An address that is not `http` or `https` is refused as
`invalid`. `pageUrl` was added in tariff 1.2, solar-forecast 1.2 and vehicle 1.1; an older host
ignores it, and an older provider simply doesn't send it.

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
- A page that wants to be framed must not send `X-Frame-Options: DENY` or a `frame-ancestors` that
  leaves Gleanvolt out. Gleanvolt sends no CSP today, so nothing on its side blocks the frame.
- In the frame, a page on another host than Gleanvolt's (another IP address, not just another port)
  is a third party. Browsers send its cookie there only over HTTPS (`SameSite=None; Secure`), so an
  `http` page on another host works for as long as the ticket does, and a reload inside the frame may
  answer `403`. Opening the page again from Gleanvolt brings a new ticket. On the same host, the
  cookie works over `http`.

## Contract versions

Each kind has a major and a minor version. A provider sends the version it was built for in its hello.
A different major is refused. A different minor is accepted: a host ignores what it doesn't know,
and the `Welcome` says which version the host speaks.

| Kind | Major | Minor |
| --- | --- | --- |
| `tariff` | 1 | 2 |
| `solar-forecast` | 1 | 2 |
| `vehicle` | 1 | 1 |

The minors grew with `pageUrl` in the hello (#410).

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

The method names: `Hello`, `ReportStatus` and `Publish` on the hub; `RefreshRequested`, `SiteChanged`
(solar forecast only), `VehicleChanged` (vehicle only) and `Goodbye` on the provider.
