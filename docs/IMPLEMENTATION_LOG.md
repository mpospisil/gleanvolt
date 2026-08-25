# Implementation log

Reverse-chronological. Newest entry at the top.

---

## 2026-08-25 — The installation is described once: the `Pv` section, phase 1 (issue #111)

Where the array is lived in `Weather:Latitude`. What it is made of lived in `Solax:Inverter` and
`Solax:EvCharger` — an address each, under a vendor's name, with no model number and a shape that can
express exactly one charger because a single object cannot express two. Nothing anywhere said *this is
a 9.2 kWp array at 50.0755, 14.4378, feeding a SolaX X3-HYB-G4 PRO with one wallbox, and it is called
Home Roof.*

This is the additive phase: the new section exists, and **nothing about a running deployment
changes**.

### What changed

- **`PvSystemOptions` (the `Pv` section), in `Gleanvolt.Core`** — id, name, address, coordinates,
  azimuth, tilt, capacity, inverter capacity, loss factor, install date, the inverter, and a *list* of
  chargers. Core because the weather client, the composition root, the API and the UI all need it, and
  Core is the one assembly all of them may reference.
- **`PvSystemInfo`, one resolved snapshot**, built once at startup and registered as a singleton. No
  consumer ever asks "which key won?" — that question has one right answer per process and no place in
  a weather client.
- **`PvSystemResolver`**, which reconciles the two sources on one rule: **the older key wins wherever
  it is set**. An existing `.env` therefore describes exactly the devices it described yesterday.
  Each such win is a warning at startup, so the log is the migration checklist. The *identity* — model
  and name — always comes from the `Pv` section, because the older keys cannot express it at all.
- **Validation at startup, all problems at once**, each naming its key: an id that is not a slug, half a
  pair of coordinates, a tilt outside 0–90, a loss factor outside (0, 1], an unparsable install date, a
  missing inverter or charger, two chargers, two chargers sharing an id. One restart per mistake is a
  miserable way to configure anything.
- **The charger list reaches the composition root.** Modbus clients are registered in a loop, one per
  charger, keyed by the charger's id; `ModbusClientKeys.EvCharger` is now an alias resolving the same
  instance, because `[FromKeyedServices]` takes a compile-time constant. A second entry is refused by
  the resolver — configuration can express two so that it need not change shape later, but one mode,
  one set of HA controls and one surplus to divide cannot drive two.
- **The device defaults moved** from `Solax:Inverter`/`Solax:EvCharger` in `appsettings.json` to
  `Pv:Inverter`/`Pv:Chargers[0]`, with the models named. The deprecated keys ship unset, so a
  deployment only sees the warning if it is actually still using them — which the Pi is, through
  `INVERTER_HOST` and `EV_CHARGER_HOST` in compose. `Solax` is left holding `PollIntervalSeconds`.
- **The weather client asks the site for its coordinates**, not `WeatherOptions`. Where the site is is a
  fact about the site.
- `Weather:Latitude`, `Weather:Longitude`, `Solax:Inverter` and `Solax:EvCharger` are documented as
  deprecated in the README, in `.env.example` and on the properties themselves. `Solcast:ResourceId`
  and `Weather:ApiKey` are **not** deprecated: a provider's handle for your roof belongs beside that
  provider's other settings.

### Verification performed

- **931 tests pass** (29 new): the resolver's two rules and every validation message, the
  identity-from-`Pv`-address-from-`Solax` split, azimuth normalisation (`-90` and `270` are one
  bearing), and the composition root's keyed registrations — including that the fixed key and the
  charger id resolve the *same* client, since two would be two sockets to one wallbox.
- **Run against a Pv-only configuration** (`Pv__Inverter__Host`, `Pv__Chargers__0__Host`, no `Solax`
  keys): the worker starts, polls, and announces itself —
  `PV system: Home Roof (home-roof) at 49.2678,16.5295; inverter SolaX X3-HYB-G4 PRO at …`. With the
  repository `.env`'s `Weather__Latitude` still set, the deprecation warning fires and those
  coordinates win, which is the documented behaviour.
- Not deployed to the Pi yet. Nothing about this phase changes what the Pi talks to; the visible
  difference there will be two deprecation warnings at startup naming `Solax:Inverter` and
  `Solax:EvCharger`.

---

## 2026-08-23 — The API's base URL answers, and the document can be read (issue #103 follow-up)

Deployed to the Pi, opened `http://<pi>:8090/api/v1/` in a browser, and got an empty 404 — the base
path was not a route, and every route that was one answered 401 because a browser cannot send an
`Authorization` header. The API was working; there was no way to see that.

### What changed

- **`GET /api/v1/` — an unauthenticated index.** Product, version, running build, document URL, how to
  authenticate, and the operations this build serves. The operation list is read from
  `EndpointDataSource` at request time, so it cannot drift from what is actually mapped. Mapped outside
  the key-filtered group; it carries no site data.
- **`/api/v1/openapi.json` no longer needs the key.** See the decision record: the document is what a
  client is generated from, and the disclosure it was protecting is slighter than the cost, on a host
  whose web UI is already open on the LAN.
- Both are 404 when `Api:Enabled` is false, like everything else — a disabled API announces nothing.

### Verification performed

- **905 tests pass** (4 new): the index without a key, with and without a trailing slash, listing the
  real routes with their summaries, carrying only the six fields it should, and 404 when the API is
  off. `OpenApiContract.json` regenerated — the diff is exactly `getIndex`, `ApiIndexResponse` and
  `ApiOperationResponse`, which is the review this snapshot exists for.
- The route template is `/api/v1`; ASP.NET matches `/api/v1/` to it, which is the form a person types,
  and there is a test for that rather than an assumption.

---

## 2026-08-23 — An HTTP API described by OpenAPI, for programs rather than people (issue #103)

Everything the controller knows was reachable by a human and by nothing else. This adds a third
surface — `Gleanvolt.Api` — beside Home Assistant and the web UI: the same telemetry, history,
forecast and actions, over HTTP, described by an OpenAPI document a client can be generated from. The
use case that shaped it is an **MCP server**, so an LLM can answer questions about the installation and
act on it; that server is a separate process in a separate repository, and nothing in the API is
specific to it.

### What was built

- **`src/Gleanvolt.Api/`** — a packable class library referencing `Gleanvolt.Core` and nothing else,
  composed by `Gleanvolt.Hosting`. Minimal APIs under `/api/v1/`, the document at
  `/api/v1/openapi.json`, both behind the key.
  - `ApiOptions` (`Enabled`, `Keys`, `MaxQueryRange`, `MaxSessions`), `ApiKeyFilter`, `ApiHostInfo`.
  - `Contracts/` — the wire DTOs, owned here rather than shared with Core, with the XML comments that
    become the schema descriptions.
  - `Endpoints/` — `status`, `health`, `energy/intervals`, `energy/days/{date}`, `sessions`,
    `sessions/{id}`, `forecast`, `vehicle`, `plans/targeted/preview`, `charging/start`,
    `charging/stop`, `battery-hold`.
- **`TargetedChargeRequestFactory` in `Gleanvolt.Core`** — the web form's own `TryCompose`, moved: the
  SOC → kWh conversion, the just-in-time tail split, the horizon check and the four refusals. The
  Targeted tab now translates its form into the factory's terms and nothing more. Two doors onto the
  same promise must reject the same things for the same reasons.
- **`AddWebSurface` became `AddHttpSurfaces`.** The listening socket now belongs to whichever surface
  wants one rather than to the UI, so the API can run with `Web:Enabled=false`; `NoListenServer` is
  reached only when neither is enabled.

### The build fix the document needed

Schema descriptions come from XML comments, and there were none: `GenerateDocumentationFile` is set in
`Directory.Build.targets`, which MSBuild imports **after** the SDK targets that derive
`DocumentationFile` from it — so the flag was set after the only thing that reads it had already
looked, and no project in this repository was emitting XML documentation at all. `Gleanvolt.Api` sets
the property in its own body, where the SDK still sees it. Fixing it repo-wide surfaces ~40
pre-existing unresolvable `cref` warnings and is left as a separate change.

### Decisions worth stating

- **The API defaults off and demands a key; the UI defaults on and does not.** Two endpoints write to
  hardware, and any program on the LAN can reach a port. `Api:Enabled` with no key is a startup
  failure, like `Web:RequireAuthentication` with no hash.
- **Keys are stored as secrets, not hashes** — generated, single-purpose, out-of-band, like the broker
  password. The key's *name* is the action's source in the log and in the recorded session.
- **An endpoint filter, not an authentication scheme**, so the API does not depend on the UI's
  authentication services existing and does not inherit the cookie's `DefaultPolicy`.
- **A failed hardware write is 200 with `succeeded: false`**, not an HTTP error. The call was
  understood; the controller is in exactly the state it was in before.
- **`/battery-hold` is 409 when the feature is disabled**, rather than recording an intent that
  silently does nothing.
- **Weather on `/forecast` is opt-in** (`?weather=true`): the forecast is cached and free to ask for,
  the weather is a live third-party call against a quota.
- **Stop-the-service, raw register writes, TLS, users, scopes and rate limiting are out**, and recorded
  as such on the issue.

### Verification performed

- **890 tests pass** (71 new). The API suite runs the real routes over `TestServer` with the hardware
  seams faked: the key check on every route and on the document, the 503 before the first poll, the
  bounded range, the local-day sum in the site's zone rather than the machine's, vehicle staleness,
  the preview writing nothing, the four refusals, the request set before the mode and dropped when the
  charger refuses, and the disabled-hold conflict. Plus the factory's own tests in Core and the
  composition-root tests in Hosting.
- **The OpenAPI contract is pinned by a projection**, not by the document's bytes:
  `tests/Gleanvolt.Api.Tests/OpenApiContract.json` holds every operation id, parameter, response code
  and schema property with its type and nullability. Regenerate with
  `GLEANVOLT_UPDATE_OPENAPI_CONTRACT=1 dotnet test tests/Gleanvolt.Api.Tests`. Byte-for-byte would have
  broken on an SDK patch bump, since CI floats `10.0.x`.
- **The test host uses `CreateSlimBuilder`.** The default builder watches `appsettings.json`, and a
  suite that stands a host up per test exhausts the machine's inotify instances long before it runs out
  of tests.
- **Not yet exercised against live hardware.** Every write path goes through `IChargeActions` and the
  selectors the UI already drives, so nothing new reaches Modbus, but the API has not been run against
  the Pi.

---

## 2026-08-23 — Just-in-time charging: the last stretch lands at departure (issue #101)

A targeted charge could say *how much* and *by when*, but not *when it should arrive*. Delivered as
cheaply as possible it usually arrives early, and a car asked for 100% then sits at 100% overnight.

### What changed

`TargetedChargeRequest` gains `Priority` (`Cheapest` | `JustInTime`), `TailEnergyWh` and
`RestSocPercent`. Every existing construction is unchanged — the defaults are the old behaviour, which
is why nothing that was working before this had to be touched.

`TargetedChargePlanner` gains `PlanHold`, `Within` and `Merge`. Under a hold the existing
`PaceOverWindow` runs twice, over `[now, release]` and `[release, deadline]`, and the results are
merged; `release = deadline − tail ÷ P_max − ReleaseSlack`. The plan carries `TailEnergyWh`,
`HoldUntil` and `IsHoldingAt(instant)`.

`TargetedChargingController` gains one branch, checked before the pace: while `IsHoldingAt` is true it
soft-pauses with a sentence saying the idle charger is deliberate. That branch is where the sun is
refused — with pace at zero it would otherwise walk straight through `DecideFromPace` and deliver the
tail early on a bright afternoon.

`VehicleTargetEnergy` gains `TailAboveRestWh` (the split) and `ResultingSocPercent` (the inverse of
`RequiredWh`, so an owner who asked in kilowatt-hours still gets a rest point).

### What was deliberately left out

`TailPowerFactor`, a charge-limit gate on `settings.target_soc`, and a `VehicleChargeCurve` derived
from the session store's `active`/`actual`/`VehicleSocPercent` samples. See the decision record — the
short version is that the car stopping short is already reported by `CompletionDwell`, the feed that
would drive a gate expires and needs a human, and the per-poll rebuild already self-corrects a slow
tail. Both are recorded as deferred on the issue rather than deleted.

### Surfaces

- **Web**: a **Charging priority** select and a **Rest at (%)** field on the Targeted tab, both shown
  only when a pack size and a reading make the rest point knowable. The plan view gains **Held back**
  and **Released at**, and the narrative gains a hold paragraph with two forms — one for a hold planned
  and one for a hold in force. *Preview* now builds a second plan at `Cheapest` and prints the
  difference in grid import.
- **Home Assistant**: `select` **Charge priority**, `number` **Target rest SOC**, `sensor` **Target
  hold until**. The MQTT worker now takes `IVehicleTelemetry` and `VehicleOptions` so it can make the
  same split the web tab makes; without a SOC or a capacity it warns and holds nothing.
- **Config**: `ChargeControl:Targeted:JustInTime` — `RestSocPercent` (80), `ReleaseSlack` (30 min).

### One existing test was narrowed, not weakened

