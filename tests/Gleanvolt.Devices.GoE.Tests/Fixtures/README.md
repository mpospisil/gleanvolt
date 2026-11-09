# go-e recordings

The recordings `GoeConformanceTests` replays (issue #352). Each is an HTTP dump: the body of every request the
driver makes, by path and query. Settings → charger → **Save register snapshot** writes the same format for a
go-e.

| File | State | Used for |
|---|---|---|
| `goe-idle.json` | Gemini 22 kW, no car (`car` 1), force state neutral, 16 A requested | Idle reads Available and 0 W; neutral is not "allowed" |
| `goe-plugged-in.json` | The same with a car waiting (`car` 3), allowed to charge | Plugged in reads Preparing, 0 W |
| `goe-charging.json` | Charging at 16 A on three phases, 11 050 W, force state on | Charging, 11 050 W, 16 A, allowed |
| `goe-api-off.json` | Nothing recorded, so every request is answered 404 | The probe refuses a web server without go-e's API v2 |

**Every file is synthetic.** They were written from go-e's API v2 key list
([goecharger/go-eCharger-API-v2](https://github.com/goecharger/go-eCharger-API-v2)): `car`, `alw`, `frc`,
`amp`, `ama`, `var`, `nrg` (power in W at index 11), `err`, `modelStatus`, `psm`, `pnp`, `fwv` and `typ`.
Their `note` says so. A tester's snapshot (#354) has to confirm what `car` reads with a car waiting and with
the force state off, that `nrg[11]` is watts on the tester's firmware, and whether `amp` is accepted as often
as the control loop writes it. The filter leaves out the serial number (`sse`), the Wi-Fi keys and the cloud
tokens, so there is nothing to redact.
