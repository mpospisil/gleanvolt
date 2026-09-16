# MyŠkoda Public API fixtures

What `SkodaVehicleResponseTests`, `SkodaApiSignInTests` and `SkodaApiUpdateServiceTests` run against
(issue #193). No network anywhere in the suite: the tests put the real client and service in front of a
fake `HttpMessageHandler`, the `VwGroupUpdateServiceTests` way.

**Every one of these is built from the published spec, not captured from a car.** Nobody working on
this has a Škoda. Each field has the type the spec at
[`/v3/api-docs`](https://public.api.connect.skoda-auto.cz/v3/api-docs) gives it, and the values are
its examples where they were plausible — `remainingCruisingRangeInMeters` is an exception, because the
spec's example of `249` metres is not a range any car reports, so the fixtures use kilometre-sized
figures in metres.

- `charging.json`, `connect-cable.json` — a `GET /api/v1/vehicles/{vin}?include=charging` answer while
  charging and while unplugged. The `state` values are from the list in the spec's description of
  `ChargingStatus.state`, which is prose rather than an enum.
- `charging-unavailable.json` — a 200 with partial data: the `charging` part missing and an `errors`
  entry saying why, as the spec describes.
- `info.json` — `?include=info`, which is what the sign-in asks for to prove a key.
- `problem-*.json` — RFC 9457 problem documents with the types the spec names.
  **`problem-unauthorized.json` is the one live body**, captured 2026-09-15 from a request with no key.

The response headers (`X-API-Key-Expires-At`, `RateLimit-*`, `Retry-After`) are described in the
developer documentation's prose rather than in the spec's responses, so the tests add them the way that
prose describes.

**When a Škoda owner can supply a sanitised live response** (VIN replaced), it should replace the
corresponding fixture here, and this README should say which one is real — as
`Fixtures/VwGroup/id4-live-capture.json` did for the VW portal, where the capture corrected four field
names the synthetic fixtures had wrong.