`HaDiscoveryTests.DiscoveryMessages_PublishNoSelectAtAll` asserted that discovery publishes no `select`
of any kind. Its intent (from #89) was that nothing offers a *charge mode* to pick, in competition with
the buttons. A charge **priority** select is a different thing — it only qualifies a target the activate
button still has to apply, and nothing about it can put the charger into a mode. Renamed to
`DiscoveryMessages_PublishNoModeSelect`, asserting that, and now also asserting the priority select is
present.

### Verification performed

- **819 tests pass** (36 new). Planner: the release point and its slack, the tail scheduled after it
  and the free part before it, the tail never exceeding what is still owed, the hold abandoned when the
  release has passed and when the free part would not fit, and `Cheapest` unchanged. Controller: a
  generous surplus refused while holding, the idle sentence, a *planned* hold not stopping the free
  part charging, and the default path untouched. Plus the rest-point arithmetic, the narrative's two
  forms, the tab's split, and the three HA entities.
- **Not yet observed on hardware.** The interesting case — a real ID.4 taking the 80 → 100 stretch more
  slowly than `P_max` and the pace correcting for it — needs an overnight run. `ReleaseSlack` at 30
  minutes is a first guess and is the number to revisit against a recorded session.

---

## 2026-08-22 — The targeted plan reads the car, and is quoted before it is promised (issue #99)

`Targeted` still promises kilowatt-hours by a time. What changed is what it will accept as the
question, and when it shows its answer.

### What was built

- **`VehicleState.RangeKm` and `range_km` on the MQTT contract** — one more optional field on the same
  rule as the rest: absent is a supported configuration, present-but-junk drops the whole payload so
  the holder keeps its last good reading and its age visibly grows. `0` is a real reading; a figure
  past 2000 km is a template publishing metres.
- **`Vehicle:BatteryCapacityKWh` and `Vehicle:ChargeEfficiency`** — the car's *usable* pack and the
  charger-meter-to-cells losses. The capacity has no default: unset means the SOC basis is not offered,
  and nothing else on the install changes.
- **`Core/Strategies/VehicleTargetEnergy`** — the whole SOC-based target, and deliberately no more than
  arithmetic. Null when it cannot honestly convert (no SOC, no capacity), zero when the car is already
  there — two different answers, and the caller refuses on the second.
- **`TargetedChargeRequest.TargetSocPercent` / `VehicleSocPercentAtRequest`** — optional, so every
  existing call site is untouched, and descriptive only: `RequiredEnergyWh` is still what every consumer
  reads. Kept so the request can be read back as "42% → 80%" rather than as a bare 32.5 kWh.
- **`ITargetedChargePreview`, implemented by `TargetedChargeProvider`** — the same planner, the same
  telemetry, the same forecast, delivery at zero, nothing logged and nothing written. `Update` now
  records the reading *before* its `request is null` early return, which is what makes a preview
  possible when no target is running — the case it exists for.
- **`Components/Plan/TargetedPlanView.razor`** — the narrative and the twelve figures, extracted so the
  preview and the running plan cannot drift apart in wording.
- **`Components/Plan/TargetedTab.razor`** — the car above the form (battery, range, plug state from both
  sources, charge state, reading age, a stale flag); a basis selector that appears only with a capacity
  *and* a reported SOC; a live conversion hint under the percentage input; **Preview plan** → the plan →
  **Start charging** / **Back to the form**, with any edit dropping the preview.
- **The dashboard's Vehicle card** gains **Car range**.

### Tested

`VehicleTargetEnergyTests` (8) pins the conversion in both directions, the two kinds of "no": no SOC
and no configured pack, and the clamps on a mistyped efficiency. `VehicleTelemetryPayloadTests` gains
the range field, an absent one, a zero one, and the three rejections. `TargetedChargePreviewTests` (6)
is the new hosting suite, and it is mostly negative on purpose: pricing is not requesting, a preview
does not disturb the metering behind a running target, and the provider keeps answering while no target
is running — the early-return bug, caught by the test that names it. `TargetedTabTests` grew from 19 to
34: the car card and its absence, the basis offered and withheld, the conversion at activation, the
refusal when the car is already past the target, and the preview's whole life — priced without being
promised, no Start button before a plan, dropped on an edit, discarded on request, startable before the
first poll, and gone once the charge is running.

783 tests pass, up from 749.

---

## 2026-08-22 — The web UI regrouped: a dashboard that reports, a plan page that decides (issue #98)

Three surfaces for one question became two with a clear division of labour. Nothing in `Gleanvolt.Core`
or `Gleanvolt.Hosting` changed: this is entirely the shape of `Gleanvolt.Web`.

### What was built

- **`Components/Pages/ChargingPlan.razor`** — `/charging-plan` and `/charging-plan/{Tab}`. A header
  common to every mode (the read-only mode line, **Off**, the action note and any charger refusal,
  and the **battery discharge hold** switch), then a tab strip, then the active tab. It owns the two
  seams every tab needs and hands them down as `Start`/`Stop` delegates, so an action's outcome is
  reported in one place rather than four.
- **`Components/Plan/{Solar,Forecasted,Fast,Targeted}Tab.razor`** — one per mode, carrying that mode's
  button, the runtime numbers it reads, and the plan it publishes while it drives. `ForecastedTab`
  absorbs the old `/forecast` page *and* the four runtime numbers that used to sit on the dashboard;
  `TargetedTab` absorbs `/targeted` whole. **Minimum battery SOC** appears on both, through the same
  runtime seam, because both planners work to it.
- **`Components/Pages/Dashboard.razor`** — regrouped as **Energy**, **Vehicle** and **Charging
  session**, and stripped of every control. The session section renders only while there is a session
  to report — a mode is driving, or the car is drawing under none — and otherwise says so in a line
  with a link to the plan page, rather than showing a grid of dashes.
- **`Components/Pages/Forecast.razor` and `Targeted.razor` deleted**; the nav is Dashboard · Charging
  plan · Sessions · Energy · Health.
- **The tab lives in the URL**, so a bookmark and a refresh return to the same mode and the back
  button walks them. `/charging-plan` with no tab resolves to whatever is actually driving the
  charger — computed on the way in only, so a mode that ends itself cannot pull the tab out from
  under the reader.
- **`site.css`** gains the tab strip and panel, and a global `h2` that reads as a divider between
  grids. The panel is the same card the control sections are, so the active tab runs into it; the
  stat cards and the chart inside it drop to the page background, or a card on a card of the same
  colour would leave every figure edgeless.

### Tested

`ForecastPageTests` and `TargetedPageTests` became `ForecastedTabTests` and `TargetedTabTests`, each
rendering the page and asking for its tab — which exercises the wiring between them rather than the
tab in isolation. `ChargingPlanPageTests` is new: the tab strip (one per mode, none for `Off`, the
running mode marked and opened on by default, a nonsense slug falling back to a real tab), the header
(the mode line, the note, a refused `Fast`, a mode that ends itself taking the note with it, the hold
switch above the tabs and not inside one), and the two tabs that are only a button and some figures.
`DashboardPageTests` keeps its telemetry and vehicle cases and gains the grouping ones — the three
headings in order, the empty session state and the uncontrolled session that must not be hidden by
it, and that the page now carries no `button`, `input` or `select` at all. 150 web tests, 749 in all.

---

## 2026-08-22 — The weather each session ran in (issue #96)

The companion to the day-forecast curve: that says what the sun was expected to do, this says what the sky did. Both ends of every session now carry a weather reading, and the day carries its sunrise and sunset.

### What was built

- **`WeatherObservation` / `WeatherReading` / `IWeatherService`** (Core). The observation is a moment — temperature, pressure, humidity, cloud, visibility, condition and its description, and the provider's own `ObservedAt`. The reading is one fetch: an observation plus the day's daylight bounds, which are not part of the observation because they belong to the day.
- **`OpenWeatherMapService`** (Infrastructure) against `data/2.5/weather`. One Call 3.0 returns `401` without its own paid subscription, and the free endpoint carries every field recorded here. It never throws for a provider-side problem, bounds its own request (5 s), warns once rather than per call, and is inert without a key or coordinates.
- **`WeatherOptions`** bound from `Weather`, with **nullable** coordinates — 0,0 is a real place in the Atlantic.
- **`ChargingSession.WeatherAtStart` / `WeatherAtEnd` / `Sunrise` / `Sunset`**, attached by `SessionRecordingWorker` on its way to the store. The tracker is untouched: it is a pure strategy, and this is I/O.
- **Database schema v4** — eighteen columns, not a JSON document, because these are the axes the analysis groups by. The insert writes the opening reading and the daylight bounds; the completing update writes only the closing one, so a failed close can never blank what the open recorded. `ChargingSessionDocument.CurrentSchemaVersion` moves to 4.
- **The session detail page** shows both readings (`clear sky, 19.6 °C, 5% cloud → light rain, 14.1 °C, 88% cloud`) and the daylight window with its length.
- **Deployment** passes `WEATHER_API_KEY`, `WEATHER_LATITUDE`, `WEATHER_LONGITUDE`; all three empty means no weather call is ever made.

No Home Assistant entity, and nothing in charge control reads any of it.

### Tested

The client against a stubbed transport, including the cases that matter more than the happy path: no key and no coordinates make no network call at all, a 401 and a DNS failure are null readings rather than exceptions, and a provider that accepts the connection and then says nothing is abandoned in milliseconds. The store round-trips both readings and the bounds, keeps the opening reading through a close that carries none, and migrates a v1 file to v4 in place.

---

## 2026-08-22 — Every session carries the whole day's forecast curve

A recorded session could be compared against the forecast *while it ran*, never against the day it ran
on. So "4 kWh delivered, 28 % solar" had no scale behind it: a poor session on a good day and a fair
one on a hopeless day were the same row. The header now carries `DayForecast` — the whole local day,
every 30-minute period with its p10/p90 bands, from before the car arrived to after it left.

### What was built

- **`SolarForecastHistory`** (Core, `Strategies/`). Solcast returns only the periods still to come, so
  the live cache erases this morning by this afternoon. This retains every period any refresh has
  carried, keyed by period end, for 7 days; a refresh upserts, so future periods get the newer estimate
  and elapsed ones stay at the last thing said about them. Thread-safe, unlike its neighbours — the
  refresh worker writes it and the poll loop reads it.
- **`ISolarForecastService.GetDayForecast(DateOnly)`**, served from that history by
  `SolcastForecastService`. Deliberately *not* a change to `GetForecastForToday`, which means
  "remaining" to the day planner, the accuracy tracker and the day summary alike. `null`, never an
  empty curve, when nothing is held for the day.
- **`ChargingSession.DayForecast`**, filled by `ChargingSessionTracker` at open and refreshed onto the
  session at close — the day fills in behind us as it passes, so the close-time version covers hours
  the open-time one could not. Passed in by `SessionRecordingWorker` like the forecast power and the
  vehicle state, so the strategy stays free of the forecast service; the worker memoises the curve per
  local day and per fetch rather than rebuilding a few hundred periods every five seconds.
- **Database schema v3** — `sessions.day_forecast_json`, one JSON document rather than a periods table:
  written once, read whole, and it has to travel with the session when the document is published.
  `ChargingSessionDocument.CurrentSchemaVersion` moves to 3, additive; older rows stay `NULL`.
- **The session detail page** shows the day's total with its p10–p90 band beside the other header
  facts, and omits the row entirely for sessions recorded before this existed.

Nothing in charge control reads any of it, and no Home Assistant entity was added: this is history, not
telemetry.

### The limit worth knowing

The history is in memory, so a restart at 14:00 loses that morning for good — nothing re-fetches the
past, and doing so would mean a second Solcast endpoint and a second call against the daily quota. The
15-minute energy history still holds forecast-versus-actual for those days.

---

## 2026-08-21 — Charging starts from a button, and the controller writes the charger's use-mode

Issue #89. Controlled charging had been a *setting* since the first version: pick a strategy from a
select, then hope the world agrees — car plugged in, charger left in Fast by hand. A mode selected
over a charger sitting in Green did nothing at all and reported it only as `Control state: Idle`. Every
kind of controlled charging is now started by an action and by nothing else.

### What was built

- **`IChargeActions` / `ChargeActions`** (Core interface, Hosting implementation — the
  `IChargeControlModeSelector` shape, because `Gleanvolt.Web` sees only Core). `StartAsync(mode,
  source)` writes `ChargerUseMode = Fast` and *then* selects the mode; `StopAsync(source)` writes
  `Stop` and returns the mode to `Off`. `ChargeActionResult` carries success plus a sentence, because
  for the first time a control surface can fail.
- **`IEvChargerControl.SetModeAsync`** + its implementation against `EvChargerRegisterMap.ChargerUseMode`
  (`0x60D`), written raw — the enum's values *are* the register's — with dry-run logging the encoded
  value like `SetCurrentAsync` does, and a simulated use-mode so dry-run reads agree with dry-run
  writes.
- **Home Assistant**: five buttons (`start_solar`, `start_forecasted`, `start_fast_no_battery`,
  `activate_target` — kept, so existing dashboards don't break — and `charge_off`), a read-only
  `charge_mode` sensor off the same `value_json.mode` the select used to hold, and the select's config
  topic added to `RetiredDiscoveryTopics` so HA deletes the entity on its own.
- **Web UI**: the dashboard's select became a button row plus a state line; `/targeted`'s `Activate`
  and `Cancel` go through the same seam, and a refused use-mode write lands in the existing `_error`
  slot.
- The reversal itself is written down in [DECISIONS.md](DECISIONS.md), because four class summaries and
  three README claims asserted the old promise in as many words.

### The decisions worth writing down

**`Fast` is written once and never re-asserted.** The alternative — writing it every cycle the mode is
selected — would make a charger changed at the wallbox unusable while any mode was running, and would
turn one write into one per poll on a register nobody in this project had ever written before. The
`if (use-mode != Fast) → Idle` precondition in all four controllers is what makes that safe, and it is
unchanged.

**The self-off paths go through `StopAsync`, not `_mode.Set(Off)`.** This is the part that would have
been missed. `FastNoBattery` and `Targeted` end themselves after writing the pause current; without
the `Stop`, a mode that switched itself off would leave the charger sitting in Fast at 0 A — which a
car is free to start drawing from again the moment anything touches the setpoint, and which is a
different end state from the one the button produces. One code path, two callers, and a test that
pins the ordering: charger stopped, *then* hold released, in the one cycle.

**A failed stop still releases control; a failed start does not take it.** The asymmetry is
deliberate and argued in DECISIONS.md. The short version: "nothing happened" is an acceptable outcome
for a start, and an unacceptable one for a stop.

**The MQTT worker's command routing became internal rather than public.** `HandleCommandAsync(topic,
payload)` with `InternalsVisibleTo` — the alternative was a public message handler on a
`BackgroundService`, and the behaviour (which topic does what, and that only an exact `PRESS` counts)
is worth a test. The worker's tests run with no broker at all: `_client` is null, so every publish
inside a handler is a no-op and only the seam calls are visible.

**The dashboard's "(started from the Web UI at 13:42)" is the page's own note, not new state.**
`IChargeControlModeSelector` is untouched — it is still what the controllers, the status holder, the
session tracker and the MQTT payload read — so the note lives in the component and is cleared by
`Mode.Changed`. A mode that has moved on since is not the action that set it.

### Verification

- 289 Core, 178 Hosting, 131 Web and 97 Infrastructure tests pass. New: the use-mode encoding and its
  dry-run behaviour, the action's ordering and both failure paths, the buttons and the retired select
  in discovery, every command topic through the worker, the self-off path stopping the charger before
  releasing the hold, and the dashboard and targeted pages against the new seam.
- **Not verified on hardware.** `0x60D` has only ever been read by this project. The values
  (`0=Stop, 1=Fast`) come from the wills106 register map, not from anything confirmed here by
  writing. Per CONTRIBUTING, this wants a `ChargeControl:DryRun` run against the real charger with the
  logged register values checked against the map before it goes near the Pi for real.

### What to watch on the first live run

1. **Does the charger actually enter Fast from Green?** The log line is `charger use-mode: Fast
   (register 1)`, immediately followed within one poll by a `Control state` that is no longer `Idle`.
2. **Does `Stop` actually stop the car**, rather than merely changing a register the charger ignores
   while a session is live? If not, the Off button needs the pause write back after all.
3. **Does a wallbox change mid-session behave?** Set the charger to Green by hand while a mode runs:
   the controllers should go `Idle` and nothing should write `Fast` back.
4. **The upgrade.** The old `select.charge_mode` should disappear from Home Assistant on its own the
   first time the controller connects. If it doesn't, the retained config topic didn't clear.

---

## 2026-08-20 — Targeted charging: an amount of energy, a departure, and as little grid as possible

Issue #80, in the four phases it was cut into (#81–#84). The mode answers the one question none of the
other four could: **"I need 22 kWh in the car by 07:00 — do that, and use as little grid as you can."**

### What was built

- `TargetedChargePlanner` (Core/Strategies) — pure, stateless, rebuilt every poll. Slices the forecast
  over `(now, departure − margin]`, books the home battery's need backwards out of it, gives the car
  what is left, and finds the grid block by a backward pass from the deadline.
- `TargetedChargePlan` / `TargetedChargeBlock` / `TargetedChargeRequest` / `TargetedChargePlannerOptions`
  (Core/Models), `TargetedChargeStrategy` / `TargetedChargeSource` (Core/Enums).
- `ForecastSlicer` (Core/Strategies) — the one refactor: `SolarDayPlanner`'s private slicing and
  reservation, extracted so both planners share them rather than growing a second copy that drifts.
  `SolarDayPlannerTests` is the guard that its behaviour did not change.
- `TargetedChargingController` (Core/Strategies) + `ITargetedChargeSelector` (Core/Interfaces).
- `TargetedChargeSelector` and `TargetedChargeProvider` (Hosting/Targeting) — the `DayPlanProvider`
  pattern: meter the delivery, fetch the forecast over the request's window, rebuild the plan, log it
  rate-limited.
- `/targeted` and `TargetedPlanNarrative` (Web) — the plan in words, not a chart.
- Home Assistant: a **Target energy** number, a **Departure time** text, an **Activate target** button
  and six plan sensors; `HaDiscovery` gained `Text()` and `Button()` helpers and a target state block.

### The decisions worth writing down

The two central ones — late grid placement, and the home battery keeping priority — are in
[DECISIONS.md](DECISIONS.md). Four smaller ones belong here.

**The ceiling has to cover hours the forecast says nothing about.** Slicing alone stops at the last
forecast period, so an overnight target's window would end there and `P_max × time` would understate
what the charger can do by most of the night. `ForecastSlicer.SliceContiguous` fills every gap with a
zero-PV slice, which makes the ceiling exactly `P_max × (deadline − now)` however far the forecast
reaches — and makes the "not enough time" case fall out of the same arithmetic as the others rather
than being a branch of its own.

**Delivery is metered on measured charger power, from activation.** Not on the commanded current, and
not from when the car was plugged in. A car that limits itself to less than we asked for simply pulls
the grid block earlier on the next poll, with no special case anywhere; and energy the car took under
some earlier mode is not part of this promise.

**"The car has stopped" only counts while we are asking it to charge.** The fast mode can read a
silent car as a finished one because it is always commanding a current. This mode pauses between
blocks, and reading that silence the same way would end the mode every time it waited for the sun. The
completion check is gated on `input.Charging` for that reason.

**No usable forecast is not a failure here.** `Forecasted` degrades towards conservatism — an absent
forecast must never read as headroom. This mode degrades towards *keeping the promise*: every solar
term goes to zero, the plan becomes grid-only, and the target is still met. `IsUsable: false` is a
caveat the UI reports, not a fallback anything takes.

### What to watch on the first real overnight run

1. **Does the grid block actually shrink?** Plug in at 21:00 with a morning departure and watch
   `Targeted plan: … Grid=…kWh GridStart=…` across the night. The block should hold roughly steady
   while nothing is charging, then shrink as sun or delivery arrives. A block that *grows* without the
   departure moving means delivery is being metered wrong.
2. **Does the hold arm exactly at `GridStart`, and release at completion?** The log line is "Battery
   discharge hold armed automatically: the Targeted plan's grid top-up has started". Armed early means
   the block placement is wrong; never armed means the plan never entered its own block.
3. **Does the car reach the target before the departure?** The safety margin is 15 minutes; a car that
   negotiates slowly, or derates, eats into it. If the target is met at the deadline rather than
   before it, raise `ChargeControl:Targeted:SafetyMargin`.
4. **Does the mode return itself to `Off`?** Three ways in: target met, departure passed, car stopped
   at its own limit. The first is the expected one; the third should name the shortfall.
5. **The 05:00 failure mode.** The request does not survive a restart. If the container restarts
   overnight — a deploy, an OOM, a power cut — the car stops charging and nothing says so. Worth
   watching the first few nights before trusting it with an actual trip.

### Verification performed

- 275 Core, 143 Hosting, 121 Web and 91 Infrastructure tests pass. New: the planner (both regimes, the
  latest-possible block, the prorated overlap, no forecast, the battery's reservation, target met,
  departure in the past, a departure onto tomorrow's periods), the controller's decision table, the
  mode end to end through the poll loop, the page and its narrative, and the Home Assistant entities
  plus the departure-text parse path.
- Nothing verified against hardware yet: this writes only the current setpoint, through the same
  charge-control path every other mode uses, but no overnight run has happened.

---

## 2026-08-19 — An energy history of the site, at 15-minute resolution

The session store records charges. Nothing recorded the **site**: how much the roof made last March,
how well the forecast tracked it across a season, how much was exported at noon and bought back in the
evening. None of that is derivable from session data, because most of the year has no session in it —
and the questions analytics will want to ask years from now are not ones anybody has asked yet, which
is precisely why the recording has to start before they are.

### What was built

A **second observer of the same status snapshots**, with its own tables in its own database file, that
does nothing but record.

- `EnergyIntervalTracker` (Core/Strategies) — pure, framework-free, and where every rule about what a
  row *means* lives. Takes poll snapshots, hands finished buckets back.
- `EnergyInterval` (Core/Models) — one bucket. In **kWh**, the one place in this codebase that isn't
  in watt-hours: it is the analytics surface, read by spreadsheets rather than by control code.
- `SqliteEnergyIntervalStore` (Infrastructure/Monitoring) — `data/energy.db`, one table, schema v1.
- `EnergyMonitorWorker` (Hosting/Monitoring) — a `BackgroundService` subscribing to
  `ChargeControlStatusHolder.Updated`, exactly as `SessionRecordingWorker` does: channel in, work on
  its own loop, so the poll loop can never be stalled by a disk.
- `EnergyMonitor` configuration section, **on by default** for the same reason the session store is.

### The decisions worth writing down

**A bucket, not a sample.** A session sample is an instant whose totals run from the start of that
session; an interval covers a fixed window and its figures belong to that window alone. Summing a
day's buckets gives the day, exactly — which no arithmetic on session samples can do.

**Split at the boundary, don't round to it.** A snapshot at 10:14:58 followed by one at 10:15:03
straddles the quarter hour. `EnergyIntegrator` could not express this — it accumulates until reset, so
resetting on arrival of the second snapshot would post all five seconds to whichever bucket won the
race. The tracker splits instead: two seconds settle the closing bucket, three open the next. That is
why this is a new class rather than a second use of the old one, and it is what makes a poll cadence
sharing no common factor with the interval unable to skew the series.

**`covered_seconds` is a column, not an assumption.** Energy is integrated by holding each reading
until the next, which is how the poll loop sees the hardware. Beyond `MaxGap` (5 min) that stops being
true — the service was restarting, or the inverter was unreachable — and holding across it would
invent energy nobody measured. So the bucket is closed short and the row says so. Without this a
restart at 09:07 would read as "the sun went out for seven minutes".

**Appends merge; they do not replace.** `period_start` is the primary key and a second write for the
same window *adds* to the stored row (`ON CONFLICT DO UPDATE`, with the SOC statistics merged as
statistics and `soc_mean_percent` re-weighted by the two coverages). The worker flushes its
part-finished bucket on shutdown, so a planned restart loses nothing: the stopping process contributes
the minutes it saw, the starting one contributes the rest. Without the merge every deploy would
silently delete the minutes before it.

**Nothing is netted.** Import and export are separate columns, and so are battery charge and
discharge. A quarter hour that exported 0.4 kWh and imported 0.4 kWh is not the same event as one that
did neither, and a net of zero cannot tell them apart afterwards. It also makes the row *balance*, so
house load needs no column of its own — it is `solar + import - export - charge + discharge`, and
storing it would only create a second version that could disagree with the columns it came from.

**`forecast_solar_kwh` is nullable.** "We had no forecast" is not "we expected nothing", and a chart
must be able to break the line rather than draw a dip. This is also why the worker reads the forecast
service directly instead of taking `ChargeControlStatus.ForecastSolarPowerWatts`, which reports 0 for
"no forecast" and so cannot be told apart from a genuine night-time zero. The merge preserves the
distinction in both directions: a null half never zeroes a half that had one.

**SOC averages over time, not over samples.** A reading that stood for ten minutes counts ten times
one that stood for one. Sample-count averaging would let a burst of polls during a Modbus retry storm
drag the figure toward whatever the pack happened to be doing that second. Consecutive buckets also
join up — the SOC standing at a boundary ends one bucket and starts the next — so a chart drawn from
the rows has no gap between them.

**Its own file, beside `sessions.db` rather than inside it.** `SqliteChargingSessionStore` serialises
writes on an in-process lock; a second writer holding a *different* lock over the same file would turn
that back into the `SQLITE_BUSY` retry loop the lock exists to avoid. The two stores also have
separate retention and separate reasons to fail. Nothing is lost: an analysis that wants both opens
one and `ATTACH`es the other.

**Buckets align to the UTC epoch, not to local midnight.** 15 minutes divides every real UTC offset in
use — including the 30- and 45-minute ones — so the two agree anyway, and aligning on UTC keeps a
daylight-saving change from producing a 45- or 75-minute bucket. `local_date` is stored alongside so
"group by day" stays a column rather than timezone maths in every query.

**`RetentionDays` defaults to 0 — keep everything**, the opposite of the session store's 365. At 96
rows a day a decade is still a file you could email, and a table built to be looked at years later
should not be quietly deleting the years.

### The viewer

`/energy` in the web UI: one recorded day at a time, a row per interval, with a date picker and
prev/next buttons that stop at today. It reads `IEnergyIntervalStore.GetIntervalsAsync` and reaches
past nothing into SQLite, and it degrades to "isn't available right now" when the store can't be
opened — the same shape `/sessions` already had.

**A table rather than a chart, deliberately.** The value of this store is that the figures are exact
and that a partial row is *visibly* partial. So a row below full coverage is marked, its percentage
spelled out — and only then, because a column reading "100%" ninety-six times would bury the handful
that matter — and counted in a note under the table; a window no forecast covered shows an em dash,
not `0.00`. A chart would smooth over both. Charts can come later, over the same query.

The `House` column is the residual — `solar + import - export - charge + discharge` — computed by
`EnergyInterval.HouseLoadKwh` rather than stored, and the note under the table says so: a column that
looks like a measurement and isn't is worse than no column.

### Not built

**No Home Assistant entities.** This feature is history, not telemetry, and nothing in it changes on a
cadence worth an entity's retained state.

### Tests

41 new tests — 15 over the tracker (boundary splitting, the gap rule, the two-column sign handling,
time-weighted SOC, interval validation, out-of-order snapshots, the closing balance), 11 over the
store against a real SQLite file (round-trip of every column, the merging upsert, the forecast-null
rules in both orders, pruning), 12 over the viewer (the local-day window, day stepping, the totals
row, the partial-row marking, the forecast dash, the residual), and 3 over the composition root,
which had no wiring test of its own before. Full suite: 555 passing.

---

## 2026-08-18 — Hysteresis on the forecast mode's SOC floor

The `Forecasted` mode's SOC floor was the one threshold in it with no hysteresis. Everything else is
deliberately asymmetric — `ResumeHysteresisWatts` on the surplus, `OutlookHysteresisFraction` on the
day outlook, `LoanSocMarginPercent` on the loan — but the floor gate was a bare `soc < floor`, and a
hard stop that skipped the `MinRunTime` dwell timer on the way down.

### The failure mode

A morning that starts just above the floor. SOC arrives as **whole percent**, so the gate turns on a
single count: a cloud or a house-load step the 3-minute surplus average hasn't caught yet takes the
pack from 51 % to 49 %, the session is cut, and 15 minutes later (the `MinPauseTime` dwell) it comes
back at 50 % to do it again. A contactor cycle and a vehicle wake per iteration.

Worse than the churn was an **inverted ordering** against the auto-armed battery hold. The hold arms
at `soc <= floor` and releases at `floor + HoldReleaseMarginPercent` (2 %), while the car resumed at
`floor + 0`. The car therefore came back *while the pack was still held*, and the steady state was not
oscillation at all: SOC pinned to the floor, the car eating the whole surplus, and the grid covering
every dip.

### What was built

- **A resume margin.** `FloorResumeMarginPercent` (default 5 %): charging continues down to the floor,
  a paused session restarts only at `floor + margin`. Runtime-settable from Home Assistant and the web
  UI like the floor itself, and floored at `HoldReleaseMarginPercent` in `ForecastRuntimeSettings` —
  one place, so neither configuration nor an HA number can recreate the inversion. On a 9 kWh pack 5 %
  is ~450 Wh, about nine minutes of a 3 kW surplus going into the battery.
- **The clamp and the trajectory split apart.** `SolarDayPlanner` now reports
  `TrajectorySocFloorPercent` (unclamped) alongside `RequiredSocFloorPercent` (clamped to
  `MinBatterySocFloorPercent`). Falling through the trajectory risks the evening 100 % and still stops
  the session at once; falling through the clamp while the trajectory sits far below — the normal case
  on a sunny morning, when the sun could recover a much deeper discharge — is a preference rather than
  a physics problem, so it goes through `MinRunTime` like any other soft reason and the session is held
  at 6 A instead of being cut short.
- Both floors are published: `soc_floor` and the new `soc_floor_traj`, so the reason a session paused
  is readable from the dashboard rather than from the logs.

- **A guard band, so the pack recovers rather than hovers.** The two changes above stop the flapping
  but not the second failure mode: a car taking every watt the sun makes holds SOC exactly on the floor
  for the whole morning, no cycling at all but no recovery either. While SOC is inside the resume
  margin, `FloorGuardReserveWatts` (default 750) is withheld from the car and the loan is suppressed —
  a loan and a reserve at the same SOC would cancel out, paying a round trip for no net movement. The
  band reuses the resume margin's width rather than adding a second knob to keep in sync with it, and
  by construction only a running session can be inside it: a paused one is held below by the margin.
  On a marginal surplus the reserve stops the session, which is the point — the pack takes the whole
  surplus and the margin keeps the car off until it is clear, converting a dozen short cycles into one
  long one.

### Session samples also record the site and the car (same PR)

The floor work above needed a way to see what actually happened on a real morning, and the session
store only recorded the car's side of it. Five fields were added to every sample:

- `SolarWh`, `GridImportWh` — what the *roof produced* and what the *site imported* since the session
  opened. Neither is derivable from the existing `FromSolarWh` / `FromGridWh`, which are the car's
  attributed share: the difference is what the house and the home battery took. Export is not netted
  off the import total, or a sunny hour would cancel out an expensive evening.
- `ForecastSolarWh` — the same integral over the forecast, so a session carries its own
  forecast-versus-reality line. Null rather than 0 while no forecast has been available for any part of
  the session; 0 would read as "the forecast predicted nothing".
- `VehicleChargeTimeRemainingMinutes`, `VehicleChargeTimeRemainingReported` — the car's **own**
  estimate of the time it still needs, which is a number only the car can produce: it knows its charge
  curve, its taper and its target. Stored as a plain number with a companion flag rather than as a
  nullable, on request: a chart wants a series it can draw. The flag is not optional decoration —
  `0` is also what a car that has finished reports, so without it a feed that publishes nothing is
  indistinguishable from a car saying "done". `VehicleState` itself keeps the codebase's usual
  `null`-means-absent contract; the flattening to `0` + flag happens only at the storage boundary.
- `VehicleSocPercent`, `VehicleSocCapturedAt` — the car's own SOC from the vehicle feed, with the time
  the *car* captured it. The capture time is not optional decoration: the reference feed lags by hours
  (2h and 3.5h both observed), so a SOC stored without it cannot be told from a live reading. A feed
  that goes quiet mid-session leaves the last reading standing rather than blanking the column, since
  samples are forced far more often than the car reports.

The MQTT contract gained one optional key, `charge_time_remaining_minutes`, parsed on the same terms
as everything else there — absent is fine, present-but-unusable is not. A value outside 0–10080 minutes
is rejected with the rest of the payload: negative is nonsense and a fortnight is a unit mix-up
(seconds published as minutes), and either would poison a chart. The documented Home Assistant
templates omit the key rather than defaulting it, since a defaulted `0` would arrive flagged as
reported and read as a finished car.

`ChargingSessionTracker` gained the three integrators and takes the vehicle state the same way it
already takes the forecast — passed in by `SessionRecordingWorker`, not looked up, so the strategy stays
free of both services and the control path acquires no new dependency. Database schema v2 adds the
columns by `ALTER TABLE`; a v1 file upgrades in place on startup and keeps `0` for the totals nothing
integrated at the time. `ChargingSessionDocument.CurrentSchemaVersion` moves to 2 — purely additive, so
a v1 reader is unaffected.

One figure *is* surfaced now, because it answers a question you have while the day is running rather
than afterwards: **Forecast solar power**, the forecast's expectation at this instant, published beside
the measured `Solar power` in Home Assistant (`forecast_solar_w`) and on the web dashboard. Zero rather
than absent when nothing covers the moment, and published in **every** mode — a forecast-versus-reality
comparison that only existed while the `Forecasted` mode was driving would be useless for deciding
whether to select it. The session store keeps its own nullable copy of the same lookup, because after
the event "no forecast" and "the forecast said zero" are worth telling apart.

The rest of the rendering is **deliberately deferred to a later change**, not overlooked: this one lands the capture
path so real sessions start accumulating the data now, and the charts can be built against sessions
that already have it rather than against an empty table. The session detail page still charts
home-battery SOC alone, and no Home Assistant entity was added (this feature is history, not
telemetry).

### Verification performed

- 514 unit tests pass. New coverage: the resume margin (start vs continue at the same SOC, restart once
  met, never demanding more than 100 %), the clamp-vs-trajectory split in both directions and past the
  dwell timer, the guard band (trimmed setpoint inside it, whole surplus above it, a marginal surplus
  handed to the battery, no loan inside it), the planner reporting the two floors, the hold-release
  lower bound in the runtime settings, and the new HA entities. For the session fields: the three
  integrators across a session, export not netted off import, the null-not-zero forecast total, the
  car's SOC with its capture time, a quiet feed leaving the last reading standing, a reading with no
  SOC carrying no capture time either, everything resetting between sessions, the SQLite round trip,
  and a v1 file migrating in place with its existing rows still readable. For the car's estimate:
  parsing it, an absent one, a reported zero kept apart from an absent one, impossible values rejected
  at both ends, and the zero-plus-flag pair surviving the round trip. For the forecast power: the
  discovery config, the payload carrying it beside the measured figure with no plan block present, and
  the dashboard tile in both the populated and the no-forecast case.
- **Not yet verified on real hardware, and this needs a plugged-in car to verify.** Nothing here can
  be confirmed from unit tests or a dry run: the whole point is how the loop behaves against a real
  pack, a real charger and real cloud cover. What to watch on the first marginal morning:
  - the count of `Charging` → `Paused` transitions in one morning — the number this change exists to
    reduce. Anything above two or three means the margin is too small for this site's noise.
  - `soc_floor` against `soc_floor_traj` while the car is charging: on a sunny morning they should be
    far apart, and a pause with them apart should log the "Holding at 6A for the minimum run time"
    reason rather than an outright pause.
  - that the hold releases *before* the car returns — SOC should visibly climb off the floor between
    the pause and the restart, and grid import while the car is charging should not sit at the floor
    for long stretches.
  - the effect on delivered EV energy: the margin and the reserve both cost charging time on marginal
    days by design, and 5 % / 750 W may prove too conservative on this pack. The reserve is the first
    knob to lower if marginal mornings stop reaching the 6 A floor at all.
  - **that the session store's new columns fill in.** The car's SOC in particular: it comes from a
    feed that can be absent, stale or dead, and a session recorded with `vehicle_soc_percent` null
    throughout means the feed, not the store, is what needs looking at.
  - **whether the ID.4 reports a remaining time at all.** The entity name in the documented template is
    unverified against this install, and `volkswagen_connect` does not expose one on every model. A
    session with `vehicle_charge_time_remaining_reported` false throughout means the automation needs
    the right entity, or that this car simply has nothing to say — both fine, neither an error.
  - **that the reserve actually lands in the battery.** It only does if the withheld surplus has
    nowhere else to go; watch `Battery power` climb while the car charges at the trimmed setpoint, and
    watch for export instead, which would mean the reserve is being spilled rather than stored.
  - **grid import during a clamp dip.** Routing the clamp breach through `SoftPause` inherits that
    method's existing trade — up to `MinRunTime` at 6 A — and a clamp dip that coincides with the sun
    disappearing entirely will now hold ~4.2 kW for up to ten minutes, covered by the grid because the
    hold is armed. That is at most ~700 Wh and it is the same bargain the surplus dip already makes,
    but it is the one place this change can cost money rather than save wear. Watch grid import around
    a pause; if it is material, gate the soft path on the live surplus still clearing the 6 A floor.

---

## 2026-08-17 — Vehicle telemetry phase 1: the car's battery SOC over MQTT (issue #73)

The controller can now read the **car's own** battery state — as distinct from the home battery the
inverter reports, and from the charger's view of what is plugged into it. Read-only: nothing in
`ChargeControl` or `BatteryHold` consumes it, and it is not republished to Home Assistant, which is
where it comes from.

### Why MQTT rather than a car API

There is no Volkswagen, Škoda or Tesla integration in this tree, deliberately. The controller
subscribes to **one topic with one JSON schema**, and each car is adapted onto that schema by a
template in Home Assistant.

The reason is that every source spells the same facts differently — the VW EU Data Act portal emits
`CHARGE_STATE_CHARGING_HV_BATTERY`, `volkswagen_connect` emits `notReadyForCharging`, an OBD dongle
emits raw CAN values — and all of them are reverse-engineered against backends that change. In May–June
2026 Volkswagen retired the WeConnect OAuth client and put the CARIAD token exchange behind app
attestation, breaking `volkswagencarnet`, `evcc` and `openWB` outright. Owning a client here would mean
chasing that forever, in a codebase whose stated point is not depending on clouds. Owning a *schema*
instead means a second vehicle costs no code: an Elroq, a Tesla or a Kia is a copied automation.

### What was built

- `Gleanvolt.Core`: `VehicleState`, `VehicleChargeState`, `VehiclePlugState`, `IVehicleTelemetry`,
  `VehicleStateHolder`.
- `Gleanvolt.Infrastructure`: `Vehicles/VehicleTelemetryPayload` — the wire contract and its parser,
  pure so it is testable without a broker.
- `Gleanvolt.Hosting`: `Configuration/VehicleOptions`, `Vehicles/VehicleMqttWorker`.
- `Gleanvolt.Web`: a dashboard section showing SOC *and the reading's age*, plus `VehicleDisplayOptions`
  — the same arrangement as `WebBuildInfo`, since the UI cannot see the host's options classes.

MQTTnet was already referenced by `Gleanvolt.Hosting`, so no project gained a package.
`IVehicleTelemetry` copies `ISolarForecastService`'s contract — fed asynchronously, answers
synchronously — so the poll loop never does I/O for this.

### Architecture decisions

- **Everything except `CapturedAt` is optional.** No two sources report the same set, and absent is
  `null` or `Unknown`, never zero — a zero SOC would read as a flat battery. `CapturedAt` is mandatory
  because it is the *car's* capture time rather than the arrival time, and staleness cannot be judged
  without it.
- **The parser's rule is: absent is fine, present-but-unusable is not.** `"soc_percent": "unavailable"`
  is exactly what a Home Assistant template emits when its source entity drops out. Rejecting the whole
  payload leaves the last good reading in place with a visibly growing age — a diagnosable state —
  instead of half-trusting junk. Unrecognised *enum* values are the one exception and map to `Unknown`,
  because those vocabularies are open-ended and an unfamiliar charge state should not cost us the SOC.
- **A single concrete topic, not a `+` wildcard.** A wildcard across several cars would silently let the
  newest message win regardless of which car it described. Multi-vehicle needs an explicit
  active-vehicle selector, which is a later phase.
- **The publisher must retain.** A controller restart is then handed the last reading on subscribe
  rather than waiting up to a quarter of an hour for the source's next update.
- **`Vehicle__BrokerHost` is overridable**, unlike the pinned `HomeAssistant__BrokerHost`. The car's
  data is *published* by Home Assistant, and under `deploy-controller-only.sh` there is no `mosquitto`
  on the compose network at all, so "the broker beside us" is the common case rather than the only one.

### Why it is advisory only, and always will be

Measured on the reference install rather than assumed:

- A parked car's report was **2 hours** old on one reading, **3.5 hours** on another, and **10 hours**
  on the first one the controller actually received. Hours stale is the normal case — and not a problem,
  since a parked car's SOC does not drift.
- The upstream `volkswagen_connect` session **expired after roughly 15 hours**, taking every entity to
  `unavailable`. Recovery needed a human re-entering a password plus an email OTP; it cannot self-heal.
- **Target SOC is not reliably available at all.** The portal's field is
  `batteryStatus.navigationTargetSOC_pct` and is null unless the car has an active charge plan; the EU
  Data Act equivalent (`settings.target_soc`) needs a separate continuous data request that VW can take
  days to start filling.

So `MaxAge` (12 h by default) exists to catch a **dead feed**, not to reject merely old numbers, and a
charge target stays configuration rather than something read from the car. Phase 2 inherits that
constraint: anything that writes to hardware must behave identically when this feed is absent, stale or
gone.

### Verification performed

- 474 unit tests pass, 0 build warnings. The parser's cover offset preservation, absent versus
  present-but-unusable SOC, epoch-number rejection, case-insensitive enum names, unknown-enum
  tolerance, forward compatibility with unrecognised properties, and non-object payloads. The dashboard
  tests cover the section being hidden entirely when nothing has reported, the stale marker past
  `MaxAge`, and a 3.5-hour reading *not* being called stale.
- **End to end against the live ID.4 on the Pi**, with the Home Assistant automation publishing
  retained:

  ```
  [22:44:05 INF] Vehicle telemetry enabled; broker mosquitto:1883,
                 topic gleanvolt/vehicle/id4/state, max age 12:00:00.
  [22:44:05 INF] Subscribed to vehicle telemetry on gleanvolt/vehicle/id4/state.
  [22:56:17 INF] First vehicle reading from gleanvolt/vehicle/id4/state:
                 SOC=28% charge=Idle plug=Disconnected captured 2026-08-17T10:44:23+00:00
  ```

  Parsed on the first attempt with zero rejected payloads. `charge=Idle` confirms the template mapped
  VW's `notReadyForCharging` onto the normalised vocabulary, and the `+00:00` offset survived intact —
  taken from `last_vehicle_report`, not the arrival time. The reading was over ten hours old when
  received, which is correct for a car parked all day and is precisely the case the design exists for.

### Not done / open

- Nothing consumes it. `ChargeControl` and `BatteryHold` are untouched.
- No per-vehicle capacity and no kWh conversion, so no "kWh to deliver by departure" — that is phase 2,
  and it is the point at which the unavailable target SOC starts to matter.
- No active-vehicle selector, so a second car would need one before it could share the charger.
- No Home Assistant entity for any of this, on purpose: Home Assistant is the source.

### Files

- `src/Gleanvolt.Core/Models/VehicleState.cs`, `VehicleStateHolder.cs`
- `src/Gleanvolt.Core/Enums/VehicleChargeState.cs`, `VehiclePlugState.cs`
- `src/Gleanvolt.Core/Interfaces/IVehicleTelemetry.cs`
- `src/Gleanvolt.Infrastructure/Vehicles/VehicleTelemetryPayload.cs`
- `src/Gleanvolt.Hosting/Configuration/VehicleOptions.cs`, `Vehicles/VehicleMqttWorker.cs`
- `src/Gleanvolt.Web/VehicleDisplayOptions.cs`, `Components/Pages/Dashboard.razor`
- `deploy/docker-compose.yml`, `deploy/.env.example` (PR #75 — the `Vehicle__*` pass-throughs, without
  which the setting was unreachable on the Pi)

---

## 2026-08-16 — Third-party notices gain the register-map source; releases stop assuming a feed

Two follow-ups, neither of which changes any behaviour.

### The register maps are attributed

`THIRD-PARTY-NOTICES.md` covered every NuGet dependency but not the one piece of *derived* material in
the tree: register addresses, the Power Control block's field layout and several enumerated control
values were read from [wills106/homeassistant-solax-modbus](https://github.com/wills106/homeassistant-solax-modbus)
(Apache-2.0, Copyright 2025 William Swann), because no SolaX document describing them is public. The
individual files have always said so; the notices file now does too, under its own heading, since it
is not a dependency and does not belong in the components table.

Individual addresses are facts about hardware and the implementation here is independent. The
attribution is given anyway: honouring the upstream licence costs nothing, and the EU database right
protects the investment in assembling and verifying a map like that independently of copyright.

### Releases produce artifacts, not feed pushes

`publish-packages.yml` became `release.yml`, and the `dotnet nuget push` step is gone along with its
`NUGET_API_KEY` requirement. A `v*` tag now produces a GitHub Release carrying:

- **Self-contained builds** for `win-x64`, `linux-x64` and `linux-arm64`. Self-contained means the
  runtime ships inside, so a Windows user who has never heard of .NET can unzip and run it — the gap
  the container images cannot fill. Each zip also carries `LICENSE`, `LICENSE-MIT`,
  `THIRD-PARTY-NOTICES.md` and the README, because a downloaded zip is the artifact least likely to be
  traced back to the repository.
- **The four `.nupkg` files**, attached rather than pushed. Packing stays because it is a cheap check
  that the package metadata is coherent, and because it produces the artifact if a feed is ever
  wanted. A consumer takes this repository as a submodule instead: no feed, no credentials, and the
  submodule commit pins the version exactly.

Trimming is off deliberately — the configuration binder and options types resolve reflectively, and a
trimmed build fails at startup rather than at compile time.

### Verification

All three runtime identifiers publish cleanly with the correct native SQLite (`e_sqlite3.dll` on
Windows, `libe_sqlite3.so` on arm64) and the Blazor static assets present. The `linux-x64` build was
then run directly from its publish directory: it logs `Gleanvolt 0.0.0-dev (ea806e3) starting.`, binds
its port and serves `blazor.web.js` at 200,645 bytes and the stylesheet at full size — static web
assets in a self-contained publish being exactly the thing that fails silently.

## 2026-08-16 — Renamed to Gleanvolt

A rename, not a refactor: no behaviour, configuration or feature change. See DECISIONS.md for why the
vendor name stays where it does.

### What moved

Five projects and four test projects, `Solax.*` → `Gleanvolt.*`, with namespaces to match.
`SolaxLocalController.slnx` → `Gleanvolt.slnx`. `SolaxControllerHostingExtensions` →
`GleanvoltHostingExtensions`, and its two public entry points to `AddGleanvolt()` / `UseGleanvolt()`.
`SolaxPollingService` → `PollingService`, since it is the application's main loop rather than a vendor
adapter. The Razor class library's static assets move with the assembly, so the UI now serves
`_content/Gleanvolt.Web/...`.

Infrastructure: image `ghcr.io/mpospisil/gleanvolt`, containers `gleanvolt-*`, deploy directory
`/opt/gleanvolt`, log files `gleanvolt-.log`. The repository was renamed in place; the image name
follows it automatically because the workflow reads `${{ github.repository }}`.

### What deliberately did not move

`SolaxOptions`, the `Solax:` configuration section, the `SOLAX_*` environment variables, the register
maps and the vendor register enums. Those describe SolaX hardware, not this product, and a second
vendor will sit beside them rather than replace them.

Home Assistant's `DeviceId` and `BaseTopic` are unchanged, so no entity is renamed and no history is
orphaned; only the display name becomes "Gleanvolt". `HaDiscoveryTests` needed no edit at all, which
is the clearest confirmation the seam held.

### Verification

434 tests pass. `dotnet pack` produces `Gleanvolt.*` packages carrying the licence files. The Linux
image builds, starts, logs `Gleanvolt 0.0.0-dev starting.`, and serves `blazor.web.js` plus every
`_content/Gleanvolt.Web/` asset at full size — the RCL asset path was the most likely thing to break
silently, because a wrong path there returns an empty 200 rather than a 404.

Files: everything except `docs/DECISIONS.md` and `docs/IMPLEMENTATION_LOG.md`, which are append-only
records of how the tree was at the time.

## 2026-08-16 — Solax.Hosting: the composition root leaves the executable (#66)

A pure refactor — no behaviour, configuration or feature change. `Program.cs` was 399 lines of DI and
the stack could only be consumed by running `Solax.Worker`, even though only two files in that project
were host-specific.

### What moved

`src/Solax.Hosting/` is a new class library between `Solax.Infrastructure` and `Solax.Worker`. It
took 22 files unchanged apart from their namespace: `SolaxPollingService`, `ChargingControlCoordinator`,
`ChargeControlCycleResult`, `ChargeControlModeSelector`, `BatteryHoldSelector`,
`SolarForecastRefreshWorker`, `HostShutdown`, `NoListenServer`, `BuildInfo`, and the `Configuration/`,
`Forecasting/`, `HomeAssistant/` and `Sessions/` folders whole. `tests/Solax.Worker.Tests` became
`tests/Solax.Hosting.Tests` with it — every type it covers moved, and the tests themselves needed only
the namespace.

`SolaxControllerHostingExtensions` is the one new file: `AddSolaxController()` carries every
registration from `Program.cs`, comments and all, since the comments explaining *why* a registration
is shaped the way it is are the most valuable part of what moved.

### What stayed

`Solax.Worker` is now `Program.cs`, `DotEnv.cs` and `appsettings.json`: the `.env` load, Serilog's
configuration and `SelfLog`, the `hash-password` tool, the startup log lines, the Windows timezone
warning and the exit code. ~95 lines. It references `Solax.Hosting` and nothing else, so nothing in
the host can reach past the composition root.

### Three things that needed more than a move

- **The implicit usings.** The moved code was written against the Web SDK's; a plain class library
  supplies only the `System` ones. Re-declared as `<Using />` items in the `.csproj` rather than as 22
  files of `using` directives.
- **The Kestrel port.** `builder.WebHost.ConfigureKestrel(...)` became
  `services.Configure<KestrelServerOptions>(...)` — the same registration, but callable without a
  builder, which is what lets a non-web host use `AddSolaxController` too.
- **`UseStaticWebAssets()`.** The only thing that cannot be a registration, so it is the entire body
  of the `WebApplicationBuilder` overload.

### Packaging

`Directory.Build.props` gained the shared package metadata with `IsPackable` defaulting to `false`;
the four libraries opt in, `Solax.Worker` stays out. `Directory.Build.targets` is new, and holds the
parts conditioned on `IsPackable` (the README and the licence text in the package, XML docs with
CS1591 silenced) because `.props` is imported before a project sets that property. The licence goes in
as `PackageLicenseFile` rather than an SPDX expression, so the terms travel inside the artifact and
the package says whatever `LICENSE` says without a second place to keep in step. `.github/workflows/publish-packages.yml`
packs and pushes on a `v*` tag using the same version derivation as the image workflow; it needs a
`NUGET_API_KEY` secret and fails loudly rather than silently if one is missing.

### Verification

434 tests pass unchanged. `dotnet pack` produces the four packages and their symbol packages. Run
against the live install: the UI comes up on 8090 and serves `blazor.web.js` (200,645 bytes) and every
`_content/Solax.Web/` asset, and with `Web__Enabled=false` nothing listens on 8090 and no "Web UI
enabled" line appears — the two properties the split was most likely to break.

Files added: `src/Solax.Hosting/*`, `Directory.Build.targets`,
`.github/workflows/publish-packages.yml`.
Files changed: `src/Solax.Worker/{Program.cs,Solax.Worker.csproj}`, `Directory.Build.props`,
`SolaxLocalController.slnx`, `Dockerfile`, `Dockerfile.windows`, `README.md`, `docs/DECISIONS.md`.

## 2026-08-16 — The service can be stopped from the UI and from Home Assistant, and stays stopped

Killing the controller was the only way to take it down, and the cost of that is one specific thing:
`SolaxPollingService.StopAsync` pauses the charger on the way out, so a killed process leaves the car
drawing at the last current we wrote. Everything else the graceful path does — closing the open
session as `ServiceStopped`, flushing the store, disconnecting MQTT — was already implemented and
already correct. **No shutdown logic was added; only a way to ask for it that isn't an ssh session.**

### One seam, two surfaces

`Solax.Core/Interfaces/IServiceShutdown.cs` — `RequestStop(string source)` — sits alongside
`IChargeControlModeSelector`: framework-free, driven by both control surfaces, owned by neither.
`Solax.Worker/HostShutdown.cs` implements it over `IHostApplicationLifetime.StopApplication()` and
records that the stop was asked for.

- **Web UI.** `Health.razor` gets a *Service* section with a two-click **Stop service** control. Two
  clicks because it is one-way from the browser: the page goes down with the service. The final
  render carries the command that starts it again, since it is the last thing the user will see.
- **Home Assistant.** A `button` entity, `homeassistant/button/solax_controller/stop_service/config`,
  published unconditionally with `entity_category: "config"` so it lands in the device's
  configuration panel rather than on the auto-generated dashboard card. Only an exact `PRESS` payload
  acts (`HaDiscovery.IsPress`) — the topic is the one command that cannot be undone from HA, so a
  stray retained message must not be able to take the controller off the air. The handler publishes
  nothing afterwards: the shutdown it starts disposes the MQTT client, and racing a publish against
  that only produces a logged failure in the last second of the run. The acknowledgement the
  dashboard actually sees is the existing availability topic going `offline`, which
  `MarkOfflineAndDisconnectAsync` already publishes.

### The exit code is the mechanism, not a detail

`restart: unless-stopped` restarts on *any* exit, so under it a Stop button is a Restart button. The
controller therefore moves to `restart: on-failure` and chooses its exit code: `0` for a requested
stop (stays down), `HostShutdown.TerminatedExitCode` = 143 for a SIGTERM (reboot, daemon restart —
comes back), non-zero for a crash or a power cut (comes back). See DECISIONS.md for why the reboot
case is worth the extra machinery on this particular box.

`stop_grace_period: 60s` goes with it, and the shutdown pause is separately bounded to 10s
(`ChargingControlCoordinator.DefaultShutdownPauseTimeout`). Both numbers come from measuring a real
stop rather than guessing — see "What a real stop measured" below.

### The log now marks the end of a run

Caught by using the feature on a real run: the stop worked, but the log just *ended* after the last
poll line — nothing said the process had stopped rather than died. `HostShutdown.LogWhenStopped()`
hooks `ApplicationStopped` (not after `Run()`, where Serilog has already gone down with the service
provider) and writes one of two lines, naming the requester and the exit code:

```
SolaX Local Controller stopped cleanly at the request of Web UI. Exiting with code 0: ...
SolaX Local Controller stopped cleanly after a termination signal. Exiting with code 143: ...
```

A log ending without either is a run that died. On the Pi that is the only evidence there is — the
journal is RAM-only, and the box hard-stops on its own.

### What a real stop measured

Stopping the service from the web UI on a live system took **19.3 seconds**, and two more poll cycles
ran after the request. The log explains it without instrumentation: a poll that started at 09:53:37.8
did not finish until 09:53:58.5 — 20.7 seconds — and returned `EvMode=n/a EvCurrent=n/a`, the
absent-charger path. Each unanswered read costs the 5-second Modbus connect/IO timeout, and a read
already in flight cannot observe the shutdown's cancellation until it returns.

Nothing was wrong with the stop itself, but two numbers were wrong:

- **`stop_grace_period` 30s → 60s.** 19s of it went on a shutdown with *nothing charging*.
- **The pause now has its own 10s deadline.** This is the case that actually bites: with a session
  active, `PauseOnShutdownAsync` does a read *and* a write against that same silent charger, so the
  budget can run out mid-write — SIGKILL, car still drawing. It now gives up on a stated deadline and
  logs "Gave up pausing the charger on shutdown after 00:00:10 — it is not answering", which is at
  least actionable. Covered by a test that would hang rather than fail if the bound regressed, so it
  uses `WaitAsync`.

### Two things the tests would not have caught

**Resolving from `host.Services` after `host.Run()` aborts the process.** `Run()` disposes the service
provider on its way out, so the first version — `return host.Services.GetRequiredService<HostShutdown>().ExitCode;`
— threw `ObjectDisposedException` and produced exit **134, core dumped**, on every SIGTERM. Found by
running the built binary and sending it a signal, not by any unit test; the fix is to resolve before
`Run()` and hold the reference.

**Docker's restart semantics were verified rather than assumed** (Docker 29.7.1): exit 0 under
`on-failure` stays exited (`restartCount=0`), exit 143 is restarted, `unless-stopped` restart-loops on
exit 0, and a `docker compose stop` stays stopped whatever the code. The whole feature rests on the
first two.

The end-to-end path was then exercised against a real broker: discovery config published, `ON` on the
stop topic rejected with the service still running, `PRESS` accepted → graceful shutdown → **exit 0**.

### Files

| File | Change |
|---|---|
| `src/Solax.Core/Interfaces/IServiceShutdown.cs` | new — the seam |
| `src/Solax.Worker/HostShutdown.cs` | new — implementation, exit-code decision |
| `src/Solax.Worker/Program.cs` | registration; resolve before `Run()`; return the exit code |
| `src/Solax.Worker/HomeAssistant/HaDiscovery.cs` | stop-service topic, button config, `PRESS` parsing |
| `src/Solax.Worker/HomeAssistant/HomeAssistantMqttWorker.cs` | subscribe and handle the press |
| `src/Solax.Web/Components/Pages/Health.razor` | the Service section |
| `src/Solax.Web/wwwroot/css/site.css` | `.danger` / `.secondary` buttons, `.stopping` |
| `deploy/docker-compose.yml` | `restart: on-failure`, `stop_grace_period: 60s` |
| `README.md`, `deploy/README.md`, `docs/DECISIONS.md` | the entity row, the stop/start runbook, the record |
| `tests/…` | `HostShutdownTests`, button + payload cases in `HaDiscoveryTests`, the confirm flow in `HealthPageTests` |

---

## 2026-08-14 — The repo builds and tests clean on a non-English Windows machine

No control logic changed, and nothing here alters production behaviour on the Pi. Three unrelated
things made the repo hostile to a Windows development machine, and two of them could not reproduce on
Linux or in CI by construction — which is the reason they are worth a log entry rather than just a
commit message.

### Number formatting inherited the OS locale

`Solax.Web` renders kWh, watts and percentages, and nothing pinned a culture, so formatting followed
whatever the host OS ran under. On a cs-CZ machine the decimal separator is a comma: the dashboard
showed `6,00 kWh`, and every bUnit assertion written against `6.00` failed. The figures are units,
not localized prose — there is no translation story for this dashboard — so the target is invariant
culture rather than a configured one.

The fix is a `[ModuleInitializer]` in `Solax.Web/CultureConfiguration.cs` setting
`CultureInfo.DefaultThreadCurrentCulture` and `DefaultThreadCurrentUICulture` to invariant.
Deliberately **not** in the entry point: `Solax.Web` is loaded by two hosts — `Solax.Worker` in
production and `Solax.Web.Tests` under bUnit — and an initializer in `Program.cs` would never run for
the test host, leaving exactly the assertions that caught this still broken. That placement is what
CA2255 warns about, so the suppression carries that reasoning inline.

### SQLite held the database file open past `Dispose`

`Microsoft.Data.Sqlite` pools connections, and the pool keeps the native file handle open after every
`SqliteConnection` using it has been disposed. Unix lets you delete a file out from under an open
handle; Windows refuses. So a test that disposed the store and then deleted its database — which is
what the suite does between cases — failed with "file in use" on Windows and passed everywhere else.

`SqliteChargingSessionStore.Dispose` now calls `SqliteConnection.ClearPool`, releasing the handle
deterministically instead of at finalization. Production impact is nil: the store lives for the
lifetime of the process and the pool dies with it. This is a test-cleanup fix that happens to belong
in the production type, because the pool is the production type's to release.

### The web UI could not be reached in local development

`Web:Enabled` is `false` and `Web:RequireAuthentication` is `true` in the shipped `appsettings.json`,
which is the right production posture and the wrong one for a developer who just wants to look at the
page: it means no listening socket at all, and then a password hash requirement on top.

Rather than weaken the shipped defaults, the overrides go in `appsettings.Development.json`
(`Web:Enabled` true, `Web:RequireAuthentication` false) so they apply only under the Development
environment. That file was previously not loaded when debugging at all — `.vscode/launch.json` set
`DOTNET_ROOT` but never `DOTNET_ENVIRONMENT`, so F5 ran as Production. Setting it there is what makes
the override reachable, and brings the debugger in line with `dotnet run`, which already gets
Development from `Properties/launchSettings.json`.

## 2026-08-13 — The controller can serve its own UI, and is still a headless worker when it doesn't

Phase 0 of [#44](https://github.com/mpospisil/solax-controller/issues/44) ([#45](https://github.com/mpospisil/solax-controller/issues/45)):
scaffolding only. Nothing a user would call a feature ships here — one diagnostic page does — but the
seam the remaining six phases hang off is now proven end to end.

### What moved, and why only this

`ChargeControlStatusHolder` moved from `Solax.Worker` to `Solax.Core.Models`. Its own doc comment
always said it existed "so the reporting layer can publish it without being coupled to the polling
loop", with Home Assistant named as an example rather than the only consumer; the second consumer has
now arrived, in an assembly that must not depend on the host. It is a plain object with an event, so
Core's no-framework-dependencies rule needed no exception.

`ChargeControlCycleResult` shared that file and did **not** move: it is how the poll loop talks to
itself, has no consumer outside `Solax.Worker`, and belongs where it is. It now has its own file.

No control logic moved. Nothing in `Solax.Core` changed semantically.

### The host is a web host that is usually not a web server

`Solax.Worker` switched to `Microsoft.NET.Sdk.Web` and `WebApplication.CreateBuilder`. Every existing
`AddHostedService` registration is untouched, and the poll loop, MQTT worker and session recorder are
unaware any of this happened.

The interesting half is the off switch. **An ASP.NET host with no endpoints mapped still listens** —
Kestrel falls back to its default address when nothing configures an endpoint, which on a LAN
appliance means a port the operator never asked for and cannot find in any config file. So with
`Web:Enabled` false the host registers `NoListenServer` in Kestrel's place: it starts nothing, binds
nothing and accepts nothing. Registration order does the work — Kestrel registers itself with
`TryAdd` during `CreateBuilder`, and the last `IServer` registration is the one resolved.

When the UI *is* enabled, the port comes from `Web:Port` via `ConfigureKestrel(...ListenAnyIP(port))`
rather than `ASPNETCORE_URLS`. A code-backed endpoint outranks the hosting addresses, so an inherited
environment variable cannot quietly move the UI somewhere else.

### The quirk that cost the most time

`_framework/blazor.web.js` 404s, and everything else looks fine.

Pages prerender correctly, the markup is right, the CSS loads, and the browser shows a static page
that never updates — with the only evidence being a failed request in the developer console. The
cause is in the SDK: `Microsoft.NET.Sdk.Web.ProjectSystem.targets` infers Blazor support from the
host project *containing `.razor` content items*, and this host deliberately contains none — every
component lives in `Solax.Web`. Without that inference the `Microsoft.AspNetCore.App.Internal.Assets`
package is never referenced and the script is never published.

The fix is one property in `Solax.Worker.csproj`, `RequiresAspNetWebAssets=true`, which is exactly the
knob the SDK exposes for this case. Recorded in `docs/DECISIONS.md` because it is invisible in code
review and would otherwise be rediscovered on the Pi.

### The second quirk, found by actually opening the page

The first one has a sibling, and it is worse because the first fix hid it. With
`RequiresAspNetWebAssets` in place the script is published and `GET /_framework/blazor.web.js`
answers **200 — with an empty body**. Not a 404, not a log line, not a console error: the page
renders perfectly, and then never updates, because the script that would have opened the circuit
arrived zero bytes long.

The cause is that assets living in build output rather than in a `wwwroot` folder — the library's
stylesheet and Blazor's own script — are only wired into the file provider automatically in the
**Development** environment. `dotnet run` outside it (no `DOTNET_ENVIRONMENT`, i.e. Production from
source) has no `wwwroot` to fall back on and serves nothing, successfully. `builder.WebHost
.UseStaticWebAssets()` asks for them explicitly; in a published app the manifest it reads does not
exist and the call does nothing, so it is free.

Worth recording how it was missed: the first verification pass checked HTTP **status codes**, and
all three assets returned 200. A status code is not content. The checks below now compare
`%{size_download}` against the file on disk.

`Solax.Web` also takes a `FrameworkReference` on `Microsoft.AspNetCore.App` instead of the
`Microsoft.AspNetCore.Components.Web` package the RCL template offers: these components only ever run
server-side, so WebAssembly compatibility buys nothing and a duplicate assembly costs.

### The image had to change with the code

Both Dockerfiles now build on `dotnet/aspnet` rather than `dotnet/runtime`, and both copy the new
project file in the restore layer. This is not deferrable to the phase that adds compose profiles:
the framework reference is a property of the build, so a `dotnet/runtime` image would fail to start
the moment this merges, `Web:Enabled` or not. It costs roughly 25 MB of image on every platform.

### What the one page does

`/health` reports the running build, the configured time zone and the last completed poll. Every
value on it comes from the host's container — `WebBuildInfo` handed over by the composition root, the
zoned `TimeProvider`, and the status holder itself — which is the point: it proves DI reaches a
component in another assembly and that the component sees the same live objects the MQTT worker does.

It subscribes to `ChargeControlStatusHolder.Updated` rather than sampling it, so the timestamp
advances on its own as each poll lands. That makes it a real liveness check: a page that sits still
means the poll loop has stopped while the web host is fine.

### Verification performed

- **335 tests pass**, 12 of them new. The existing projects were not touched beyond the holder's
  namespace.
- **The seam test can fail.** `Follows_the_holder_instead_of_sampling_it_once` was re-run with the
  subscription commented out and does fail — a live-update test that passes either way is worthless.
- **Enabled, from source:** `blazor.web.js` and the library's stylesheet serve their full 200,645
  and 1,539 bytes (byte counts, not status codes — see above), and `POST /_blazor/negotiate` answers
  200 offering WebSockets. `ss -ltnp` shows one socket, on the configured port, and nothing else.
- **Driven in a real browser**, headless Chrome over CDP: the page renders styled, `Blazor` is
  defined, no console errors, and the "last poll" line advances — `00:27:27` to `00:27:40` on one
  page that was never reloaded. That is the seam working end to end: Modbus poll, holder, event,
  circuit, DOM. Checked in all three run modes (published output, Development, Production from
  source), because only the first two worked before `UseStaticWebAssets`.
- **Disabled:** `ss -ltnp` shows **no** listening socket in the process, while the poll loop keeps
  logging telemetry normally.
- **Published output, not just `dotnet run`:** static web assets resolve differently once published,
  so the same three URLs were re-checked against `dotnet publish` output. All 200.
- The `Timestamp` shown is formatted in the app's zone, not the machine's — the test asserts a Prague
  wall-clock time against a UTC status, which is the mistake it exists to catch.

**Not verified:** anything on the Pi. No container was built or run, so the aspnet base image, the
arm64 build and the real memory cost of a live circuit are all still on paper. Phase 6 owns the
compose profiles and the deploy documentation; nothing in the deploy stack changes yet, because the
UI is off by default.

**Files changed:** `src/Solax.Core/Models/ChargeControlStatusHolder.cs` (moved),
`src/Solax.Worker/ChargeControlCycleResult.cs` (extracted), `src/Solax.Worker/Program.cs`,
`src/Solax.Worker/NoListenServer.cs`, `src/Solax.Worker/Solax.Worker.csproj`,
`src/Solax.Worker/appsettings.json`, `src/Solax.Worker/HomeAssistant/HomeAssistantMqttWorker.cs`,
the new `src/Solax.Web/` project, the new `tests/Solax.Web.Tests/` project,
`SolaxLocalController.slnx`, `Dockerfile`, `Dockerfile.windows`, `README.md`, `docs/DECISIONS.md`.

## 2026-08-12 — The deploy guide described a Pi that no longer exists

Documentation only; no code changed. `deploy/README.md` was written against a Pi 3 B rooted on a
single USB SATA SSD running Bookworm. The host is now a Pi 3 **B+** on Trixie with a split boot, and
two of the nine preparation steps had become wrong rather than merely stale.

**Step 4 fails outright on Trixie.** It ran `dphys-swapfile swapoff`, and Raspberry Pi OS 13 does not
ship that package — the step dies with `command not found` before doing anything. The premise was
also false: the image already has swap. `/proc/swaps` shows `/dev/zram0`, ~905 MB, priority 100 —
zstd-compressed swap in RAM, with writeback to `/var/swap` through
`rpi-setup-loop@var-swap.service`. That `/var/swap` file is not a leftover to clean up; it is zram's
backing store, which is why `swapon --show` lists only the zram device. The step now verifies swap
instead of building it, and the optional disk swapfile it offers is explicitly gated on not being
SD-rooted.

**Step 3 edits a device the document never mentioned.** The cgroup instructions write
`/boot/firmware/cmdline.txt`, which on this host is the **SD card**, not the disk the OS runs from.
The M.2 still carries its own boot partition from the original image; editing that one is completely
silent. A new *Storage layout* section states the arrangement up front and step 3 links to it.

**Hardware quirk that forced the layout.** The Pi 3 boot ROM supports only USB Bulk-Only Transport
and gives the device roughly two seconds to enumerate. A Crucial P1 NVMe behind a Realtek RTL9210
bridge does not answer in time, so the board will not boot from it even with a byte-perfect image —
verified with a freshly flashed card whose `cmdline.txt` named a PARTUUID that existed at the time.
The Linux kernel drives the same adapter fine, so the SD boots and hands root to the NVMe. Recorded
in `docs/DECISIONS.md`.

**Also corrected:** the board is a Raspberry Pi 3 Model B+ Rev 1.3, not a Pi 3 B, in the prose and
the diagram. The distinction matters beyond pedantry — USB-boot OTP is set at the factory on a B+ and
must be burned by hand on a B, so the two boards fail to boot from USB for entirely different
reasons.

**Verified on the live host before writing any of it:** `/proc/swaps` and the `zram`/`rpi-setup-loop`
units; `findmnt` confirming `/` on `/dev/sda2` and `/boot/firmware` on `/dev/mmcblk0p1`;
`/proc/device-tree/model`; and that `cmdline.txt`, `fstab` and the on-disk PARTUUIDs all agree after
the first-boot resize renumbered them.

**Not verified:** whether `program_usb_timeout=1` or a powered hub would make the RTL9210 bootable.
Both are mentioned as untested options rather than recommendations.

**Files changed:** `deploy/README.md`, `docs/DECISIONS.md`, `docs/IMPLEMENTATION_LOG.md`.

---

## 2026-08-11 — The Windows entry in the manifest list had no build number

Found by inspecting the images the pipeline actually published rather than by reading the workflow.
The multi-platform index was correct in every visible way — `linux/amd64`, `linux/arm64` and
`windows/amd64` all present, every tag resolving, the amd64 image running and reporting the right
commit — but the Windows entry read:

```json
"platform": { "architecture": "amd64", "os": "windows" }
```

No `os.version`. `docker buildx imagetools create` drops it when it composes an index from tags, and
that field is how a Windows host picks an image it can actually run. With one Windows entry the
practical harm is small; with two — the `ltsc2022` + `ltsc2025` option considered when this was
designed — there would be nothing left in the index to tell them apart.

**`docker manifest` replaces `imagetools create` in the manifest job**, purely because it can set the
field: `manifest create`, then `manifest annotate --os-version`, then `manifest push`. The cost is a
push per public tag instead of one for all of them, which at this number of tags is not worth
optimising away.

**The build number is read off the built image, not hardcoded.** The Windows job now runs
`docker inspect --format '{{.OsVersion}}'` and exports it as a job output. A literal per ltsc release
would go stale on every base-image patch, and a stale value here would be worse than the missing
field it replaces — it would assert a specific host compatibility that is not true.

**The real lesson is that a green run proved nothing.** This defect shipped in the first release and
survived a second, because everything the workflow checked was fine. So the manifest job now ends by
asserting what it just published: every required platform present, and the Windows entry carrying a
non-empty `os.version`. `.github/scripts/verify-manifest.py` is a file rather than a heredoc — the
first attempt embedded it in the YAML and its unindented lines silently broke the block scalar, which
is its own small argument for not writing programs inside workflow files.

**Verified before pushing:** the script was run against the currently-published manifest, where it
correctly fails with exit 1 on the missing `os.version`; and against three fixtures covering a good
index, a Windows entry without the field, and a missing architecture. The good fixture also confirms
attestation manifests (`unknown/unknown`) are skipped rather than counted as platforms.

**Not verified:** the `docker manifest annotate` path itself, which needs a Windows image built on a
Windows runner. The next push to `main` exercises it — and unlike the daemon fix, this one cannot
pass silently, because the verify step fails the build if the annotation did not take.

Files added: `.github/scripts/verify-manifest.py`.
Files changed: `.github/workflows/publish-image.yml`.

---

## 2026-08-10 — The deploy path said `IMAGE_TAG=v1.0.0`, which would not have worked

An audit of the Raspberry Pi deployment files against the naming introduced in #35, prompted by the
question of whether they had kept up. Mostly they had. One thing had not, and it was the kind that
only fails at the moment you need it.

**`deploy/README.md` documented `IMAGE_TAG=v1.0.0` for deploying a release.** No such image exists.
Releases are cut as git tag `v1.0.0`, and `docker/metadata-action`'s `type=semver,pattern={{version}}`
strips the prefix, so the published image is `…/solax-controller:1.0.0`. The documented command would
have failed on the Pi with `manifest unknown` — during a rollback, which is exactly when nobody wants
to debug a tag string. The same file's own tag table two paragraphs later listed `1.0.0` correctly,
so the document contradicted itself; verified the stripping behaviour against the action's docs
rather than trusting either version of the file.

Both occurrences fixed, and the distinction is now stated where it can be tripped over: `v1.0.0` is
the git tag you check out, `1.0.0` is the image tag you pull. The one command that legitimately
contains both now says so inline.

**`deploy/.env.example` still described the pre-#35 world** — only `latest` and `sha-<short>`, no
released versions, no hint that the tag is platform-independent now. It is the file an operator
actually edits on the Pi, so it is the one place the scheme most needs to be right. It now lists all
three forms, carries the same no-`v` warning, and mentions the single-platform escape hatch for when
one platform holds up a release.

**`deploy/deploy.sh`'s usage header** gained the release form and a note that `IMAGE_TAG` names an
image tag rather than a git tag.

**The Linux `Dockerfile` header still read as the arm64 Dockerfile** — accurate about its mechanism,
but written when arm64 was the only target. It now says it builds both Linux architectures, points
at `Dockerfile.windows` for the OS it cannot build, and gives both `--platform` examples.

**Checked and correct, no change needed:** `deploy/docker-compose.yml`'s
`image: …/solax-controller:${IMAGE_TAG:-latest}` works unchanged with every new tag form and needs no
`platform:` key, because a manifest list resolves per host. The older `2026-08-02` decision record
still says CI builds `linux/arm64`; that file is append-only and the `2026-08-10` record supersedes
it, so it stays as written.

**A second pass checked the documentation against the code rather than against itself.** Three things
came out clean and are worth recording as checked, since "still accurate" is invisible otherwise:

- All 32 entities in the README's entity table map to an object id in `HaDiscovery` — 24 sensors,
  three numbers, the select, the switch, `battery_hold_target` and the two binary sensors. Nothing
  documented is gone; nothing published is undocumented.
- Every configuration default quoted in the README matches `appsettings.json`. The one apparent
  mismatch, `MaxChargingCurrentAmps` (class default 20, README 16), is not one: the README documents
  the shipped settings file, which sets 16 for the reference three-phase ID.4, and 20 is only the
  fallback when the key is absent.
- Every config key, mode, enum member and charger status named in the docs exists in `src/`.

The one real finding was self-inflicted, from the timezone work earlier today: `appsettings.json` had
gained a `"//TimeZone"` pseudo-comment key. It was the only such construct in the file — nothing else
in it carries a comment — and being a real JSON key it bound a junk config entry at
`Controller://TimeZone`. Removed. The explanation belongs where this project already puts it: the XML
doc on `ControllerOptions` and the README's Configuration section, both of which already had it.

Files changed: `deploy/README.md`, `deploy/.env.example`, `deploy/deploy.sh`, `Dockerfile`,
`src/Solax.Worker/appsettings.json`.

---

## 2026-08-10 — A running build can finally say which build it is

Until now nothing inside the running system knew its own version. The image tag knew; the process
did not. So a log file, or a `docker logs` dump pasted into an issue, could not be tied to a build —
and with three platforms behind one manifest list since #35, "which one is actually on the Pi"
had become a real question with no answer short of `docker inspect` on the device.

**The git tag is the source of truth.** `Directory.Build.props` holds `0.0.0-dev` as a deliberate
placeholder; CI passes `-p:Version=<tag minus the leading v>` on a `v*` build, so the number in the
binary and the number on the image come from the same place and cannot drift. The alternative — a
real version committed in the repo and bumped by hand — adds a commit per release whose only purpose
is to agree with a tag that already exists.

**The commit rides along.** `-p:SourceRevisionId=<sha>` makes the SDK emit
`AssemblyInformationalVersion` as `1.0.0+<sha>`, which `BuildInfo` splits apart again. The version
alone would not have been enough: every non-release build shares `0.0.0-dev`, and a release version
may be built more than once.

**The trap worth recording.** The image tag and the assembly version cannot be the same string. The
workflow's existing `version` output is what the *image* is called and is `main` on a branch push or
`release-1.3` on a release branch — neither is a legal assembly version. So the `version` job gained
a second output computed separately: the tag without its `v` on a release, the placeholder otherwise.
Passing the image tag straight through would have failed the build on every push to `main`.

Both Dockerfiles take `VERSION` and `SOURCE_REVISION` build args defaulting to the same placeholder,
so a plain `docker build` is honestly labelled a local build rather than silently inheriting whatever
CI last used.

**Verified end to end, not just unit-tested.** Built `linux/amd64` with
`--build-arg VERSION=1.2.3 --build-arg SOURCE_REVISION=$(git rev-parse HEAD)`, ran it, and the first
log line was `SolaX Local Controller 1.2.3 (31bf347) starting.` — the short sha matching the actual
commit. That is the part that could have silently produced an empty version, since it depends on the
SDK's attribute generation rather than on any code in this repo. 323 tests pass, 4 new.

Also surfaced in Home Assistant as the device's `sw_version`, so the running build is visible on the
device page without an ssh session. Device metadata, not an entity — it adds no row to the README's
entity table and nothing to any dashboard.

Files added: `Directory.Build.props`, `src/Solax.Worker/BuildInfo.cs`,
`tests/Solax.Worker.Tests/BuildInfoTests.cs`.
Files changed: `Dockerfile`, `Dockerfile.windows`, `.github/workflows/publish-image.yml`,
`src/Solax.Worker/Program.cs`, `HomeAssistant/HaDiscovery.cs`, `README.md`, `deploy/README.md`,
`docs/DECISIONS.md`.

---

## 2026-08-10 — The Windows publish job waits for a Docker daemon instead of assuming one

Follow-up to #35, whose first run on `main` failed in the `windows` job after 12 seconds:

```
failed to connect to the docker API at npipe:////./pipe/docker_engine;
check if the path is correct and if the daemon is running:
open //./pipe/docker_engine: The system cannot find the file specified.
```

Nothing to do with `Dockerfile.windows`, which was never reached. GitHub's Windows runners
intermittently start with the Docker service still stopped — an open upstream regression,
[actions/runner-images#13729](https://github.com/actions/runner-images/issues/13729), affecting
windows-2022 and 2025 on both standard and larger runners since February. The `Log in to GHCR` step
passed right before it because `docker login` talks to the registry, not the daemon, which is why the
failure looked like a build problem rather than an environment one.

The job now starts the service if it is stopped and polls the API until it answers, up to three
minutes, before doing anything else. It also asserts the daemon is in **Windows** container mode:
otherwise a Linux-mode daemon fails much later, deep in the build, complaining about the base image's
OS rather than about the runner.

**One line in that step is load-bearing and looks like noise:** `$PSNativeCommandUseErrorActionPreference = $false`.
pwsh 7.4+ turns a non-zero native exit code into a *terminating* error when
`$ErrorActionPreference` is `Stop`, so the probing `docker version` would have thrown on its first
failed attempt — the exact attempt the retry loop exists to survive. The retry would have been
decorative without it.

Also added `timeout-minutes: 60` to the job. Windows base images are large and this runner caches
them inconsistently, and the default job timeout is six hours.

**The failure did expose a real design consequence**, so it is now documented rather than discovered
twice: because the manifest job needs every platform, one flaky Windows runner holds back `:latest`
for the Pi as well. That is the intended trade-off — a `:latest` that quietly lost a platform is
worse — but the single-platform tags are pushed regardless, so the escape hatch
(`IMAGE_TAG=sha-abc1234-linux-arm64`) is in `deploy/README.md` now. The failed run confirmed it: both
Linux jobs went green and pushed `main-linux-arm64` and `sha-afef9d3-linux-arm64` before the Windows
job failed.

Files changed: `.github/workflows/publish-image.yml`, `deploy/README.md`.

---

## 2026-08-10 — Three platforms under one image name, and a timezone that is now configuration

Issue #35. The image was `linux/arm64` only. It is now `linux/arm64`, `linux/amd64` and Windows Nano
Server ltsc2022, all published under the one name `ghcr.io/mpospisil/solax-controller` as a
multi-platform manifest list — so `:1.0.0` pulls the arm64 image on the Pi and the amd64 image on an
x64 host, and `deploy.sh` needed no change at all.

**Naming was the part with an actual choice in it.** Three separate packages
(`…-linux-arm64`, `…-nanoserver`) would have meant three things to keep in version step and would
have discarded the platform selection Docker already does. One manifest list keeps a single name;
single-platform tags (`:1.0.0-linux-arm64`) still exist underneath it for pinning and for answering
"which one did it actually pull". `docker buildx imagetools inspect` lists what is behind a tag.

**The publish workflow went from one job to four:** `version` (one version string everything else
derives from), `linux` as a two-arch matrix, `windows`, and `manifest` folding the three
single-platform tags into every public name. The split is not cosmetic — the manifest job is what
keeps `:latest` pointing at the previous release until *all three* platforms of the new one exist, so
a half-finished matrix can never publish a partially-platformed release. Per-arch `scope=` on the
GHA cache was needed too; without it the two matrix legs evict each other.

**Windows needs its own Dockerfile, not a `--platform`.** A Dockerfile builds for one OS and a
Windows runtime stage can only be assembled on a Windows daemon, hence `Dockerfile.windows` on a
`windows-2022` runner with plain `docker build` — buildx has no usable Windows driver. ltsc2022 over
ltsc2025 because an ltsc2022 container runs on Server 2022 *and* 2025 hosts while ltsc2025 requires a
2025 host; Nano Server over Server Core for ~300 MB against ~2 GB. Note the `escape=` directive at
the top of that file: the default escape character is a backslash, which is also the path separator.

**The quirk this turned up is not a Modbus one for once.** `.NET` on Windows ignores the `TZ`
environment variable entirely — it reads the OS setting. The `TZ` line in `deploy/docker-compose.yml`
would therefore have done nothing, the container would have run in UTC, and since the forecast day
boundary, the daily loan-budget reset and the zone id recorded on every charging session all came
from `TimeZoneInfo.Local`, evening sessions would have been filed under the following day. Silently:
nothing logs a timezone. That is a data-correctness bug that only surfaces days later as charging
decisions nobody can explain.

So the zone became configuration. `Controller:TimeZone` resolves through `ZonedTimeProvider`, which
overrides `LocalTimeZone` on the `TimeProvider` the services already take and delegates the clock
untouched to `TimeProvider.System`. Routing it through the existing abstraction rather than adding a
second notion of "local" means all six call sites were fixed by one DI registration — except
`SolaxPollingService`, which was reaching for `TimeZoneInfo.Local` statically and now takes a
`TimeProvider` like everything else.

An unresolvable id throws at startup rather than falling back to UTC, because a quiet fallback is
precisely the failure this setting exists to prevent. Empty remains the default, so Linux behaviour
is untouched.

**Nano Server has no ICU**, which has one consequence worth knowing before deploying there: .NET
falls back to Windows NLS, so culture-aware formatting still works, but the IANA→Windows timezone
mapping does not — it is an ICU feature. `Controller__TimeZone` must be a Windows id there
(`Central Europe Standard Time`), not `Europe/Prague`. The same appsettings value is therefore not
portable between the Linux and Windows images. The worker logs a warning at startup if it finds
itself on Windows with the zone unset.

**Verified:** 319 tests pass. The `linux/amd64` image builds and starts; a bad zone id fails fast at
`ZonedTimeProvider.Resolve`. **Not verified:** the Windows build — there is no Windows daemon on the
development machine, so `Dockerfile.windows` and the `windows` job get their first real exercise on
the next push to `main`.

Also folded in from the branching discussion that prompted this work: `ci.yml` and the publish
workflow now trigger on `release/**` as well as `main`, so a release branch is gated and publishable.

Files added: `Dockerfile.windows`, `src/Solax.Worker/Configuration/ControllerOptions.cs`,
`ZonedTimeProvider.cs`, `tests/Solax.Worker.Tests/ZonedTimeProviderTests.cs`.
Files changed: `.github/workflows/publish-image.yml`, `ci.yml`, `src/Solax.Worker/Program.cs`,
`SolaxPollingService.cs`, `appsettings.json`, `README.md`, `deploy/README.md`, `docs/DECISIONS.md`.

---

## 2026-08-09 — Charging sessions are recorded to a local store

Issue #32. Every controlled charging session is now written to a SQLite file: when it started and
finished, which strategy drove it, how the delivered energy split between solar, grid and the home
battery, and a sampled trace of the whole thing.

**Where it plugs in.** `ChargeControlStatusHolder` already raised `Updated` with a full status snapshot
on every poll, and that record already carried nearly everything needed. So recording is a *subscriber*
— `SessionRecordingWorker`, alongside `HomeAssistantMqttWorker` — and `SolaxPollingService` is
untouched apart from one added field. The handler does nothing but drop the snapshot into a bounded
channel; attribution, tracking and SQLite all happen on the worker's own loop, so a slow SD card costs
latency there and nowhere else.

**The new logic is two pure classes in `Solax.Core`.** `ChargingSourceAttribution` splits the charger's
draw across solar, grid and battery — surplus PV first, then the pack, then the grid as the residual,
with the invariant that the three always add back up to the measured draw. `ChargingSessionTracker` is
the state machine: it opens a session when a controlling mode is driving a connected car, integrates
every poll into five `EnergyIntegrator` totals, stores a sample every 30 s *and* on every change, and
closes on unplug / mode-off / the car finishing / shutdown. A mode switched mid-session records an
event rather than splitting the session.

**Four charging figures per sample, not one.** Measured power, the actual current derived from it, the
charger's setpoint read back, and the current we commanded. The gaps are the point: target ≠ active
means our write didn't land, active ≠ actual means the car itself is the limiter. Nothing in the system
could tell those apart after the fact before this.

**`ChargeControlStatus.SessionCompleted` was added** because the information is otherwise destroyed:
the poll loop sets the mode back to `Off` when the fast mode ends itself, after which "the car filled
up" and "somebody selected Off" look identical downstream.

**Two things the tests caught.** `ChargingSessionDocument` originally had a convenience second
constructor, which makes it undeserializable by `System.Text.Json` — it would have failed in whatever
consumes the published object rather than here; it is a static `Create` now. And the day plan's
`NextFeasibleWindow` is a value tuple, whose members are fields, so it vanishes silently under a
default serializer configuration — `IncludeFields` is on, with a test that asserts the window survives
the round trip.

**Not done here:** the `data/` bind mount and its uid chown belong to `deploy/docker-compose.yml`, which
lives on the unmerged #26 branch. Recording works locally without it; in the container it needs that
mount or the history dies with the container.

Files added: `src/Solax.Core/Models/ChargingSession*.cs`, `ChargingSourceSplit.cs`,
`src/Solax.Core/Enums/ChargingSession*.cs`, `src/Solax.Core/Interfaces/IChargingSessionStore.cs`,
`src/Solax.Core/Strategies/ChargingSourceAttribution.cs`, `ChargingSessionTracker.cs`,
`src/Solax.Infrastructure/Sessions/*`, `src/Solax.Worker/Sessions/SessionRecordingWorker.cs`,
`src/Solax.Worker/Configuration/SessionStoreOptions.cs`, and tests for all of it.
Files changed: `ChargeControlStatus.cs`, `SolaxPollingService.cs`, `Program.cs`, `appsettings.json`,
`Solax.Infrastructure.csproj`, `README.md`, `docs/DECISIONS.md`.

---

## 2026-08-09 — Short entity names again; the explanations moved to the README

The previous entry named every entity `Label — what it means`, because HA's hover text is the friendly
name and there is no tooltip field. Seen in HA, that was the wrong trade: every card row was a
truncated sentence and the entity list stopped being scannable — a name that is really a paragraph
confuses more than it explains.

So the entity names are back to the short originals (`Grid power`, `Charging now`, `Required SOC
floor`, …), the `description` attribute and the `Describe` helper are gone from `HaDiscovery`, and the
two tests that enforced them are gone with them — `HaDiscovery.cs` and `HaDiscoveryTests.cs` are byte
for byte what they were before the change.

The explanations themselves were not lost. They are now a table in the README under *Home Assistant
(MQTT) → **What each entity means*** — one row per entity with its unit and meaning, split into the
always-present entities and the ones only the `Forecasted` mode populates. It keeps the detail that
was in the attributes: the enum states of `Control state`, `Charger status` and `Day outlook`, both
sign conventions, that the surplus is a smoothed 3-minute average, that `Active charging current` is a
read-back to compare against the target, that a working discharge hold still leaves a ~60 W trickle,
and that `Charging now` reports our own command rather than the car's draw.

Files changed: `src/Solax.Worker/HomeAssistant/HaDiscovery.cs`,
`tests/Solax.Worker.Tests/HaDiscoveryTests.cs`, `README.md`, `docs/DECISIONS.md`.

---

## 2026-08-08 — Home Assistant entities explain themselves

The entities published to Home Assistant showed their name and nothing else: hovering one gives a
tooltip identical to its title, which tells a reader nothing about what the number means or which way
its sign runs.

**Home Assistant has no description or tooltip field.** The frontend sets the hover text to the
friendly name so a truncated name can still be read in full; there is no per-entity description in
MQTT discovery, and adding one is
[an open feature request](https://community.home-assistant.io/t/add-description-for-each-entity-which-shows-in-the-gui-when-you-hover-over-the-name/622053).
The tooltip therefore cannot be changed except by changing the name — so that is what changed:

1. **Every entity is now named `Label — what it means`.** `Grid power` became `Grid power — positive
   while importing from the grid, negative while exporting`. HA truncates the label in a card and
   reveals the whole sentence on hover, which is the only tooltip mechanism it has. The label comes
   first so a truncated row is still scannable.
2. **The detail that will not fit on one line is an attribute.** `json_attributes_topic` points at the
   existing state topic and `json_attributes_template` is a **constant** — no placeholders, so every
   state message renders the same JSON. It is built with `JsonSerializer` rather than by hand so
   anything in the prose that would break the JSON is escaped; the only authoring rule is to avoid
   Jinja delimiters. It shows under Attributes in the more-info dialog and covers the enum states, the
   sign conventions, and the traps: the surplus is a smoothed 3-minute average, the active current is a
   read-back, a working discharge hold still leaves a ~60 W trickle.

A test enforces both halves — the name must be in `Label — meaning` form with a label short enough to
survive truncation, and the description must be present, valid JSON and not merely the name repeated —
so a new entity cannot be added without an explanation.

The binary sensor named `Charging now` was also a misnomer: it reports `HoldingControl`, whether *we*
are commanding a current rather than whether the car is drawing one. It now says so.

Renaming is safe for existing installs: `entity_id` is assigned when an entity is first discovered and
does not follow later name changes, so dashboards and automations keep working. A fresh install
derives its entity ids from the new names.

Files changed: `src/Solax.Worker/HomeAssistant/HaDiscovery.cs`,
`tests/Solax.Worker.Tests/HaDiscoveryTests.cs`, `README.md`, `docs/DECISIONS.md`.

---

## 2026-08-08 — Fast charge without the battery: the `FastNoBattery` mode (issue #28)

A fourth charge mode, and the first one that turns *itself* off. While it is selected the battery
discharge hold is armed automatically, the charger is pinned at `MaxChargingCurrentAmps` regardless of
sun, SOC or forecast, and when the car reaches its own charge limit the setpoint drops to the pause
current and the mode returns to `Off` — releasing the hold it armed.

### The one contract change

A controller could previously only say Charge / Pause / None. It now has a third thing to express, so
`ChargingControlDecision` gained `SessionComplete` and `ChargingControlInput` gained the two facts a
strategy needs to decide it: `EvDrewPower` and `EvIdleFor`. Both are defaulted, so the existing
controllers and their tests were untouched. The reasoning behind the completion rule — power
authoritative, `SuspendedEv`/`Finishing` corroborating, `ChargePaused` deliberately excluded because
it is what our own pause write produces — is in [DECISIONS.md](DECISIONS.md).

Cross-cycle state stays in `ChargingControlCoordinator`, next to the session-energy and loan tracking
it already owned: since when the car has been drawing nothing, and whether it ever drew at all. Both
reset on plug-in and on `ReleaseControl`, so a newly selected mode can't inherit the previous one's
verdict that the car has already charged and end itself on its first idle poll.

`FastChargingController` itself is the smallest strategy in the codebase — no smoothing, no
hysteresis, no SOC gate, because none of those inputs can change a constant setpoint.

### Ending the mode, in the right order

In `SolaxPollingService` the completion is handled *between* the charge cycle and the hold
reconciliation:

```
RunCycleAsync  -> Pause written, SessionComplete: true
_mode.Set(Off) -> mode := Off for the rest of this iteration
ApplyBatteryHoldAsync(mode: Off) -> release written on the same poll
```

Putting the mode change after the hold reconciliation would have left the inverter held for one extra
poll. Home Assistant needs nothing new: `PublishStatusAsync` already republishes the select's retained
state from `_mode.Mode` every status tick, so a controller-initiated change reaches the UI on its own.

`AutoHold` was generalised from "the forecast mode at its SOC floor" to "whatever the selected mode
wants", with `FastNoBattery` wanting it unconditionally, and now logs its automatic release as well as
its arming. The owner's manual switch is still OR-ed on top and is never released by a mode.

With `BatteryHold:Enabled` false the mode still charges and warns once on selection rather than
refusing to run — a select option that silently does nothing would be the worse failure.

### Hardware quirks and open verification

- **Nothing new is written.** The mode uses the same two write paths that already existed: the
  charger's current setpoint and the inverter's power-control command.
- **`MaxChargingCurrentAmps` becomes a supply limit.** The solar modes only reach the ceiling when the
  sun is that generous; this one sits at it for hours. On the reference install that is 16 A × 230 V ×
  3 ≈ 11 kW drawn continuously from PV and grid. Documented in both the README and the options class.
- **End-of-charge status is unverified.** No completed session has been logged through this controller
  yet, which is why the rule leans on power rather than on the charger's status enum. First live
  session should be logged end to end and the DECISIONS entry amended if the transitions differ.

### Tests

`FastChargingControllerTests` (13) covers the use-mode precondition, the clamped ceiling, indifference
to SOC and surplus, and every branch of the completion rule. `FastNoBatteryModeTests` drives the real
`SolaxPollingService` loop over a scripted telemetry sequence — a fake reader parks after the last
scripted reading, so the assertions need no timing assumptions — and checks the hold is armed with no
forecast at all, that a finished car pauses the charger, returns the mode to `Off` and releases the
hold in the same cycle, and that a hold the owner asked for survives all of it.

### Files changed

- `src/Solax.Core/Enums/ChargeControlMode.cs`, `EvChargerStatusExtensions.cs` (`IsChargeWindingDown`)
- `src/Solax.Core/Models/ChargingControlDecision.cs` (`SessionComplete`, `EvDrewPower`, `EvIdleFor`)
- `src/Solax.Core/Strategies/FastChargingController.cs` (new)
- `src/Solax.Worker/ChargingControlCoordinator.cs`, `ChargeControlStatusHolder.cs`,
  `SolaxPollingService.cs`, `Program.cs`
- `src/Solax.Worker/Configuration/ChargeControlOptions.cs`, `appsettings.json`
- `tests/Solax.Core.Tests/Strategies/FastChargingControllerTests.cs` (new),
  `tests/Solax.Core.Tests/Enums/EvChargerStatusExtensionsTests.cs`
- `tests/Solax.Worker.Tests/FastNoBatteryModeTests.cs` (new),
  `ChargingControlCoordinatorTests.cs`, `HaDiscoveryTests.cs`
- `README.md`, `docs/DECISIONS.md`

---

## 2026-08-08 — Five days of observation: #24 verified on hardware, forecast bias shape (issues #22, #24)

Read-only run 2026-08-02 09:26 → 2026-08-06 18:40, ~46,000 polls, charge mode `Off` throughout.
No code changes — this entry records what the run showed.

### The #24 reconnect fix is verified on hardware

The overnight run the #24 entry below asked for, four times over.

| Day | Polls | Failures | Transaction-ID | Rate | Worst gap |
|---|---|---|---|---|---|
| Aug 3 | 12,329 | 57 | 41 | 0.46 % | 52 s |
| Aug 4 | 11,995 | 92 | 76 | 0.76 % | 42 s |
| Aug 5 | 12,116 | 96 | 80 | 0.79 % | 43 s |
| Aug 6 (to 18:40) | 9,658 | 57 | 44 | 0.59 % | 43 s |

**Nothing ever got stuck.** Zero gaps over 60 s between successful polls across four full days, and
the failure-run distribution is almost entirely single polls:

```
Aug 3: {1 poll: 50, 2: 2, 3: 1}   Aug 5: {1 poll: 94, 2: 1}
Aug 4: {1 poll: 90, 2: 1}         Aug 6: {1 poll: 55, 2: 1}
```

Worst case in four days was three consecutive failed polls. Compare the pre-fix cliff: 43 successes,
564 errors, and zero successes for the last 50 minutes of the log. Traced end to end, one failure
costs exactly one poll:

```
16:14:24 [WRN] Failed to poll — Response was not of expected transaction ID. Expected 905, received 904.
16:14:31 [INF] SOC=99% BatteryPower=0W Solar=2093W ...
```

**But the underlying desync rate is climbing**: 0 (Jul 31) → 15 (Aug 1) → 41 → 76 → 80 → 44 per day.
The fix absorbs it at one poll each, so nothing is broken, but it is *masking* a trend rather than
addressing it. The cause is on the device or network side, not in this code. Worth its own issue if
it keeps rising.

### Forecast accuracy: totals are good, the intraday shape is not

Note the `Solar: Actual/Forecast` log line compares against **P50** (`EstimatedPowerWatts`), while the
planner uses **P10** — these numbers are median-vs-actual.

Daily energy on the three clear days: **+8.3 %, +6.5 %, +4.2 %** actual over forecast. Aug 6 (cloudy,
partial day) came in **−17.7 %**. Magnitudes are fine and err on the safe side.

The shape does not hold up. Hourly actual/forecast on clear days:

```
hour    07     08     09     10     11     12     13     14     15     16
Aug 3  0.67   0.88   0.99   1.07   1.06   1.06   1.19   1.10   1.29   1.14
Aug 4  0.68   0.86   1.01   1.09   1.11   1.12   1.12   1.12   1.11   1.10
Aug 5  0.68   0.84   0.97   1.06   1.10   1.14   1.12   1.12   1.09   1.18
```

**The 07:00 hour over-predicts by ~32 %, three days running, to within one percentage point** —
forecast ~1500 W against ~1000 W delivered. Midday is the opposite, a steady 6–14 % under-prediction.
A deficit that recurs at the same hour with the same magnitude on consecutive clear days is not
weather; it looks like morning shading the Solcast site model doesn't know about, or a wrong
azimuth/tilt in the site configuration.

**The single scalar bias cannot represent this.** The tracker whipsaws within each day — 1.00 at
start, down to 0.71–0.79 through the morning, back to 1.04–1.08 by evening — because no one number
is simultaneously +12 % and −32 %. Whichever value it holds is wrong for half the day, and applying
the evening value at 07:00 would make the over-prediction worse.

This lands where `SolarDayPlanner` is most sensitive: 1000–1500 W at 07:00 sits on the
shoulder/plateau boundary (surplus below vs above the charger's minimum power), so an optimistic
morning forecast is exactly what would open a charge window that cannot be sustained.

**Not yet a code change.** Check the Solcast site configuration first — azimuth, tilt, declared
capacity, horizon/shading if the plan offers it. If the site model is wrong, correcting it at source
beats compensating for it here. Only if the configuration is already right does per-period bias
(instead of one daily scalar) become the fix, and three clear days is thin evidence for that.

### Still not exercised

Across every log to date the charge mode never left `Off`, the battery hold was never armed, and
`ForecastedChargingController` has never run a cycle. The planner and accuracy tracker have been
validated read-only; **no feature that writes to hardware has been observed in a live run.** Every
outstanding verification item on #20 and #22 remains open for that reason.

---

## 2026-08-02 — Validation fixes after three days of observation (issue #22)

Branch: `feature/22-forecasted-charging`

Three days running in `Off` mode, ~34,000 polls. The service was stable — the #24 reconnect fix held
(22 transaction-ID errors, every one followed by successful polls) — and the forecast tracker proved
itself: Solcast p50 tracked reality within 5% all day, cumulative 50.0 kWh forecast against 51.1 kWh
actual. **p10 runs 13–16% low on this roof**, which is the insurance premium `ForecastConfidence: P10`
charges.

Four defects fell out of the logs.

### A. The house baseline ran away (the serious one)

`HouseBaselineEstimator` was one EWMA with a ~1.4 h time constant. That is slow per minute and fast
per day: it followed the diurnal curve, so by mid-afternoon it reported the afternoon peak and the
planner projected that flat across every remaining hour. Measured: **264 W at 05:00 → 2124 W at 11:00
→ 5406 W at 15:00**. The consequence, on a day whose forecast was accurate to 5%:

- 05:00 — "Window 08:00–16:30, 33.6 kWh available for the car"
- 12:00 — "No EV charging today", Plateau 0.0 kWh, window none, SOC floor 100%

Replaced by `HouseLoadProfile`: 24 hour-of-day buckets, each a slow EWMA (~3 days per bucket), seeded
from `BaselineHouseLoadWatts` until an hour has been observed. The planner now asks
`IHouseLoadProfile.ExpectedWattsAt(instant)` per forecast slice instead of taking one figure for the
whole day — which is the question it was always really asking.

### B. `ForecastToday` in the day summary was nonsense

`ForecastToday=6.6kWh ActualToday=52.7kWh (702%)`. Solcast returns only *future* periods, so by the
19:00 deadline "today's forecast" holds nothing but the evening. Both figures now come from the
accuracy tracker, which integrates them live period by period — the same numbers that were already
correct in the `Forecast check` lines.

### C. The day-plan line logged every poll

13,475 Information lines a day, 5–8 MB of log. The change-detection signature carried the budget to
0.1 kWh, which drifts continuously. Signature coarsened to whole kWh and whole percent, plus a
five-minute floor between lines — bypassed when the outlook changes or the plan becomes
usable/unusable, so the first real plan after startup is never held back. Measured after the fix: one
line per 90 s smoke run instead of eighteen.

### D. The outlook chattered

`Shortfall → Tight → Shortfall → Tight` inside three minutes, because the Tight/Shortfall boundary is
half the EV target and the day sat exactly on it. `Classify` now takes the previous outlook and
applies `OutlookHysteresisFraction` (0.05), so leaving a state needs the margin and entering it does
not.

### Also

- The forecast refresh now wakes 30 minutes *before* first light, so the day no longer starts on a
  stale forecast and the live-solar fallback (observed as an `Unknown` plan from 02:45 to 04:45).
- A once-daily `House profile:` line dumps the learned hourly shape, since that is the first thing to
  read when a day's decisions look wrong.

### Still open

**What draws ~5 kW at midday on the reference site?** The battery is full by 10:00 every day, and from
then on `OtherLoads` is ~91% of PV (r=0.69) against a 300 W night base and 47 kWh/day total. That
shape — load that appears exactly when surplus does — suggests a PV-surplus diverter. If so it is
self-fulfilling: the plan reads it as fixed house load, stands down, and leaves it the surplus it was
measuring. **This affects `Solar` mode identically** (its surplus was ~440 W at midday), so it is not
specific to the forecast strategy. Needs an answer from the site owner before the next measurement
round can be interpreted.

---

---

## 2026-08-02 — Raspberry Pi deployment: three containers, arm64 image from CI (issue #26)

Branch: `feature/26-raspberry-pi-docker-deploy`

The controller only ran from a developer machine, which contradicts the project's premise: a service
built to survive internet outages stopped when a laptop closed. This puts the whole system — the
controller, Home Assistant, and an MQTT broker — on a Raspberry Pi 3 B as three Docker containers.

### What changed

| File | |
|---|---|
| `Dockerfile` | Two stages; cross-compiles rather than emulating, runtime stage has no `RUN` |
| `.dockerignore` | Keeps `dev/`, `tests/`, and anything `*.env` out of the build context |
| `.github/workflows/publish-image.yml` | Builds `linux/arm64`, pushes to GHCR as `latest` / `sha-` / semver |
| `deploy/docker-compose.yml` | The three services, all state on bind mounts, per-service `mem_limit` |
| `deploy/deploy.sh` | tar-over-ssh + `docker compose pull && up -d`; refuses to guess if the Pi isn't prepared |
| `deploy/mosquitto/config/mosquitto.conf` | Authenticated broker, no host port, logs to stdout |
| `deploy/homeassistant/config/*.yaml` | Seed config with recorder tuning for a 1 GB board and an SD card |
| `deploy/.env.example` | Image tag, device addresses, MQTT + Solcast secrets, memory limits |
| `deploy/README.md` | Pi preparation, first run, operations, backup/restore, troubleshooting |
| `README.md`, `docs/DECISIONS.md` | Deployment section; the decision record for all of the above |
| `src/Solax.Worker/Program.cs` | Enable Serilog's `SelfLog` — the only `src/` change, see below |

The rationale for each choice is in [DECISIONS.md](DECISIONS.md).

### Rebased onto the session store (#32)

Two things main changed underneath this branch, neither of which git could flag:

- **`ChargeControl:Enabled` no longer exists.** The service boots in mode `Off` and takes control only
  when Home Assistant selects a mode, so the compose file was passing a setting that silently did
  nothing — and the deployment's safety claim rested on it. Replaced with `ChargeControl__DryRun`.
- **The SQLite session store needs a bind mount**, exactly as the #32 record predicted. `data/` is now
  mounted from `/opt/solax/data`, created in the image's build stage beside `logs/`, and covered by
  the same ownership handling — extended to a loop over both directories. SQLite makes it stricter
  than logs: `-wal` and `-shm` live beside the database, so the directory itself must be writable by
  uid 1654, and the documented backup stops the controller rather than copying a live database.

### Verified, not assumed

- **The arm64 cross-build works and is fast.** `docker buildx build --platform linux/arm64` completes
  in ~75 s from cold, with the `dotnet publish` step taking 3.5 s — proof it ran natively rather than
  under emulation. No QEMU is installed in the workflow.
- **The image runs as uid 1654 and writes where it should.** An amd64 build of the same Dockerfile
  started, logged, and created `/app/logs/solax-20260802.log` owned by `app` — which is what makes the
  bind mount over that path work once the host directory is chowned to 1654.
- **Bridge networking reaches the Modbus devices.** The smoke-test container, with no special network
  configuration, polled the live inverter and charger (`SOC=56% BatteryPower=-748W Solar=101W
  EvCharger=Available`). This was the main open question about the container topology, and it means
  host networking is not needed for either the controller or HA.
- **No log file is written inside any container.** With the logs bind mount in place, `docker diff`
  on the running controller is **completely empty** and `solax-<date>.log` appears on the host owned
  by 1654. HA logs to its own bind-mounted `/config`, and the broker only logs to stdout.
- **The way that breaks is silent, and is now guarded twice.** Given a root-owned logs directory —
  what Docker creates if the mount source is missing — the container runs happily, polls, reports
  healthy, keeps `docker diff` empty, and writes **no log file anywhere**; Serilog's file sink fails
  and the process never mentions it. Hence `Serilog.Debugging.SelfLog.Enable(Console.Error)` in
  `Program.cs` (the failure now appears in `docker logs` as `RollingFileSink: the target file could
  not be opened or created`) and directory preparation in `deploy.sh`. Both verified against the
  reproduction.
- **The compose file's required-variable guards fire.** Without `MQTT_USERNAME`/`MQTT_PASSWORD`,
  `docker compose config` exits 1 with `required variable MQTT_USERNAME is missing a value: set
  MQTT_USERNAME in .env`, rather than starting a broker nothing can authenticate against.

### Things worth knowing

- **The runtime stage deliberately contains no `RUN`.** It looks like an odd constraint until you
  notice that one `RUN mkdir` in an arm64 stage is enough to drag QEMU into the build. The logs
  directory is created in the build stage instead and `COPY --chown` does the ownership.
- **`mem_limit` is silently ignored on Raspberry Pi OS** until `cgroup_enable=memory cgroup_memory=1`
  is added to `/boot/firmware/cmdline.txt`. Easy to miss, and the failure mode is the whole board
  thrashing instead of one container being killed.
- **`deploy/` mirrors `/opt/solax` path-for-path.** An earlier version rewrote paths with `tar
  --transform` on the way over; making the two layouts identical deleted that cleverness.
- **The deploy script does no first-time setup.** Creating and chowning directories needs `sudo`, and
  a routine deploy should never be the thing that does it — so it checks, and prints the exact
  commands if something is missing.
- **HA's `.storage` is the one irreplaceable directory** (account, entity registry, MQTT integration).
  `deploy/README.md` documents the backup and the restore.

### Not done here

Hardware verification: the 72-hour soak, the reboot and power-cut tests, and the measured memory
headroom from issue #26's acceptance criteria all need the Pi itself.

## 2026-07-29 — Modbus reconnect on failure (issue #24)

Branch: `fix/24-modbus-reconnect`

A three-hour run produced 43 successful polls and 564 transaction-ID errors. Bucketed by ten minutes
it is not intermittent at all — it is a cliff:

| bucket | ok | errors |
|---|---|---|
| 18:50 | 4 | 0 |
| 19:00 | 39 | 10 |
| 19:10 | **0** | 91 |
| 19:20–20:00 | **0** | ~97 each |

### Cause

A late response left in the socket buffer puts the stream permanently one or more replies behind.
NModbus's own retries cannot escape it, and nothing tears the connection down — `IsConnected` is true
because the socket is fine. See [DECISIONS.md](DECISIONS.md).

### What changed

`ModbusTcpClient` rewritten around a single `ExecuteAsync` path: serialise on a `SemaphoreSlim`,
connect on demand, wait out `DeviceConfig.MinRequestInterval`, run the operation, and — on any failure
— invalidate the connection through an exception filter so the original exception still surfaces
unchanged. `DeviceConfig` gained `MinRequestInterval` (250 ms default).

### Things worth knowing

- **NModbus retries a mismatched response by re-sending the request**, so corrupting a single reply is
  self-healing and proves nothing. The tests had to model the *persistent* offset to reproduce the
  field failure — that discovery is baked into `FakeModbusTcpServer.Persistent`.
- **The tests need a real socket.** `FakeModbusTcpServer` implements enough MBAP framing for function
  codes 3, 4, 6 and 16, plus deliberate transaction-id corruption.
- **Verified as regression tests**: disabling only the invalidation makes 3 of the 9 fail; restoring it
  makes all 9 pass.
- **Not verified on hardware yet** — the fix wants an overnight run showing successful polls all the
  way to the end of the log. *(Done — see the 2026-08-08 entry above: four full days, ~46,000 polls,
  no gap over 60 s, worst failure run three polls.)*

### Files

`src/Solax.Infrastructure/Modbus/ModbusTcpClient.cs`, `src/Solax.Core/Models/DeviceConfig.cs`,
`tests/Solax.Infrastructure.Tests/{ModbusTcpClientTests,FakeModbusTcpServer}.cs`, docs.

---

## 2026-07-27 — Always boot in Off, with the battery free (issue #22 follow-up)

Branch: `feature/22-forecasted-charging`

The service used to seed its runtime state from configuration: `ChargeControl:Enabled` chose the boot
charge mode (`true` → Solar) and `BatteryHold:HoldAtStartup` could arm the discharge hold at startup.
Both are gone. The service now **always** starts with the charge mode `Off` and the hold `off`, and no
configuration key can change that.

### Why

A restart happens for reasons nobody chose — a crash, a power cut, a deploy — and in each case the
safe assumption is that the controller has no business acting until somebody asks it to. Seeding from
config inverted that: a machine rebooting at 3am would take control of the charger and, if
`HoldAtStartup` was set, immediately re-arm a hold that keeps the pack idle. The hold in particular is
a *command with a lifetime*, not a stored setting, so re-arming it on boot conflicts with the failsafe
that #20 was built around: stop renewing and the inverter returns to normal within `Duration`.

### Changed

- `Program.cs` seeds `ChargeControlModeSelector` with `Off` and `BatteryHoldSelector` with `false`,
  unconditionally.
- Removed `ChargeControl:Enabled` (it did nothing else) and `BatteryHold:HoldAtStartup`.
  `BatteryHold:Enabled` stays — it is a real master switch that decides whether the inverter's Modbus
  client is writable at all, and it supersedes the note in the #20 entry below about `HoldAtStartup`.
- Startup log lines now say what the state is *and* that the hardware is untouched until asked.
- README updated in four places; existing `.env` files carrying the removed keys are harmless (unbound
  configuration keys are ignored), but they no longer do anything.

---

## 2026-07-27 — Forecast-driven charge mode: `Forecasted` (issue #22)

Branch: `feature/22-forecasted-charging`

A third charge mode alongside `Off` and `Solar`, selectable at runtime from the Home Assistant select.
Where `Solar` waits for a 95 % battery and then follows the last three minutes of surplus, `Forecasted`
plans the whole remaining day from the Solcast forecast so the car can start hours earlier while the
home battery still reaches 100 % by a configured evening deadline.

### What was built

**`Solax.Core` (all pure, all unit-tested)**

- `SolarDayPlanner` — the heart of it. Slices the remaining forecast (prorating the period the plan is
  built inside), splits it into *shoulder* (surplus below the charger's minimum power) and *plateau*
  (at or above it), books the battery's need backwards from the deadline, and reports what is left as
  both an energy budget and a **deliverable** budget restricted to periods that clear the minimum
  power. Also produces the SOC floor, the next viable charge window, the shortfall and the outlook.
- `ForecastedChargingController` — decides the current from the plan. Hard stops (session ceiling,
  final guard, SOC floor, no window) bypass the dwell timers; soft reasons respect them and hold the
  session at 6 A rather than stopping inside `MinRunTime`. Grants the bounded battery loan. Delegates
  to `LiveSolarChargingController` whenever the plan is unusable.
- `ForecastAccuracyTracker` — accumulates today's actual against forecast energy per period, exposes
  the clamped bias, hands each closed period to the caller once for logging, and withdraws trust after
  a sustained breach.
- `EnergyIntegrator`, `HouseBaselineEstimator`, `SolarDayPlan`, `DayOutlook`, `ForecastConfidence`,
  `IForecastRuntimeSettings`, plus p10/p90 bands on `SolarForecastPeriod`.

**`Solax.Infrastructure`** — Solcast now parses `pv_estimate10`/`pv_estimate90` (only the median was
read before) and logs all three bands on refresh.

**`Solax.Worker`** — `DayPlanProvider` (baseline + accuracy + plan + all four log lines + day roll),
`ForecastRuntimeSettings` (the three HA-settable numbers), mode-keyed controller routing in
`ChargingControlCoordinator`, session/loan energy tracking, automatic arming of the #20 discharge hold
at the plan's SOC floor, daylight-only forecast refresh, and thirteen new HA sensors plus three number
entities.

### Things worth knowing

- **The SOC floor had to be redefined mid-implementation.** Deriving it from the energy *booked* for
  the battery made it equal the current SOC — a floor that forbids all discharge. It counts all
  remaining surplus instead; see [DECISIONS.md](DECISIONS.md).
- **`MaxLoanPowerWatts` defaults to 2500, not the 1500 the issue first proposed.** Bridging a typical
  2–3 kW surplus up to the ~4.2 kW three-phase floor needs ~2.2 kW; a 1.5 kW cap could never reach it,
  which would have made the loan silently useless.
- **The dwell timers change what a "pause" means.** Inside `MinRunTime` a soft pause holds the charger
  at 6 A instead of stopping. Five of the loan tests initially failed because of exactly this, which is
  the behaviour working as intended.
- **`ChargeControlStatus` and `ChargingControlInput` both grew.** The input gained defaulted
  parameters (plan, dwell, session energy, loaned energy) so the live-solar controller and its tests
  are untouched.
- **Nothing is persisted.** A restart loses today's totals and resets the bias to 1.0; it re-converges
  within a few forecast periods.

### Not done / open

- **Not verified against hardware.** No live day has run through this yet. `BatteryCapacityKWh` must
  be set to the real pack before the plan means anything, and the accuracy tracker should be left
  running read-only for a week (it works in every mode) to see whether p10 is systematically low for
  this roof.
- The undocumented interaction between an auto-armed hold and a manual one is resolved by OR-ing them
  (manual always wins), but has not been exercised live.
- Whether the pack curtails PV near 100 % SOC — the open question from #20 — still matters here: it
  would affect the trajectory's final approach.

### Files

`src/Solax.Core/{Enums,Models,Interfaces,Strategies}/*` (11 new, 4 changed),
`src/Solax.Infrastructure/Solcast/*` (2 changed),
`src/Solax.Worker/{Forecasting/*,Configuration/*,HomeAssistant/*,Program.cs,SolaxPollingService.cs,ChargingControlCoordinator.cs,SolarForecastRefreshWorker.cs,appsettings.json}`,
tests in all three test projects (48 new).

---

## 2026-07-27 — Battery hold verified on hardware; discharge deadband; grid-power sensor (issue #20)

Branch: `feature/20-battery-discharge-hold`

Follow-up to the entry below, which shipped the hold unverified. It has now been exercised against
the live inverter.

### What was found

The mechanism works. Arming the hold at dusk (PV ~360 W, SOC 87 %, no EV charging) moved the house
off the battery within a single poll: battery **−2846 W → −56 W**, grid **0 W → +1601 W**, solar
unchanged at ~370 W. Renewal at half the duration held it continuously with no observed lapse, and PV
was not curtailed. Full measurements are in [DECISIONS.md](DECISIONS.md).

### What changed

- **A 150 W deadband on the "hold armed but battery discharging" warning**
  (`SolaxPollingService.ResidualDischargeWatts`). A working hold still leaves a **50–65 W trickle**
  out of the battery — inverter standby draw, not load being served; it persisted while house load
  swung between 143 W and 2877 W. The warning originally fired on any negative value, so it fired
  every poll and drowned out the signal it existed to give.
- **Grid power exposed as a Home Assistant sensor** (`grid_w` in the state payload, `grid_power`
  discovery config), so the hold can be observed from HA rather than only from the log — watching
  import rise as battery discharge falls is the clearest evidence the hold is working.

### Consequences

- **Issue #20's "`BatteryPowerWatts` is never negative" acceptance criterion is not literally
  achievable** on this hardware, because of the standby trickle. The achievable guarantee is that the
  battery stops *serving house load*.
- Defaults are unchanged: `Enabled: false`, `DryRun: true`. Verification on one inverter and firmware
  says nothing about any other.

### Still unobserved

Behaviour under strong midday PV (does the battery still charge from surplus while held, and is PV
curtailed at full output), behaviour with the EV actually charging, and what the undocumented
`timeout` field does relative to `duration`.

134 unit tests pass.

---

## 2026-07-26 — Battery discharge hold (issue #20)

Branch: `feature/20-battery-discharge-hold`

A switch that stops the home battery discharging, so the EV charges from PV and grid while the
battery is still free to charge from PV surplus. Orthogonal to charge control: either can be on
without the other.

### What was built

**`Solax.Core`**

- `Enums/InverterControlRegister.cs` — the inverter's *holding* register space (a different address
  space from `InverterRegister`, which is input registers), carrying the Modbus Power Control block
  at `0x7C` and the "verify against your hardware" warning.
- `Enums/InverterPowerControlMode.cs`, `Enums/InverterPowerControlSetType.cs` — the device-level
  enums, with an explicit note on why there is no `No Discharge` value.
- `Strategies/BatteryDischargeHoldStrategy.cs` — pure, stateless computation of the `active_power`
  target: `-min(house load, PV)`.
- `Interfaces/IBatteryDischargeControl.cs`, `Interfaces/IBatteryHoldSelector.cs`,
  `Models/BatteryHoldState.cs`.
- `Models/EnergyState.HouseLoadPowerWatts` — total house load *including* the EV charger
  (`PV + Grid − Battery`), as distinct from the existing `OtherLoadsPowerWatts` residual, which
  excludes it. The hold needs the EV counted as load the grid may cover.

**`Solax.Infrastructure`**

- `RegisterMaps/PowerControlPayload.cs` — pure encoder for the 13-register block, 32-bit fields low
  word first.
- `BatteryDischargeControl.cs` — the write path on the keyed inverter client. Writes on arm, release,
  retarget beyond the threshold, and renewal; nothing in a steady state.

**`Solax.Worker`**

- `BatteryHoldSelector`, `Configuration/BatteryHoldOptions`, poll-loop reconciliation, and a Home
  Assistant `switch` plus **Battery power** and **Battery hold target** sensors.

### Architecture decisions

The central one — that the inverter has no `No Discharge` mode, so the hold is a computed
`Enabled Power Control` target rather than the fire-and-forget switch the issue described — is
recorded in [DECISIONS.md](DECISIONS.md) along with what it costs.

Two smaller ones worth noting here:

- **`BatteryHold:Enabled` is a real master switch, not a boot default.** `ChargeControl:Enabled`
  only seeds the mode, because Home Assistant can select `Solar` at runtime afterwards. That pattern
  can't hold here: issue #20 requires that `Enabled: false` performs no inverter writes *at all*, and
  a runtime-settable switch would break it. So while the flag is off the feature is entirely inert —
  no HA switch is published, the poll loop skips it, and the inverter's Modbus client is wrapped
  read-only. `HoldAtStartup` covers the boot value of the hold itself.
- **`WriteProofInDryRun` became `WriteProof`, taking an explicit `writable` flag.** It previously
  gated *both* device clients on `ChargeControl:DryRun`, which would have left the inverter writable
  whenever charge control was live — regardless of the battery-hold settings. Each device now derives
  writability from the feature that actually writes to it.

### Hardware quirks and edge cases

- **Holding register `0x7C` is overloaded**: written it is the power-control command, read it is the
  ARM firmware version. There is no read-back of the active command, so the HA switch reports what we
  last successfully wrote. A failed write therefore shows in HA as the switch returning to OFF rather
  than as an assumed success.
- **Nothing verified on hardware yet.** The register map comes from the upstream integration, not a
  SolaX document, and issue #20's Phase 0 observations are still outstanding. Hence
  `Enabled: false` + `DryRun: true` defaults. *(Superseded — see the 2026-07-27 entry above: the hold
  was subsequently verified on the reference inverter. The defaults stand regardless.)*
- **The compensating check for the missing read-back**: if the battery is discharging while we
  believe the hold is armed (and we are not in dry-run), the poll loop logs a warning. It is the only
  observable signal that the command isn't taking effect on this firmware. *(A 150 W deadband was
  added on 2026-07-27; a working hold still trickles 50–65 W.)*
- **Clock going backwards** (NTP step, telemetry timestamp jitter) is treated as "renewal due" rather
  than deferring renewal indefinitely.
- A failed write is not recorded as armed, so the next poll retries instead of reporting a hold that
  was never established.

### Verification performed

- 131 unit tests pass, covering the payload encoding field by field, arm/release/retarget/renew/
  no-change, duration clamping, dry-run, failed-write retry, the target strategy, the selector, and
  the HA discovery configs and state payload.
- Smoke-run against the live inverter with `Enabled: true` + `DryRun: true`, confirming the encoded
  block is logged (`registers [1,1,0,0,0,0,60,0,0,0,0,0,0] at 0x7C`) and that no write reaches the
  device — the `ReadOnlyModbusClient` tripwire warning never fires.
