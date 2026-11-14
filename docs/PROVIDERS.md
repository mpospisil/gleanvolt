# External data providers

An external data provider is a program outside Gleanvolt that supplies data Gleanvolt would
otherwise get from a built-in source. It runs in its own process, on the same machine or another
one, connects to Gleanvolt's web port, and sends its data over a public, versioned contract. A
provider that crashes or hangs can only make its own data stale. It cannot slow the controller down.

There is one **kind** of provider so far:

| Kind | Replaces | Hub | Turned on by |
| --- | --- | --- | --- |
| `tariff` | The tariff table | `/providers/tariff` | Choosing **External** under **Settings → Tariff** and saving |

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

The SDK connects with a backoff until Gleanvolt answers, says hello after every reconnect, publishes
once after every accepted hello, answers every refresh request, and waits longer after a refusal.
To publish at other times, for example when day-ahead prices come out, resolve
`TariffProviderConnection` and call `PublishNowAsync`.

[`samples/Gleanvolt.Samples.TariffMock`](../samples/Gleanvolt.Samples.TariffMock) is a complete
provider that sends a fixed pattern. Don't run it against a live installation: Gleanvolt acts on
what it sends.

## Tokens

Each kind has its own token, made on Settings with **Make a token**. It is shown once. Gleanvolt keeps
only its SHA-256 hash, in the secret store, so a lost token is replaced rather than shown again. A
new token disconnects the provider that used the old one.

A token is separate from the web password and the API keys. It can only publish its own kind's data.

The token goes in `Authorization: Bearer <token>`. A WebSocket from a browser-style client can't set
headers, so the token is also accepted as `access_token` in the query string. A wrong or missing
token gets `401` before any connection is made.

## The session

1. The provider connects to the kind's hub.
2. It calls `Hello` with its id, name, version and the contract version it was built for.
3. Gleanvolt answers with a `Welcome`. Until a hello is accepted, every other call on the connection
   is refused.
4. The provider publishes its data, whenever it likes and in reply to Gleanvolt's requests.
5. When the connection drops, the provider reconnects and says hello again: a reconnect is a new
   connection to Gleanvolt.

**One session per kind.** The same provider id reconnecting replaces its old session, since a
reconnect can arrive before the old connection has timed out. A different provider id is refused
while one is connected.

**Refusals.** A refused hello comes back as a `Welcome` with `accepted: false`, a `reasonCode` and an
English `reason`, and Gleanvolt closes the connection. Wait before trying again: a refusal only
changes when somebody changes a setting. The SDK waits 5 minutes.

| `reasonCode` | Why |
| --- | --- |
| `disabled` | The owner hasn't turned the kind on. |
| `wrong-kind` | The token is another kind's. |
| `contract-major` | The provider speaks another major version of the contract. |
| `another-provider` | A different provider is connected for this kind. |
| `invalid` | The id or the name is missing or malformed. |

**Goodbye.** Before Gleanvolt closes a session itself, it sends `Goodbye(reasonCode, reason)`: the kind
was turned off (`disabled`), a newer connection of the same provider replaced this one (`replaced`),
or the owner made a new token (`token-replaced`).

**Requests.** Gleanvolt never waits for a provider inside a call. When it wants fresh data, it sends a
request with an id, and the provider answers with an ordinary publish that quotes that id. A request
that gets no answer within 2 minutes shows as "did not answer" on Settings. It changes nothing else.

**Status.** `ReportStatus({ healthy, message })` tells the owner how the provider is doing, for
example that its own source is down. It is shown next to the session.

## Contract versions

Each kind has a major and a minor version. A provider sends the version it was built for in its hello.
A different major is refused. A different minor is accepted: a host ignores what it doesn't know,
and the `Welcome` says which version the host speaks.

| Kind | Major | Minor |
| --- | --- | --- |
| `tariff` | 1 | 0 |

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
the *low tariff only* options of the charging modes. They are kept in memory: after a restart,
Gleanvolt has no tariff until the provider has said hello and published again. After a disconnect,
the windows still count until the last one ends. After that, there is no tariff restriction until the
provider publishes again.

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
ISO 8601.

The method names: `Hello`, `ReportStatus` and `Publish` on the hub; `RefreshRequested` and `Goodbye` on
the provider.
