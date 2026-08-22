# Decision records

Append-only. A new record goes here whenever we adopt a library or establish a core pattern.

---

## 2026-08-22 — The web UI splits along "reports" and "decides"

Issue #98. The UI had grown one page per feature as the features landed: telemetry and controls on
`/`, the forecast plan on `/forecast`, the targeted mode on `/targeted`. Every page was defensible on
the day it was written and the set was not: the numbers a control shapes were a nav item away from the
control, and two of the five nav items were modes — as if `Solar` and `FastNoBattery` simply had no
opinions worth a page.

**Decision — one axis, not five: a page that reports and a page that decides.** `/` answers "what is
happening", `/charging-plan` answers "what should happen". Every control lives on the second, every
figure that is not part of a plan on the first. A page that does both is what produced the split in
the first place.

### The dashboard groups by the question being asked

**Decision — Energy, Vehicle, Charging session, in that order.** Fourteen equal tiles made the reader
do the grouping, every time. The order is the order the questions arrive: the house is true whatever
is plugged in, the car is next, and the session is last because it is the only one that may not exist.

**Decision — the session section renders only while there is a session**, and its absence is a
sentence naming the mode and the charger's state plus a link to start one — not a grid of dashes,
which is eight tiles saying nothing eight times.

**Decision — "is there a session" is not "is the mode `Off`".** A car drawing power under no mode at
all — somebody put the charger into `Fast` at the wallbox — is precisely the case that must not be
hidden behind "nothing is charging".

### The modes get tabs, not pages

**Decision — a tab per mode, under one page.** Tabs are the shape of the underlying model: exactly one
mode drives the charger, and the modes are alternatives to each other. Four nav items would have said
they were four independent things you might do at once.

**Decision — the tab is in the URL** (`/charging-plan/forecasted`), so a bookmark, a refresh and the
back button all behave. State in a field would have made "the forecast plan" unlinkable, which it was
not before this change.

**Decision — `/charging-plan` with no tab opens on the running mode, and only on the way in.**
Resolving it on every render would let a mode that ends itself — `FastNoBattery` and `Targeted` both
do — pull the tab out from under whoever is reading it.

**Decision — `Off` has no tab.** It is not a strategy with terms of its own; it is the button that
ends whichever one is running, and it belongs with the state line it changes.

### What is common stays above the tabs

**Decision — the battery discharge hold sits in the header, not in a tab.** It is not a property of
any one strategy: `FastNoBattery` arms it for its whole run, `Targeted` arms it while the plan is
importing, and the owner may want it under `Solar` for reasons no mode knows about. Putting it in a
tab would have implied it belonged to that mode; repeating it in all four would have made four
switches out of one setting.

**Decision — the tabs do not hold `IChargeActions` themselves.** The page passes `Start` and `Stop`
down as delegates and reports every outcome — the "started from the Web UI at 13:42" note, and any
charger that refuses the use-mode write — beside the mode line those outcomes moved. Four tabs each
with their own error paragraph would have been four places for the same message, and the message is
about the mode, not about the tab.

**Decision — `Minimum battery SOC` appears on two tabs.** Both planners work to it and both show it as
a plan figure; reading a floor you cannot move from where you are reading it is the exact split this
page was built to end. It is one runtime seam (`IForecastRuntimeSettings`), so the two cannot
disagree — which is what makes the duplication safe rather than a second source of truth.

---

## 2026-08-22 — The weather goes on the session, twice, in columns

Issue #96, and the other half of [the day-forecast curve](#2026-08-22--a-session-carries-the-whole-day-it-happened-on). That record gave a session what the sun was *expected* to do. This one gives it what the sky actually did — without which a session that under-delivered against a confident forecast has no explanation attached to it at all, and cloud, a cold morning and snow on the panels are indistinguishable afterwards.

### The endpoint chose itself

The issue proposed OpenWeatherMap's One Call API. Against a real key it answers:

```
401 — Please note that using One Call 3.0 requires a separate subscription to the One Call by Call plan.
```

`data/2.5/weather` on the same key answers `200` with **every figure this feature records**: `main.temp`, `main.pressure`, `main.humidity`, `clouds.all`, `visibility`, `weather[0].main`, `weather[0].description`, `sys.sunrise`, `sys.sunset` and `dt`. One Call's extra horizons are forecast data, and a session is recorded against what the sky *did*.

**Decision — the free current-weather endpoint, and no abstraction over providers.** One provider, one client. A second one can be abstracted over when there is a second one.

### Two readings, and no series between them

**Decision — one fetch when the session opens, one when it closes.** A six-hour session can finish in entirely different weather from the one it started in, and a single reading cannot say so; the *pair* can. It is the same shape the header already has for SOC, and it costs two API calls per session — a handful a day, inside any free plan.

Not a per-sample series, deliberately. Weather moves in tens of minutes and is decoration on a record rather than an input to a decision; a reading every thirty seconds would be a hundredfold the storage and the quota to say the same thing. Not a background refresh worker either: that would spend calls on the hours when nothing is recording, which is most of them.

**Decision — the fetch lives in `SessionRecordingWorker`, not in `ChargingSessionTracker`.** The tracker is a pure strategy with no I/O, exactly as it has no forecast service and no vehicle feed. The worker attaches the reading on its way to the store.

**Decision — a weather call may never hurt a session.** `IWeatherService.GetCurrentAsync` returns null for every provider-side failure — bad key, rate limit, timeout, outage — and never throws. It bounds its own wait (5 s by default) because the closing call sits between a session ending and its totals being written, and no weather figure is worth delaying that. On the shutdown path a null `WeatherAtEnd` is the correct outcome rather than something to wait for.

### Columns, not a document

**Decision — eighteen columns rather than one JSON blob**, which is the opposite of what the day-forecast curve does one record above. The difference is how each is read. A forecast curve is written once and read whole; cloud cover and temperature are the *axes* the analysis groups by, and "solar share against cloud cover" or "delivered energy by temperature band" should be SQL rather than a parse per row.

**Decision — sunrise and sunset are stored once, not per reading.** They belong to the day, not to the moment, and would be identical in both fetches. A session crossing midnight carries the starting day's pair — the rule `StartedAt` and `DayForecast` already follow. They are worth storing at all because they are the denominator: they make an 8 kWh December session comparable with an 8 kWh June one, and they say whether a session ran out of sun or merely out of surplus.

### Off unless configured, and null when off

Coordinates are `double?`, not `double`. Defaulted to 0 they would be a real place in the Atlantic, and an unconfigured site would silently record the weather in the Gulf of Guinea rather than recording none. With no key or no coordinates the service makes no HTTP call at all, and every weather column stays `NULL` — which must stay distinguishable from a genuine reading of 0 °C under a clear sky. Schema v4 and `schemaVersion` 4, additive; older rows keep `NULL`, which is the truth for them.

---

## 2026-08-22 — A session carries the whole day it happened on

### The problem

A recorded session could be read against what the forecast expected *while it ran* — every sample
carries `ForecastPowerWatts`, and the header carries `ForecastRemainingAtStartWh`. Neither answers the
question that actually comes up: **was this a good session, or merely a good day?** 4 kWh from the roof
means one thing on a day forecast at 6 kWh and something else entirely on a day forecast at 30, and
nothing stored could tell the two apart after the fact.

The obvious fix — "snapshot today's forecast onto the session" — does not work, because of what a
forecast provider actually returns.

### Solcast only ever forecasts forward

`rooftop_sites/{id}/forecasts` returns the periods still to come and nothing behind them. So the
cached forecast is *self-erasing*: at 07:00 it holds the whole day, and by 16:00 it can no longer say
what the morning was predicted to bring. A session opening at noon and snapshotting "today" would
capture half a day, and the half it dropped is exactly the half already spent.

**Decision — the controller retains what the provider forgets.** `SolarForecastHistory` (Core, pure,
lock-guarded because the refresh worker writes it and the poll loop reads it) keeps every period any
refresh has ever carried, keyed by period end, for 7 days. Each refresh upserts: future periods are
replaced by the newer estimate, elapsed ones simply stay. What survives for a given period is
therefore the **last prediction made about it before it happened** — the closest to a nowcast the
provider ever gave, and the fairest thing to judge the roof against.

**Decision — this is a new method, not a change to `GetForecastForToday`.** `GetDayForecast(DateOnly)`
sits beside it on `ISolarForecastService`. Retained history in the existing method would have quietly
changed what "today's forecast" means for the day planner, the accuracy tracker and the day summary —
all of which mean *remaining* when they say today. A separate method makes the analysis surface
explicitly not a control surface: nothing in charge control reads it.

**Decision — one JSON column on the session header, not a periods table.** `sessions.day_forecast_json`
(schema v3, `schemaVersion` 3, additive; older rows stay `NULL`). The curve is written once, read
whole, and has to *travel with the session* when the document is published to object storage — none of
which a join gives us. About 10 kB per session, against a 365-day retention: irrelevant next to the
samples.

**Decision — written at open, rewritten at close.** The day fills in behind us as it passes, so the
version available when a session closes covers hours the version available when it opened could not.
A closed session is still immutable; this is one more field the closing write sets, like the totals.

### What it does not do

**A day the controller wasn't running for all of is short by that much.** The history is in memory, so
a restart at 14:00 loses that morning permanently — nothing re-fetches the past, and doing so would
mean a second Solcast endpoint and a second call against the daily quota. The curve is `null` rather
than empty when nothing is held, because an empty curve sums to zero and would read as a day the sun
never came up. The [energy history](../README.md#energy-history-the-energymonitor-section) store still
holds forecast-versus-actual per quarter hour for those days, which is the fallback.

---

## 2026-08-22 — Rate, not timing, decides the solar share

Supersedes the placement half of [2026-08-21](#2026-08-21--targeted-charging-imports-under-the-sun-not-after-it).
That record moved the grid block onto the sunniest hours. This one deletes the block.

### What the site showed

A 13 kWh target, a day peaking at 8.5 kW, and the session summary:

```
Session ended after 01:27: 13.02kWh delivered — 3.59 solar / 9.09 grid / 0.34 battery
```

Nine of thirteen kilowatt-hours bought, on a sunny day, by a mode whose entire purpose is not to do
that. Placement was not the problem. **Rate** was: inside a grid block the controller commanded a flat
16 A, so the car drew 10.7 kW while the roof gave 3–5 kW, finished in 87 minutes, and stopped — missing
the whole afternoon. No placement can fix that, because the charger can only use the sun that shines
*while it runs*.

### The pace

```
pace = (energy still needed − sun forecast to reach the car) ÷ time left to the deadline
charge at  live surplus + pace,  clamped to 6–16 A, recomputed every poll
```

Sum, not maximum: the pace is by construction what the sun will *not* cover, so the two are additive.
Taking the greater lets a sunny window swallow the grid's share and arrive short — a bug the tests
caught before the hardware did.

Blocks disappear entirely, which is a simplification rather than a cost: there is nothing left to
place, and the sunniest-first pass from the previous record goes with it.

### Two look-aheads, not one

The subtle part, and the one that took two attempts.

*Unaided* counts only half-hours whose surplus clears the charger's ~4.14 kW floor — sun that can run
the car by itself. It answers **"need we buy anything at all?"** and must be strict: counting sub-floor
sun here defers the start on strength that never arrives alone, and finishes short.

*Assisted* counts every watt of surplus. It answers **"once the charger is running, how much does the
roof cover?"** and must be generous: a 3.2 kW half-hour genuinely supplies 3.2 kW to a charger held at
its floor.

Using the strict figure for both was a real defect, caught by simulating the owner's own scenario —
activate at 02:00 for 15 kWh by 17:00. On a 4.2 kW day carrying **17.5 kWh** of forecast surplus
against a 15 kWh target, it scored the day at ~4 kWh of usable sun and planned to buy 11 kWh, starting
at 02:00 in the dark, on a day the roof could have covered outright. With both figures it waits for
daylight and buys roughly 3 kWh.

### What follows from it

- **Sub-floor sun is harvested, not skipped.** Once the sun cannot finish alone, any slice with surplus
  runs right through at the floor: the roof supplies what it has, and only the difference is bought.
  Harvesting a 3.2 kW half-hour costs 0.9 kW of grid; buying the same energy after dark costs 4.14 kW.
- **A sub-floor pace is never deferred.** It is energy the sun will not cover, so waiting for the pace
  to climb past the floor merely crams the remainder into the last minutes of the window, where a
  dropout has no room to recover. It self-regulates: delivering ahead drives the pace to zero and the
  car returns to living on surplus.
- **The hold arms on what is actually imported.** Below the SOC floor the surplus belongs to the pack,
  so it must not be netted off the commanded power — doing so reported zero import, left the hold
  released, and had the pack quietly funding the pace.

### The cost we accept

Pacing spends the slack: a charger dropout late in the window has less room than the old finish-early
behaviour. The safety margin is the only buffer, and it is worth revisiting if a real session ever
runs close.

---

## 2026-08-21 — Targeted charging imports under the sun, not after it

Supersedes the placement half of [2026-08-20](#2026-08-20--the-grid-block-goes-as-late-as-it-can-and-the-home-battery-still-comes-first).
The battery-priority half of that record stands unchanged.

### What was wrong with "as late as it can go"

The old record argued the block should end at the deadline, because every poll before it is another
chance for the sun to shrink it before a watt is bought. The optionality is real. What the argument
missed is what late placement *costs*, and it turns out to be the same optionality, spent the other
way round.

Two cases, both reported from the site:

**A poor forecast day with the departure this evening.** The plan concludes the sun cannot cover the
target, so it books the grid block against the last hours before the deadline and the charger sits
idle straight through the middle of the day. Meanwhile the roof is producing — just not enough per
half-hour to clear the charger's 6 A minimum, which is exactly the condition that made the plan fall
back on the grid in the first place. That surplus is exported for nothing, and the same energy is
bought back at dusk. Placement *at* the peak turns it into an offset instead: the charger runs at
maximum, the array supplies what it can at that instant, and only the difference is metered.

**Any day at all, given a P10 forecast.** Planning on P10 means the plan is deliberately wrong in one
direction — the day usually beats it. Late placement can only collect that upside by *cancelling*
the block, which needs the surplus to have been chargeable on its own. Placement under the sun
collects it as a smaller meter reading, whether or not any half-hour ever cleared the minimum.

### And when there is no sun in the window, start at once

The 21:00 → 07:00 case the old record led with is the one where late placement looked most obviously
right, and it is the one where it does the least. There is no sun between 21:00 and 07:00. Deferring
the import until 05:00 does not expose it to a single additional watt of surplus; it buys precisely
the same kilowatt-hours eight hours later. What deferral does buy, at that point, is risk: a charger
that drops its connection, a car that limits itself to 3 kW, a departure moved forward to 05:30 — all
of them now land with no slack at all. So a window with no forecast surplus in it, and a window with
no usable forecast at all, both start immediately.

### The implementation is one ordering

`AddGridBlocks` no longer walks the slices backwards from the deadline. It walks them by leftover
forecast surplus, highest first, breaking ties earliest-first, and anchors each block at its slice's
start. Both behaviours above fall out of that single comparison: a window with sun in it fills the
sunny slices first, and a window without any ties at zero everywhere and is therefore filled from the
front. Nothing else about the plan changes — the block is still sized by the same power-prorated
arithmetic, still rebuilt every poll from the measured delivery, and still never committed to.

`GridStart` is now the earliest of the grid blocks rather than the single start of one contiguous
block, and the blocks may be scattered across the window instead of ending at the deadline. Nothing
consumed it as "the last thing that happens" — the controller asks `IsInGridBlock(now)`, and the
discharge hold follows the same predicate — so the arming window moves with the block and stays
scoped to the importing part of the plan.

### The cost we accept

An import placed at noon is bought eight hours earlier than one placed at 05:00, and a day that goes
on to beat its forecast *after* noon cannot un-buy it. That is a real loss and it is smaller than the
one it replaces: the block is still resized every poll, so only the part actually drawn before the
sun arrives is lost, and against it we now collect sub-minimum surplus and forecast upside on every
day rather than only on the days generous enough to cancel the block outright.

### The live half: sun is never refused, and the grid may bridge to the floor

The placement rule above is a *forecast*-side fix, and it only reaches the surplus the forecast
predicted. Two live cases were still throwing sun away, and the mode's own contract — deliver the
energy, prefer the sun, the grid is allowed — says both should be taken.

**Unplanned sun.** The controller only charged from surplus inside a block the planner had drawn. A
half-hour the forecast said nothing about, or underestimated, found the car idle. The plan schedules
*imports*; it has no business refusing sunshine, so the solar-block gate is gone. The car now takes
live surplus whenever there is any, and the only thing that holds it back is the plan's SOC floor —
which is the pack's priority talking, and is meant to.

**Sub-floor surplus.** A 3.5 kW surplus cannot charge at all against a 4.14 kW floor, so the
controller paused and the energy was exported. The **grid bridge** runs the charger at 6 A and buys
the ~640 W difference instead. This reverses "there is no battery loan in this mode" only in its
source, not in its principle: the pack is still untouched, and the argument for the grid is that a
`SolarPlusGrid` plan has *already committed* to buying this energy — the only open question was when.
Bridging pays `floor − surplus` now against the whole floor later, and shrinks the planned block watt
for watt. It is refused where that argument fails: on a `Solar` plan (nothing committed), below the
SOC floor, and under `MinBridgeSurplusWatts` (the sun is not really contributing). Sized to the floor
and never past it — a bridge, not a booster.

The bridge's one sharp edge is the discharge hold. It arms on `IsInGridBlock`, and a bridge is not
inside a block. Left there, the controller commands 6 A against a surplus that cannot carry it and
the *pack* silently funds the gap — a battery loan by accident, which is the one thing this mode
promises never to do. So the decision now carries `GridBridgeWatts` out to the poll loop and the hold
arms on it too. (On an installation with no `BATTERY_HOLD_*` configured the hold is a log warning
rather than a write, and this drains the pack — that is pre-existing, and it is now one more reason
to configure it.)

---

## 2026-08-21 — Charging is an action, and the action owns the charger's use-mode

This reverses a promise the project made in as many words, in four places (`IEvChargerControl`,
`EvChargerControl`, `ChargingControlCoordinator`, and three separate README claims): **the controller
writes only the charge-current setpoint, never the use-mode, never a start/stop command.**

### Why that was right

It was right for what the controller was: a *modulator*. The owner made the decision — car plugged in,
charger set to Fast — and the controller's whole job was to move the current under a session somebody
else had started. In that shape, writing the use-mode would have been the controller taking a decision
that was not its to take, over hardware whose register map is transcribed from a third-party
integration rather than from a SolaX document. The narrow write surface was the safety argument, and
"the owner keeps the charger in Fast" was the price of it.

### Why it is wrong now

Because the mode stopped being a *setting* and became a *thing you press*. A select is a statement of
intent that waits for the world to agree with it: picked while the charger sits in Green it does
nothing at all, and says so only through a `Control state` sensor reading `Idle` — five words down a
dashboard, and the single most common "it isn't working" report this project produced. A button is a
promise that something happens. It cannot keep that promise without the use-mode.

Two surfaces already had the better shape and proved it: `Activate target` and `/targeted`'s `Cancel`
are presses, and nobody has ever had to explain either of them.

### What the reversal is careful about

- **`Fast` is written once, by the action, and nothing re-asserts it.** All four controllers keep
  their `if (use-mode != Fast) → Idle` precondition. A charger changed at the wallbox mid-session
  leaves the controller idle exactly as it does today. The owner has the last word on their own
  hardware; the controller does not fight them for it, poll after poll.
- **`Off` writes `Stop` and does not touch the current setpoint.** `Stop` is what stops the car;
  re-writing the setpoint would be a second write to say the same thing.
- **Shutdown is unchanged.** `PauseOnShutdownAsync` still pauses the current and does **not** write
  `Stop`: a container restart for a deploy is not the owner saying "stop".
- **The self-off paths run the same action.** `FastNoBattery` and `Targeted` finishing now stop the
  charger too — otherwise a mode that switches itself off would leave the charger in Fast at the pause
  current, which is a different end state from the button and one a car can start drawing from again.
- **There is still no enable flag, and nothing is written until a button is pressed.** That was
  already the property `ChargeControl:Enabled`'s removal bought; it is now more literally true.

### The one asymmetry, and why

A failed use-mode write on **start** leaves the mode alone: nothing happened, and the surface says so.
A failed write on **stop** still returns the mode to `Off`, and *also* reports the failure. Releasing
control is the half of the button that cannot depend on the hardware answering — a controller left
driving a charger it cannot reach would go on commanding currents at a wallbox whose owner has just
said stop, which is the worse of the two failures by a distance.

---

## 2026-08-20 — The grid block goes as late as it can, and the home battery still comes first

Issue #80 adds a fifth charge mode: an amount of energy, a departure time, and "use as little grid as
you can". Two decisions in it are worth recording, because both look wrong at first glance.

### The grid block is placed as late as it possibly can be

The obvious arrangement is the opposite: a car plugged in at 21:00 for an 07:00 departure could start
importing immediately and be done by 23:00, and everyone sleeps easier. The plan does the reverse —
it computes the import it still needs and places that block **ending at the deadline**, so nothing is
imported until the last possible moment.

The reason is that **the plan is rebuilt every poll and never committed to**. Between 21:00 and 05:00
there are thousands of chances for the forecast to improve, for the car to have taken more than
expected, or for a sunny morning to appear inside the window. Every one of them shrinks the block —
often to nothing — *before a single watt of it is bought*. Scheduling the import early converts all
of that optionality into a purchase. Late placement costs nothing in exchange: the deadline is met
either way, because the block is sized to meet it.

The cost is that a failure late in the window has less time to recover. That is exactly what the
comparison against `P_max × (departure − now)` is for: the moment the remaining need exceeds what the
charger could physically deliver in the time left, the plan says so — with the shortfall and the
departure that would have covered it — instead of quietly falling short.

It also means the block must be found by a **backward pass**, not by `deficit / P_max`. Where the
block reaches back over a sunny slice the grid only has to supply `P_max` minus what the sun is
already giving, so the start is prorated inside that slice by power rather than by time. The naive
division understates the block and misses the deadline on exactly the days the mode exists for.

### The home battery keeps its priority, and the grid pays for the gap

The car has a deadline here and the pack does not, which is an argument for letting the car take the
pack's surplus and refilling it tomorrow. It is rejected, for two reasons.

The first is that the two deadlines are not comparable. The car's is a promise the owner made and can
change — moving the departure by an hour is a keystroke. The pack's evening 100% is what carries the
house through the night, and its cost when missed is paid by the house at the evening tariff, not by
the person who set the target.

The second is that the alternative is not free energy, only a **more expensive route to the same
kilowatt-hours**. Energy taken from the pack and repaid tomorrow pays a round-trip loss and a cycle on
both packs; the grid block pays the tariff and nothing else. Taking the pack's share would not reduce
the import, only move it to the evening — which is why there is no battery loan in this mode, and why
the discharge hold arms while the grid block runs. The pack is not a cheaper source here. It is the
same source, borrowed at a loss.

The hold is scoped to the importing part of the plan rather than to the whole mode, unlike
`FastNoBattery`. Outside the grid block the car is running on surplus the pack has already been
offered and declined, and holding the pack there would push the house onto the grid for nothing.

## 2026-08-16 — The product is Gleanvolt; the vendor is still SolaX

**Context.** "SolaX Local Controller" put someone else's trademark in the project's name, its
repository URL and the image everyone pulls. That is tolerable for a hobby project and not tolerable
for anything sold, and it also mislabels the roadmap: the control logic is vendor-agnostic, and the
intention is to support other inverter makes.

**Decision — rename the product, not the vendor.** The split is the useful part of this change:

| Renamed (product identity) | Kept (vendor identity) |
|---|---|
| Assemblies and namespaces `Solax.*` → `Gleanvolt.*` | The `Solax:` configuration section and `SolaxOptions` |
| `AddSolaxController()` → `AddGleanvolt()`, `UseSolaxController()` → `UseGleanvolt()` | `SOLAX_*` variables in `.env` |
| `SolaxPollingService` → `PollingService` | `Gleanvolt.Infrastructure/RegisterMaps/*` |
| The solution, the image, the repository, the UI title, the HA display name | `InverterRegister`, `InverterControlRegister`, `EvChargerRegister` |
| Container names and `/opt/solax` → `/opt/gleanvolt` | Every mention of SolaX hardware in prose |

`Solax:Inverter:Host` configures a **SolaX inverter**, and when a second make is supported it will
get its own section beside it rather than inheriting this one. Renaming it to `Gleanvolt:` would have
been a breaking change to every deployment that also made the eventual multi-vendor layout worse.

**Decision — Home Assistant keeps its identity.** `DeviceId` stays `solax_controller` and `BaseTopic`
stays `solax`; only `DeviceName` becomes "Gleanvolt". Those two strings are the primary key of every
entity Home Assistant has created: changing them renames every entity, orphans all recorded history,
and breaks every dashboard and automation built on them — for a cosmetic gain, since the display name
is the only part a user sees. The retained discovery topics would also need clearing from the broker
or the old device would linger as a ghost. This is a change worth making once, at a major version,
with a migration path — not as a side effect of a rename.

**Decision — the repository was renamed in place, not recreated.** GitHub redirects the old URL
permanently (verified: 301), so every existing clone, link and bookmark keeps working, and the issues
and pull requests that carry most of this project's design rationale stay attached. Deleting the old
repository would have broken all of that permanently while revoking nothing. `publish-image.yml`
derives the image from `${{ github.repository }}`, so the image name followed the rename by itself.

**The historical logs were left alone.** `DECISIONS.md` and `IMPLEMENTATION_LOG.md` are append-only
and describe the tree as it was at the time; rewriting them to say "Gleanvolt" would make them
describe a repository that never existed. The old repository URLs in them still resolve through
GitHub's redirect.

## 2026-08-16 — Noncommercial from here on, and the terms ship inside the artifact

**Context.** The intent is that the controller stays free for the people it was written for — someone
running it on their own house — while commercial use, an installer deploying it for clients or a
vendor bundling it with hardware, is licensed separately. MIT grants commercial use expressly, so the
intent and the licence disagreed.

**Decision — PolyForm Noncommercial 1.0.0, from this version onward.** Chosen over writing our own
terms because the hard part is defining "commercial", and PolyForm's wording is drafted and tested;
over BSL/FSL because a change date is a promise this project has no reason to make yet; and over
AGPL-plus-exception because AGPL barely inconveniences a company running a controller on its own LAN,
so it would not produce the conversation it exists to produce.

**The change is not retroactive, and the repository says so.** Every version published under MIT
stays MIT — `LICENSE-MIT` is kept in the tree and referenced from `LICENSE` and the README. A licence
change cannot withdraw a permission already granted, and pretending otherwise in the README would be
both wrong and a bad look. The practical consequence is that someone can fork the last MIT commit;
that is the accepted cost, and it is small for a project whose value is register maps verified against
real hardware.

**Decision — the terms live inside the artifact, not only beside it.** Three places, because each one
can be lost independently:

| Where | Why |
|---|---|
| `/app/LICENSE` and `/app/LICENSE-MIT` in both images | A registry label is stripped by any re-tag or re-push; a file in the image is not. `.dockerignore` no longer excludes them. |
| `org.opencontainers.image.licenses` label | The machine-readable form, for `docker inspect` and scanners. Set in the Dockerfile so both images carry it — the Windows image, built with plain `docker build`, previously had no OCI labels at all. |
| The licence text inside the NuGet packages | The packages already carry `PackageLicenseFile` rather than an SPDX expression, so they pick this up with no change. See below. |

`publish-image.yml` passes the same value to `metadata-action`'s `labels:` input. Left to itself,
that action fills the field from GitHub's licence detection, which does not recognise PolyForm and
would leave the image unlabelled — or still claiming MIT — while `build-push-action`'s `--label`
silently overrode the Dockerfile.

**Consequence — the packages needed no change, and could not have used an expression anyway.** They
already declare `PackageLicenseFile`, so they ship whatever `LICENSE` says. That turns out to have
been the only option open to them: NuGet accepts SPDX expressions only for licences approved by the
OSI or the FSF, and `PolyForm-Noncommercial-1.0.0` is a real SPDX identifier approved by neither, so
`PackageLicenseExpression` would have been rejected outright. The file form is what every
commercially licensed package on nuget.org uses, and it has the same virtue as the image copy: the
terms are inside the artifact.

**What this costs.** The project is no longer OSI open source. That most likely rules out a HACS or
Home Assistant add-on listing, changes what the GitHub repo signals to a passing developer, and makes
the libraries a trial rather than a funnel. Accepted deliberately: the alternative was a stated policy
with nothing behind it.

**What it does not buy.** Nothing here detects anything. A container on someone's LAN reports home
never, and there is no key to check. These terms are a basis for invoicing an organisation that cares
about compliance; they are not a control, and should not be mistaken for one.

## 2026-08-16 — The composition root is a library; the executable is only a host

**Context.** `Program.cs` had grown to 399 lines, and the only way to run the controller was to run
`Solax.Worker`. That was an accident rather than a choice: of the 24 files in the project, exactly two
— `Program.cs` and `DotEnv.cs` — were specific to *that* executable. The polling service, the
coordinator, the selectors, the Home Assistant worker, the session recorder, the forecasting types
and all seven options classes were host-agnostic library code that happened to live inside an
`Microsoft.NET.Sdk.Web` project, and therefore could not be referenced, packaged or composed
anywhere else. The DI wiring could not be exercised at all except by booting the process.

**Decision — the wiring moves to `Solax.Hosting`, and the executable keeps only what makes it an
executable.** `AddSolaxController()` holds every registration; `Solax.Worker` keeps the `.env` load,
the Serilog configuration, the `hash-password` tool and the exit code, and lands at ~95 lines. The
layer order becomes `Solax.Worker` → `Solax.Hosting` → `Solax.Infrastructure` → `Solax.Core`.

`Solax.Worker` now references `Solax.Hosting` **and nothing else**. That is the enforcement, not the
comment: host code cannot reach `Solax.Core` or `Solax.Infrastructure` directly any more, so the
composition root cannot quietly grow a second half back inside the entry point.

**Decision — the primary shape is `IServiceCollection`, not `WebApplicationBuilder`.** A host that is
not built on `WebApplicationBuilder` — a console host, a desktop shell — should still be able to run
the controller, so the registrations take an `IServiceCollection` and an `IConfiguration`. Two
consequences:

- The UI's port is set with `services.Configure<KestrelServerOptions>(...)` rather than
  `builder.WebHost.ConfigureKestrel(...)`. These are the same registration; only the second needs a
  builder.
- `UseStaticWebAssets()` is the one thing that genuinely cannot be expressed as a registration, so it
  is the only content of the `WebApplicationBuilder` overload. Without it, an unpublished build
  serves `blazor.web.js` as an empty 200 and the UI renders once and then dies — see the 2026-08-13
  record.

**Decision — the implicit usings are declared in the `.csproj`.** The moved code was written against
the Web SDK's implicit usings, which a plain class library does not supply. They are re-declared as
`<Using Include="..." />` items rather than added as 22 files' worth of `using` directives, so that
the move stays a move and the diff stays reviewable.

**Decision — the four libraries are packages; the executable is not.** `IsPackable` defaults to
`false` in `Directory.Build.props` and each library opts in, so a project (a test project especially)
cannot start publishing itself by existing. The ids `Solax.Core`, `Solax.Infrastructure`, `Solax.Web`
and `Solax.Hosting` were all unregistered on nuget.org when checked on 2026-08-16, so the package ids
match the assembly names. The version is the same tag-derived number the image already uses, which is
what makes a package, an image and a commit one release rather than three.

**Consequence — `BuildInfo` reads `Solax.Hosting`'s attributes now, not `Solax.Worker`'s.** It reads
its own assembly, and it moved. The reported string is unchanged because `Directory.Build.props`
stamps every project in the repo with the same version and commit; a consumer that packages
`Solax.Hosting` separately would see that assembly's version, which is the right answer for them.

## 2026-08-16 — The service can be stopped from its own surfaces, and the exit code is what keeps it stopped

**Context.** The only way to take the controller down was to kill it — no control surface offered a
stop, so it meant an ssh session and, in practice, `docker rm -f` or a pulled plug. That skips the
host's shutdown, and the expensive part of skipping it is not the data: SQLite is crash-safe in WAL
mode and the Serilog file sink is unbuffered, so a killed process loses almost nothing on disk. It is
the hardware. `SolaxPollingService.StopAsync` exists precisely to drop the charger's setpoint to the
pause current on the way out; kill the process and nothing revokes it, so **the car keeps drawing at
whatever current we last wrote**. The open charging session is also left to be recovered as
interrupted rather than closed as `ServiceStopped`.

**Decision — a stop is a control like any other, driven through one Core seam.** `IServiceShutdown`
(`RequestStop(string source)`) joins `IChargeControlModeSelector` and friends: the web UI's Health
page and a Home Assistant `button` entity both drive it, neither owns any logic, and `HostShutdown` in
the worker turns it into `IHostApplicationLifetime.StopApplication()`. The existing `StopAsync`
implementations then do what they were always written to do. Nothing new had to be added to the
shutdown path — only a way to trigger it that isn't a shell.

**Decision — the process's exit code distinguishes "stopped" from "terminated", and the deploy stack
reads it.** `restart: unless-stopped` cannot express what an operator means by "stop": it restarts on
any exit, so a Stop button would be a Restart button with extra steps. `restart: on-failure` can
express it, but only because the worker now sets its exit code deliberately:

| How the run ended | Exit code | Docker's response |
|---|---|---|
| `RequestStop` from the UI or Home Assistant | `0` | stays down |
| SIGTERM — reboot, daemon restart, `docker compose restart` | `143` | comes back |
| Crash, OOM kill, power cut | non-zero | comes back |

Without the 143 the two are indistinguishable — .NET exits 0 for a SIGTERM as well — and a Pi that
rebooted would come up with no controller running and nothing anywhere saying why. That case is not
hypothetical on this deployment: the box hard-stops on its own, and automatic recovery from it is a
property we are not willing to trade for a Stop button. Verified against Docker 29.7.1: a container
exiting 0 under `on-failure` stays exited, one exiting 143 is restarted, and a `docker compose stop`
stays stopped regardless of the code.

**Decision — the shutdown pause has a deadline, and the grace period has headroom.** Docker's default
10-second grace would SIGKILL the container part-way through the one thing a graceful stop exists to
do. Measuring a real stop showed why the number has to be generous: it took **19 seconds** with
nothing charging, because a Modbus read already in flight when the stop arrives cannot observe the
cancellation until it times out, and an EV charger that has gone quiet costs 5 seconds per unanswered
exchange. `stop_grace_period` is therefore 60s.

That alone would not be enough, because the failure it guards against is the one that matters most:
had a session been active, `PauseOnShutdownAsync` would then have done its own read-then-write against
that same silent charger, and a shutdown that spends its whole budget waiting gets killed *during the
pause write*, with a car still drawing. So the pause is bounded independently
(`ChargingControlCoordinator.DefaultShutdownPauseTimeout`, 10s): a charger that answers needs well
under a second, and one that doesn't is not going to start. Giving up on a stated deadline and saying
so in the log leaves the operator a fact to act on; waiting forever leaves them a SIGKILL.

**Decision — a run that ends properly says so, on its last line.** Until now a graceful stop left the
log simply ending after the last poll, which is indistinguishable from the process dying mid-cycle.
The closing line names which of the two cases it was, who asked for it, and the exit code the restart
policy will read — so the *absence* of the line is now itself the diagnosis. This matters more here
than it would elsewhere: the Pi's journal is RAM-only and the box hard-stops on its own, so the
controller's log file is the only account of a run that survives the reboot. The line is written from
`ApplicationStopped` rather than after `Run()` returns, because by then the service provider — and
with it Serilog and its file sink — has been disposed and the write would be swallowed in silence.

**Consequences accepted.** Stopping from the UI or Home Assistant is one-way from those surfaces: the
service *is* both of them, so starting it again needs `docker compose start solax-controller` on the
Pi. That is documented next to the stop rather than designed around. A standby mode — the process
staying up, idle, with the UI serving a Start button — would avoid it, and is the obvious next step if
the round trip through ssh becomes annoying; it was considered here and rejected as more machinery
than the problem currently justifies. Also, with no `Web:PasswordHash` configured the stop control is
as open as the rest of the UI, which is called out in the README rather than special-cased: a control
surface that anyone on the LAN can drive is already the documented default.

---

## 2026-08-15 — The web UI's default port moves from 8080 to 8090

**Context.** 8080 is the most contended port on a general-purpose Linux box. The reference Pi turned
out to have Kodi installed, whose HTTP remote-control interface defaults to 8080 as well; a Pi that
also ran a proxy, a dev server, or any of the many appliances that assume 8080 would collide the same
way. The failure is unpleasant to diagnose because it depends on boot ordering: whichever process
binds first wins, and the loser reports only that the port was unavailable.

**Decision — the default is 8090, in one place, and everything else follows it.** `WebOptions.Port`
is the single source of truth; `appsettings.json` ships the same number, `docker-compose.yml`
defaults `WEB_PORT` to it for both the published port and `Web__Port`, and both Dockerfiles' `EXPOSE`
lines document it. 8090 is not special beyond being far less contended — the point is that the
out-of-the-box experience should not require choosing a port, and 8080 could no longer deliver that.

**This changes the URL of an existing deployment.** A Pi that never set `WEB_PORT` moves from
`:8080` to `:8090` on the next deploy, because the default it was relying on changed underneath it.
Anyone who wants the old URL sets `WEB_PORT=8080` in `.env` explicitly, which is exactly the knob
that already existed for moving the port; nothing else needs to change. Note that `WEB_PORT` moves
the host *and* container port together, so the container's internal port changes too — nothing binds
8080 anywhere afterwards unless it is asked to.

---

## 2026-08-13 — Home Assistant and the broker become compose profiles, and the controller stops depending on either

**Context.** Issue #51, the last phase of #44. The web UI (phases 0–5) made Home Assistant optional
in principle — the controller can be fully driven from its own dashboard — but `docker-compose.yml`
still started all three containers unconditionally, and `MQTT_USERNAME`/`MQTT_PASSWORD` were
required with no default. A Pi that only wanted the controller and its UI still paid for, and had to
configure, a broker and a copy of Home Assistant it never used.

**Decision — `mosquitto` and `homeassistant` carry compose `profiles:`; `solax-controller` does
not.** A service outside the active profile set simply isn't created, which is what makes
`docker compose up -d` with no active profiles start the controller alone. `COMPOSE_PROFILES` is
docker compose's own environment variable, and it can come from `.env` beside the compose file — but
see the next decision for why that isn't where this repo sets it.

**Decision — which profiles are active is chosen by which deploy script runs, not by `.env`.**
`deploy/deploy.sh` (full stack) and `deploy/deploy-controller-only.sh` (controller alone) each set
`COMPOSE_PROFILES` explicitly for their own `docker compose pull`/`up`/`ps` invocations, overriding
whatever the value already sitting in `.env` on the Pi happens to be. The first design put
`COMPOSE_PROFILES=mosquitto,homeassistant` in `.env.example` instead and left both containers to a
single `deploy.sh`; rejected once written down, because it made the deployed stack a function of a
line in a file that nothing forces to match reality — run `deploy.sh` once with the full-stack line
in `.env`, delete the line without redeploying, and the containers happily keep running against a
`.env` that now claims they shouldn't exist. Two scripts make the choice a command someone actually
runs, not a value that can go stale next to it.

**Decision — soft dependencies, not hard ones.** `solax-controller`'s (and `homeassistant`'s)
`depends_on: mosquitto` gained `required: false`. Without it, compose refuses to start a service that
depends on one whose profile isn't active at all — the opposite of "optional". The controller's own
tolerance for an unreachable broker (`HomeAssistantMqttWorker` already retries and logs) was already
there; this only stops compose itself from getting in the way before that code ever runs.

**Decision — `HomeAssistant:Enabled` gets its own switch, separate from whether the broker
container exists.** `HOMEASSISTANT_ENABLED` (default `false`) drives it, distinct from
`COMPOSE_PROFILES`. Tying the app setting directly to "is the `mosquitto` profile active" was
rejected: compose has no way to expose "which profiles are active" as a value inside another
service's environment, so the two would have needed to agree by convention regardless — making them
two explicit settings is more honest than one setting pretending to control both.

**Decision — the UI's port is published from a second compose file, not a `ports:` entry in the
base one.** `Web:Enabled=false` was built (phase 0) to guarantee no listening socket **inside** the
container, verified with `ss -ltnp`. An unconditional `ports:` mapping would have reintroduced
exactly that hole one layer up: Docker's port-publish proxy binds the **host** port regardless of
whether anything inside is listening, which is a real, checkable difference from "no socket at all"
that the original guarantee was written to rule out. `docker-compose.web.yml` carries the mapping
instead, merged in only via `.env`'s `COMPOSE_FILE=docker-compose.yml:docker-compose.web.yml` — a
second file over a conditional line, because compose has no syntax for "this one attribute, only if
this variable is truthy" within a single service block.

**Consequences.**

- **A pre-existing deployment must add `HOMEASSISTANT_ENABLED=true` to `.env` to keep publishing MQTT
  discovery after upgrading to this release** — it was already running `deploy.sh`, so the containers
  themselves need nothing (`deploy.sh` still means the full stack, unconditionally), but
  `HomeAssistant:Enabled` was previously hardcoded `true` in `docker-compose.yml` and is now this new,
  off-by-default environment variable. Flagged prominently in `.env.example` and `deploy/README.md`,
  the same way the `TZ` and timezone-fail-fast changes were before it.
- **`MQTT_USERNAME`/`MQTT_PASSWORD` are no longer hard-required** (`${VAR:?message}` became
  `${VAR:-}`) — a controller-only `.env` no longer needs credentials for a broker it never starts.
  Home Assistant's own MQTT integration setup is unaffected; nothing there reads these two.
- **The broker-password-file check moved into `deploy.sh` unconditionally, and out of
  `deploy-controller-only.sh` entirely** — each script only checks for what it might actually need,
  rather than one script grepping `.env` to guess. The Home Assistant config seed step stays
  unconditional on both — it only touches files on disk, costs nothing when unused, and means
  switching from `deploy-controller-only.sh` to `deploy.sh` later needs no extra step.
- **The reference footprint drops from 848 MB to roughly 200–250 MB of the Pi 3 B+'s 905** with
  Home Assistant and the broker off — the number [issue #44](https://github.com/mpospisil/solax-controller/issues/44)
  projected at the start of this work, now the documented, deployable default for anyone who wants
  it. See `deploy/README.md`'s "Running without Home Assistant" for the full table.
- **Both Dockerfiles already published from `dotnet/aspnet`, not `dotnet/runtime`**, and already
  reasoned about "Web:Enabled=false must mean no socket" — both landed with the UI itself in an
  earlier phase (see the record below). This phase only adds `EXPOSE 8080` (documentation; it opens
  nothing by itself) and extends that same reasoning from the container's network namespace to the
  Docker host's.

---

## 2026-08-13 — The web UI is a Blazor library the worker hosts, and the host is a web server only when asked

**Context.** Home Assistant is the only control surface, and on the reference Pi 3 B+ it is also the
binding constraint: HA alone accounts for 600 MB of the 848 MB the stack reserves out of 905 MB. A
self-hosted UI (issue #44) lets the controller run useful on that hardware. It is explicitly *not* a
replacement for the MQTT integration — both surfaces stay first-class, and all four combinations of
the two must run.

**Decision — a second adapter over the existing seam, not a second architecture.** The UI consumes
exactly what the MQTT worker consumes: `ChargeControlStatusHolder` for state, and the Core selector
interfaces for commands. No control logic moved, and nothing in `Solax.Core` changed semantically.
The one refactor is that the holder moved from `Solax.Worker` to `Solax.Core`, because a second
consumer in a second assembly cannot depend on the host. It is a plain object with an event, so
Core's "no framework dependencies" rule is untouched.

**Blazor with interactive server rendering, in a Razor Class Library (`src/Solax.Web`) that
`Solax.Worker` hosts.** Three choices, each with an alternative that was rejected:

- **Server-side, not WebAssembly.** The components run beside the services they read, so a page
  subscribes to `ChargeControlStatusHolder.Updated` directly and each completed poll pushes down the
  circuit. WebAssembly would need a REST API this project needs for nothing else, plus a first-load
  download onto a LAN appliance. With one or two browsers, the circuit's overhead is noise.
- **Interactive, not static SSR.** A dashboard that shows a live system has to update itself; a
  page that only tells the truth at F5 is worse than no page.
- **A library the worker hosts, not an executable of its own.** One process, one container, one
  entry point. Making `Solax.Web` the executable would churn both Dockerfiles, the CI publish
  matrix and the test projects for no benefit.

**Consequences accepted.**

- **The runtime image is now `dotnet/aspnet`, not `dotnet/runtime`** — roughly 25 MB more image on
  every platform, including for people who never switch the UI on. Unavoidable: the framework
  reference is a property of the build, not of the configuration, and the process will not start
  without the framework it was compiled against.
- **Disabled has to mean no socket, and that took code.** Kestrel binds its default address when no
  endpoint is configured, so an ASP.NET host with nothing mapped would still open a port the operator
  never asked for. With `Web:Enabled` false the host registers `NoListenServer` in Kestrel's place,
  which starts nothing and accepts nothing; the process is then indistinguishable from the headless
  worker it was before. Verified with `ss -ltnp`: no listening socket in the process at all.
- **The host project contains no `.razor` file, and the SDK infers Blazor support from exactly
  that.** Without `RequiresAspNetWebAssets=true` in `Solax.Worker.csproj`, `_framework/blazor.web.js`
  is never published: pages prerender correctly, no circuit is ever opened, and the only evidence is
  a 404 in the browser's console. It cost an hour to find, and it is the sort of thing that would
  otherwise be rediscovered on the Pi.
- **Static assets must be asked for explicitly** (`builder.WebHost.UseStaticWebAssets()`). Outside
  the Development environment, assets that live in build output rather than a `wwwroot` folder are
  served as an empty HTTP 200 — the page renders and silently never updates. Free in a published
  app, where the manifest it reads is absent.
- **`Solax.Web` takes a `FrameworkReference` on `Microsoft.AspNetCore.App`** rather than the
  `Microsoft.AspNetCore.Components.Web` package the RCL template offers. These components only ever
  run server-side, so WebAssembly compatibility buys nothing and a duplicated assembly costs.
- **The UI ships off by default** (`Web:Enabled: false`), like the Home Assistant integration.
  Authentication is a later phase of #44, and an unauthenticated control surface must not appear on
  a LAN because somebody upgraded.

---

## 2026-08-12 — The Pi boots from an SD card because it cannot boot from the disk it runs on

**Context.** The deploy host's USB SSD — an OCZ Vector 180 — began returning unrecoverable read
errors. The failure was not subtle once located, but it presented as three unrelated symptoms over
half an hour: `dpkg` refusing to read its own status file, a cached `.deb` failing to decompress, and
Docker aborting a Home Assistant image layer with `failed to register layer: corrupt stream`. Each
looked like a software fault. All three were `critical medium error` on new sectors each time. The
drive was replaced with a Crucial P1 1 TB NVMe in a Realtek RTL9210 USB enclosure.

**Hardware contradicted the plan: the Pi 3 will not boot that enclosure.** The image was correct —
right `bcm2710-rpi-3-b-plus.dtb`, right `kernel8.img`, `cmdline.txt` naming a PARTUUID that existed,
partition type `0xc`. It still would not boot. The Pi 3 boot ROM speaks only USB Bulk-Only Transport
and allows roughly two seconds for enumeration; an NVMe behind a USB bridge does not answer in time.
The previous SATA SSD did, which is why USB boot had always worked before and its failure now looked
like a configuration mistake rather than a hardware limit.

**Decision — split the boot.** `/boot/firmware` is a 512 MB FAT32 partition on an SD card; `/` is the
M.2. The boot ROM reads the SD, which it has always been able to do, and `cmdline.txt` hands root
straight to the NVMe, which the Linux USB stack drives without complaint. The alternatives were worse:
burning `program_usb_timeout=1` is a permanent OTP write that might not have helped, and going back
to an SD-rooted Pi puts every Docker layer and the session database on the component most likely to
fail.

**Consequences accepted.**

- The boot device is now a single SD card with no redundancy. It holds 76 MB of firmware and kernel
  and is written only by kernel updates, so the wear profile that kills SD-rooted Pis does not apply
  — but losing it means the machine does not start, however healthy the NVMe is.
- `/etc/fstab` must point `/boot/firmware` at the **SD's** PARTUUID. The M.2 keeps its own orphaned
  boot partition from the original image, and pointing at that one is silent: updates land somewhere
  never booted, and the failure surfaces weeks later at an upgrade. A first-boot resize also rewrites
  every PARTUUID on the M.2 — the firstboot script repairs `cmdline.txt` and the root line itself,
  and never the `/boot/firmware` line.
- Anyone editing `cmdline.txt` per the cgroup step is editing the SD card. That is correct, and it is
  not obvious.

**Not established:** whether the RTL9210 would boot with a longer ROM timeout or on a powered hub.
Neither was tried, because the split boot removed the reason to care. Power draw remains the open
risk — an NVMe in a bus-powered enclosure can brown out a Pi 3 under load.

**Trixie made the swap step obsolete at the same time.** Raspberry Pi OS 13 does not ship
`dphys-swapfile`, so the documented commands fail outright. The image configures zram instead —
zstd-compressed swap in RAM at priority 100, with writeback to `/var/swap` on disk — which is better
than the swapfile it replaces for this workload. The deploy guide now verifies swap rather than
creating it, and offers a low-priority disk swapfile only as an optional overflow tier, and only
because root is no longer on an SD card.

---

## 2026-08-10 — The git tag is the version; the build carries it, and says so

**Context.** Nothing running could say what it was. The image tag knew, but the process did not, so a
log file or a `docker logs` dump was untraceable to a build — and with three platforms now behind one
manifest list, "which one is on the Pi" had become a fair question with no answer from inside.

**Decision — one source of truth, and it is the git tag.** `Directory.Build.props` carries
`0.0.0-dev`, a deliberate placeholder. CI passes `-p:Version=<tag without the leading v>` when
building from a `v*` tag, so the number compiled into the binary and the number the image is tagged
with come from the same place and cannot drift. Storing a real version in the repo and bumping it by
hand was the alternative; it adds a commit per release whose only job is to agree with a tag.

**Decision — the commit travels with the version.** `-p:SourceRevisionId=<sha>` makes the SDK emit
`AssemblyInformationalVersion` as `1.0.0+<sha>`, which `BuildInfo` splits back apart. A version alone
is not enough: several builds share `0.0.0-dev`, and during a release the same version may be built
more than once.

**Consequences.**

- A build that did not come from a tag reports `0.0.0-dev` rather than claiming a number it does not
  have. Unstamped and un-versioned is the honest state for a local build, and it is visible at a
  glance in a log.
- The version is logged as the first line at startup and published as Home Assistant's `sw_version`.
  The latter is device metadata, so it adds no entity and no row to the README's entity table.
- The image tag string (`main`, `release-1.3`) and the assembly version are now computed separately
  in the workflow. They have different legal alphabets — no assembly version can be called `main` —
  and conflating them was the trap worth avoiding here.

---

## 2026-08-10 — One image name, three platforms, and a timezone that no longer comes from the OS

**Context.** The image was built for `linux/arm64` alone, because the Pi was the only target. Wanting
to run the controller on an x64 box and on a Windows host turns one build into three, and raises the
question the single-platform world never had to answer: what is any of them *called*.

**Decision — one package, one manifest list.** All three platforms publish under
`ghcr.io/mpospisil/solax-controller`. `:1.0.0` is a manifest list, so the same string pulls the arm64
image on the Pi, the amd64 image on a server and the Nano Server image on Windows, with no per-host
knowledge anywhere. Separate packages per platform were the alternative; they would have meant three
things to keep in version step and would have thrown away the platform selection Docker does for
free. Single-platform tags (`:1.0.0-linux-arm64`) still exist alongside, for pinning and for
debugging "which one did it actually pull".

**Decision — Windows means Nano Server ltsc2022, built in its own job.** A Dockerfile builds for one
OS and a Windows runtime stage needs a Windows daemon, so `Dockerfile.windows` is a second file built
on a `windows-2022` runner rather than a `--platform` on the existing one. ltsc2022 over ltsc2025
because an ltsc2022 container runs on both Server 2022 and Server 2025 hosts, while ltsc2025 demands
a 2025 host — the newer base buys nothing here and costs reach. Nano Server over Server Core for
~300 MB against ~2 GB, accepting that Nano Server ships no ICU.

**Decision — the local timezone is configuration, not an ambient property of the host.** This is what
the Windows image forced. The controller's day boundary, the daily loan reset and the zone id stamped
on every recorded session all came from `TimeZoneInfo.Local`, which on Linux is set by the container's
`TZ`. **.NET on Windows ignores `TZ` entirely.** A Windows container would therefore have run in UTC
whatever `docker-compose.yml` said, and silently filed evening sessions under the following day —
visible only as inexplicably bad charging decisions days later. `Controller:TimeZone` now names the
zone, resolved through a `ZonedTimeProvider` that overrides `LocalTimeZone` on the `TimeProvider` the
services already inject, so there is exactly one answer to "what day is it here".

**Consequences.**

- An unresolvable zone id is fatal at startup. Falling back to UTC is the failure mode the setting
  exists to prevent, so it must not be the failure mode of the setting itself.
- Empty stays the default and keeps today's behaviour. Linux deployments are unaffected; `TZ` in
  `deploy/docker-compose.yml` still does the job.
- On Nano Server the value must be a **Windows** zone id (`Central Europe Standard Time`), not IANA.
  Mapping IANA to Windows is an ICU feature and Nano Server has no ICU. The worker logs a warning at
  startup when it is running on Windows with the zone unset.
- `SolaxPollingService` reached for `TimeZoneInfo.Local` directly; it takes a `TimeProvider` now, like
  everything else that needed the zone.
- The publish workflow grew from one job to four (version, linux × 2 arches, windows, manifest). The
  manifest job is what keeps `:latest` pointing at the previous release until every platform of the
  new one exists.

---

## 2026-08-09 — Charging sessions are stored in SQLite, and published as immutable documents

**Context.** Everything the controller knew was live: a log line that scrolls past and a Home Assistant
entity overwritten on the next poll. Nothing survived a restart, so the question the forecast-driven
mode exists to answer — did the plan hold, and where did the car's energy actually come from? — had no
data behind it. Issue #32.

**Decision — SQLite (`Microsoft.Data.Sqlite`) as the local store.** The questions worth asking of this
data are queries ("sessions in July", "solar share by mode"), which rules out flat JSONL; and it runs
on a Raspberry Pi 3 B already giving 600 MB to Home Assistant, which rules out a second server process.
One file is also one artefact to back up. WAL journalling with `synchronous=NORMAL`, batched commits,
and a sample cadence coarser than the poll interval, because the binding constraint on that device is
SD-card write amplification rather than size.

`SQLitePCLRaw.bundle_e_sqlite3` is pinned to 2.1.12 as a direct reference: the version
`Microsoft.Data.Sqlite` 10.0.10 pulls in transitively (2.1.11) carries GHSA-2m69-gcr7-jv3q.

**Decision — the published document is not the table schema.** A closed session is immutable, so it can
be rendered as one self-contained `ChargingSessionDocument` (header + samples + events) carrying an
explicit `schemaVersion`. Local tables will churn as features are added; that document must not,
because it is what a future upload writes to object storage and what a web app reads. Ids are
locally-generated UUIDv7 — globally unique without a server, and time-ordered so they sort as object
keys.

**Decision — per-source energy is an attribution, not a measurement.** No meter says which electron
went where. `ChargingSourceAttribution` fixes the rule (surplus PV first, then the battery, then the
grid as residual) and guarantees the three shares sum to the measured draw. The measured battery share
stays separate from the loan the forecast mode *commanded*.

**Consequences.**

- The store is on by default, unlike every other feature that needed a flag. Those flags exist because
  those features write to hardware; this one only observes.
- Recording is a subscriber to `ChargeControlStatusHolder.Updated`, like the HA worker — the poll loop
  gains no dependency on a disk, and a store failure cannot stall a Modbus cycle.
- `ChargeControlStatus` gained one field, `SessionCompleted`. By the time a status is published the
  loop has already returned the mode to `Off`, so without it "the car finished" and "somebody switched
  it off" are indistinguishable to any later reader.
- Every sample carries the running totals, which is what lets a session interrupted by a power cut be
  closed at its last sample rather than discarded.
- The container needs a `data/` bind mount, chowned to the image's uid — the same trap the logs
  directory hit. That change belongs to the deploy branch (#26), which is not yet merged.

---

## 2026-08-09 — Entity names stay short; the explanation lives in the README

**Context.** Supersedes the record below, which put each entity's explanation into its friendly name
so that HA's hover text would say something. In use that was worse than the problem: every dashboard
row read as a truncated sentence, the entity list became hard to scan, and a name that is really a
paragraph is confusing rather than informative.

**Decision.** Entities keep their original short names — `Grid power`, `Charging now`, `Required SOC
floor` — and the `description` attribute is dropped with them. What each entity means, including the
sign conventions and the traps, is documented once in the README, under
*Home Assistant (MQTT) → What each entity means*.

**Consequences.**

- Hovering an entity in HA still tells a reader nothing beyond its name. Accepted: a short label that
  scans is worth more on a dashboard than a tooltip, and the explanation is one link away.
- One place to keep current. A new entity is documented by adding a row to that table; nothing in the
  discovery payload duplicates it, so the two cannot drift.
- The tests that enforced a `Label — meaning` name and a description attribute are gone, since there
  is nothing in the payload left to enforce.
- Names returned to what earlier installs already published, so `entity_id`s, dashboards and
  automations are unaffected either way — `entity_id` is fixed at first discovery and does not follow
  renames.

---

## 2026-08-08 — The entity name carries its explanation, because in HA the name is the tooltip

> **Superseded on 2026-08-09** by the record above: the long names were confusing in practice, and the
> explanations moved to the README. The finding about HA having no description or tooltip field still
> holds.

**Context.** The Home Assistant entities carried names and nothing else, so a reader looking at
`Grid power` or `Required SOC floor` had no way to learn what the number means or which direction its
sign runs without opening this repository.

**What was found.** Home Assistant has no description or tooltip field for an entity. The hover text
is the friendly name — the frontend sets it so a truncated name can still be read in full — and MQTT
discovery has no `description` key. Adding one has been requested and not implemented.

**Decision.** The name carries the explanation, because the name is the tooltip. Every entity is
named `Label — what it means`, e.g. `Grid power — positive while importing from the grid, negative
while exporting`. HA truncates the label in a card and shows the whole sentence on hover, which is the
only tooltip mechanism it has.

The detail that will not fit on one line is published as an entity **attribute**: `json_attributes_topic`
points at the state topic every entity already subscribes to, and `json_attributes_template` is a
*constant* JSON document carrying a `description` key. It renders identically on every state message
and shows up under Attributes in the more-info dialog.

**Consequences.**

- The template is authored as text but must render valid JSON, so it is built with `JsonSerializer`
  (escaping anything in the prose that would break it). Descriptions must not contain Jinja
  delimiters; a test enforces that, along with every entity having both halves — a name in
  `Label — meaning` form, and a description that isn't merely the name repeated.
- Dashboard labels are long and truncate. That is the point: a short name would leave the hover text
  saying nothing, which is the problem this record exists to solve.
- Entity ids on a **fresh** install derive from the new names and will be long. Existing installs are
  unaffected — `entity_id` is fixed at first discovery and does not follow later name changes, so
  dashboards and automations survive a rename.
- Attributes only appear once a state message has been received, and are cleared while the entity is
  unavailable. The state topic is retained and republished every `StatusInterval`, so this is only
  visible when the controller is not running at all.

---

## 2026-08-08 — A mode may end itself, and "the car is finished" is decided on power

**Context.** Issue #28, the `FastNoBattery` mode. It creates the most expensive state this controller
can ask for — maximum current, grid import, the home battery locked out of the house — for a goal that
completes: the car reaches its own charge limit. Leaving that armed until somebody looks at Home
Assistant is the obvious failure mode, so the mode has to be able to end itself.

**Decision 1: a controller can say "this is over".** `ChargingControlDecision` gains
`SessionComplete`, carried up through `ChargeControlCycleResult`, and the poll loop answers by writing
the pause current and calling `IChargeControlModeSelector.Set(Off, …)`. The mode change is applied
*before* the battery hold is reconciled in the same cycle, so the inverter release goes out on the
same poll rather than the next one.

This is the first time control flows from a strategy back into the mode selector. The alternative —
a scheduler or timer outside the strategies — would have needed its own copy of "is the car still
drawing", which is exactly what the strategy already sees. The selector's existing contract does the
rest: `Set` logs and raises `Changed`, and the HA worker republishes the retained select state from
`_mode.Mode` on its next status tick, so the mode flipping under the owner needs no new plumbing.

**Decision 2: completion is a power judgement, corroborated by status.** The X1/X3-HAC's end-of-charge
status is firmware-specific and **has not been observed here yet** — the mode ships before a full
session has been logged through it. So the rule is built on the reading that cannot be misinterpreted:

- idle = draw at or below `CompletionPowerThresholdWatts` (200 W), *or* status `SuspendedEv` /
  `Finishing`, which is the car declaring itself done even while trickling for conditioning;
- finished = idle continuously for `CompletionDwell` (2 min);
- and only once the car has drawn power at least once this session, which is what separates "finished"
  from "hasn't started".

`ChargePaused` and `SuspendedEvse` are excluded on purpose: those are the *charger's* state, and our
own pause write produces them. Including them would let the controller read its own pause back as a
finished charge.

**Consequences.**

- The 200 W threshold sits in a wide gap — a charger's standby draw is tens of watts, its 6 A floor is
  1.4 kW single-phase and 4.1 kW on three — so no realistic reading is ambiguous.
- A car that pauses mid-session for longer than the dwell (thermal management, a utility signal) will
  be read as finished and the mode will end. Acceptable: ending returns control to the owner rather
  than doing anything to the car, and the owner reselects the mode.
- **Still to verify on hardware:** what the charger actually reports as the car finishes. Log a full
  completed session and, if the observed transitions contradict the rule above, amend it here.

---

## 2026-08-02 — The Pi runs containers it did not build, and holds no state inside them

**Context.** Issue #26: move the service off a developer laptop onto a Raspberry Pi 3 B (Raspberry
Pi OS Lite 64-bit) so it runs unattended. Three containers — the controller, Home Assistant, and an
MQTT broker. The board has **1 GB of RAM, an SD card, and an arm64 CPU**, and all three constraints
shaped the design.

**Decision 1 — CI builds the image; the Pi only pulls.** A `dotnet restore` + `publish` on a 1 GB
Pi 3 B takes tens of minutes and can be OOM-killed. GitHub Actions builds `linux/arm64` and pushes to
GHCR; the Pi runs `docker compose pull`. `sha-<short>` tags make a rollback a one-line command.

**Decision 2 — cross-compile, don't emulate.** The obvious way to build arm64 on an amd64 runner is
QEMU, which works and is roughly ten times slower. Instead the SDK stage is pinned to the *builder's*
architecture (`FROM --platform=$BUILDPLATFORM`) and targets the other one via `dotnet publish -a
$TARGETARCH`, so the compiler runs natively and only the output is arm64. The runtime stage was then
written with **no `RUN` instruction at all** — the logs directory is created in the build stage and
`COPY --chown` sets ownership — so nothing arm64 ever executes at build time and the workflow needs
no QEMU setup step. Measured: 75 s for a cold cross-build, of which the publish itself is 3.5 s.

**Decision 3 — the Debian runtime image, not chiseled.** Chiseled is ~80 MB smaller, but it omits
tzdata and a shell. Log timestamps and `SolarForecast.ForDate` are timezone-sensitive, and this is a
headless box where the diagnostic path is `docker exec`. The disk saving is not worth either.

**Decision 4 — no state inside any container.** Every container must be destroyable with
`docker rm -f` and recreated with no loss; that is what makes upgrade and rollback routine rather
than risky. All state is on **bind mounts under `/opt/solax`** — chosen over named volumes because
the data is then visible to ordinary shell tools over SSH, without `docker volume inspect`
indirection. The consequence accepted: bind mounts carry host uids, so the deploy documents chowning
`logs/` to 1654 (the .NET image's non-root user) and `mosquitto/` to 1883.

**Decision 5 — the production broker authenticates.** The dev stack's `allow_anonymous true` is not
carried over. These topics include the charge-mode select and the battery-hold switch, so anonymous
access is control of the inverter and charger. The broker also publishes **no host port** — only the
compose network reaches it. No application change was needed: `HomeAssistantOptions` already had
optional `Username`/`Password`, previously unused.

**Consequences.**

- **1 GB is the binding constraint, and Home Assistant is the risk.** Per-service `mem_limit`s
  (600/200/48 MB) leave ~170 MB for the OS. They only take effect if cgroup memory accounting is
  enabled in `cmdline.txt`, which Raspberry Pi OS ships **off** — an easy silent failure, so it is
  step 3 of the setup. If HA cannot be made to fit, the fallback is moving it to another host; the
  three services are independent precisely so that stays a compose edit.
- **SD-card wear is the long-term failure mode.** Container logs are size-capped, the broker logs to
  stdout rather than its own file, and the seeded HA `recorder` config uses `purge_keep_days: 3` with
  `commit_interval: 30`.
- **Serilog's `SelfLog` is now enabled** (`Program.cs`), the one `src/` change the deployment forced.
  Verified: with a logs bind mount the non-root user cannot write, the file sink fails and the
  process carries on — console logging normal, container healthy, `docker diff` empty, and the log
  files silently never created. That is the exact failure a bind mount invites (Docker auto-creates a
  missing mount source as root), so it must not be silent. `deploy.sh` also creates the directory and
  hands it to uid 1654 when it is missing or wrongly owned, fixing it before the stack starts rather
  than a month later.
- **The controller is stateless, which costs one Solcast call per restart** — the forecast cache is
  in-memory. Normal operation is unaffected, but a crash-restart loop burns the free-tier daily quota,
  so restart counts are worth watching. Persisting the forecast is a possible follow-up.
- **Deployment writes nothing to hardware.** Charge control boots in mode `Off` and takes control
  only when Home Assistant selects a mode; `BatteryHold` stays disabled and dry-run. The compose file
  passes those settings explicitly so the safety posture is visible rather than implied.
- **The session store (#32) gets the `data/` bind mount its own record asked for.** `SessionStore:Path`
  resolves against the content root, so `data/sessions.db` lands in `/app/data` and is mounted from
  `/opt/solax/data`. Two consequences specific to SQLite: the *directory* must be writable by uid 1654
  because the `-wal` and `-shm` files live beside the database, and a backup stops the controller
  first, since WAL makes a hot copy unlikely to tear rather than unable to. `deploy.sh` prepares `data/`
  as it does `logs/` — without a mount the app happily writes into the
  container and the history dies with it, which is the one loss here that cannot be undone by
  re-polling.

---

## 2026-07-29 — A failed Modbus exchange invalidates the connection

**Context.** Issue #24: after roughly fifteen minutes of normal operation the service began failing
every single poll with `Response was not of expected transaction ID. Expected 2426, received 2424`,
and never recovered — 45 further minutes produced zero successful polls. Earlier logs show the same
failure in smaller doses going back a week.

**What was found.** A Modbus TCP response that arrives after its request has given up stays in the
socket's receive buffer. The next request reads *that* reply, and every request after it is
permanently one or more responses behind. NModbus retries a mismatch by re-sending, which heals a
one-off glitch — but not this, because every subsequent response is offset too, so the retries are
exhausted and the read throws.

The connection is not the problem. Throughout all of it the TCP socket is open and healthy, so
`ModbusTcpClient.IsConnected` returns true and the callers' `if (!IsConnected) ConnectAsync()` guard
never fires. The poll loop dutifully catches the exception, logs it, and retries on the same poisoned
stream, forever. Only restarting the process cleared it.

**Decision.** Any failed exchange invalidates the connection: the master and the `TcpClient` are
disposed and nulled, so the next call reconnects with a fresh stream and a fresh transaction counter.
This is done in an exception filter, so the original NModbus exception still reaches the caller and
the logs unchanged.

Three supporting changes:

- **Connect on demand.** Operations no longer throw "not connected"; they connect if needed. The
  callers' explicit guards still work but are no longer load-bearing.
- **One request at a time,** via a `SemaphoreSlim`. Requests are sequential today, but a reconnect can
  now happen mid-call, and two requests sharing a stream is another route to the same desync.
- **A minimum gap between requests** (`DeviceConfig.MinRequestInterval`). The SolaX protocol documents
  a second between instructions — a constraint noted in `InverterRegisterMap`'s own comment and then
  honoured nowhere. The poll loop issues about five requests per five-second cycle, four of them to
  the charger, which is where the failure was observed.

**Why 250 ms and not the documented second.** A full second across four charger requests would consume
most of a five-second poll. Recovery no longer depends on the spacing, so this value only affects how
often the device is pushed into a state that needs recovering; it is per-device configuration, and
raising it to `00:00:01` is the first thing to try if desyncs persist on other hardware.

**Consequences.**

- A transient glitch now costs one request instead of the process. The poll loop's existing
  catch-log-retry becomes sufficient rather than futile.
- Reconnection is invisible to callers, so a burst of failures shows up as a few warnings rather than
  an outage — worth remembering when reading logs: absence of errors no longer proves the link was
  never disturbed.
- Testing this needed a real socket. `FakeModbusTcpServer` speaks just enough MBAP to answer reads and
  writes and, on demand, to answer them with the wrong transaction id. Verified as a genuine
  regression test: with the invalidation disabled, three of the nine tests fail.

---

## 2026-07-27 — The forecast plans by power band, not by daily energy; the car absorbs any shortfall

**Context.** Issue #22 adds a third charge mode, `Forecasted`, driven by the Solcast forecast, with
one hard requirement: the home battery must be at 100 % by evening, while the car takes as much solar
as it can and neither pack is degraded unnecessarily.

**Decision 1 — plan in power bands, not in kilowatt-hours.** The obvious formulation,
`EvBudget = forecast − house − battery`, is wrong on this hardware: it treats energy as fungible when
the two consumers cannot accept the same power. The EV charger's floor is 6 A, which on three phases
(and the X1/X3-HAC has no phase switching) is **~4.2 kW**; the home battery accepts anything down to a
few hundred watts. So the day is split into *shoulder* production (below the floor — battery only) and
*plateau* production (at or above it — the only time the car can charge), and the battery's need is
booked against the shoulders first. Only what the shoulders cannot cover is claimed from the plateau.
A budget expressed purely in energy would happily promise the car 3 kWh on a day whose surplus never
once clears 4.2 kW.

**Decision 2 — book the battery backwards from the deadline.** The booking walks the remaining
forecast from `FullByTime` backwards, reserving the latest production first. That is what "100 % by
evening, not by lunchtime" means, and it hands the car the *earliest* feasible plateau — when it is
most likely to be plugged in, and when a forecast error still has the rest of the day to correct
itself. Recomputed every poll, so a collapsing afternoon simply grows the reservation next cycle.

**Decision 3 — the SOC floor counts all remaining surplus, not the booking.** An earlier draft derived
the floor from the energy booked for the battery. That is degenerate: the booking is sized to the need
*at the current SOC*, so the floor came out equal to the current SOC — "you may never discharge". The
floor answers a different question ("how far may SOC fall and still recover by the deadline?"), and a
deeper discharge simply grows a need the battery outranks the car to satisfy. It therefore counts
every remaining watt of surplus, clamped by `MinBatterySocFloorPercent`.

**Decision 4 — plan on p10, and measure the forecast against reality.** Planning a guarantee against
the median means missing it about half the time, so the plan uses Solcast's `pv_estimate10`
(`pv_estimate` was the only band parsed before). On top of that a realised bias (`actual ÷ forecast`
for elapsed daylight) scales the remaining forecast, clamped asymmetrically to `[0.5, 1.2]`:
under-production must be able to throttle the car, but a sunny morning must not be able to
over-commit the afternoon. A sustained breach of `[0.6, 1.4]` abandons the plan for the day. The
forecast refresh drops from 12 h to 3 h, skipped overnight — a 12-hour-old forecast cannot steer a
deadline, and a fresh one at 02:00 cannot change any decision.

**Decision 5 — the loan bridges a surplus; it never funds a session.** The battery may lend the
difference between a real surplus and the 6 A floor, repaid later from sun that would otherwise be
exported. It is refused below `MinBridgeSurplusWatts` (2 kW), on any shortfall day, once
`MaxDailyLoanKWh` is spent, and near the floor. Lending 4.2 kW into no sun would be a battery-to-car
transfer: a round trip and a cycle on both packs, buying nothing. Enforcement is not left to the
arithmetic — at the floor the #20 discharge hold is armed automatically, so the grid covers an
estimate error rather than the pack.

**Decision 6 — on a shortfall the car gives way, and we report rather than act.** Priority is fixed:
house → battery to 100 % → EV. Chosen deliberately over the alternatives (grid top-up to a daily
minimum; letting the battery finish below 100 %) because the owner's requirement is the evening 100 %,
and because grid-charging an EV is a decision worth making deliberately rather than automatically.
**No code path initiates grid charging.** What the controller owes the owner instead is early warning:
`Day outlook`, `Projected shortfall` and `EV energy expected today` are published as soon as the day
can be judged, so the decision — drive less, charge elsewhere, plug in on a night tariff — stays with
a person.

Consequences, deliberately accepted:

- **Two new stateful pieces in the worker** (`DayPlanProvider`, and the session/loan integrators in
  `ChargingControlCoordinator`). Nothing is persisted, so a restart loses today's accumulated
  energies and the bias resets to 1.0 — consistent with the rest of the service, and self-correcting
  within a few forecast periods.
- **The house baseline is a single rolling mean, not a per-hour profile.** A learned profile that
  resets on every deploy would be worse than an honest average.
- **The dwell timers can briefly import.** Holding a session at 6 A through a surplus dip for up to
  `MinRunTime` may pull from the grid; the alternative is contactor cycling and vehicle wake cycles on
  every passing cloud.
- **`Forecasted` degrades to `Solar`, never to something more permissive.** Missing forecast, stale
  forecast and broken trust all take the same path.

---

## 2026-07-26 — Battery discharge hold uses computed Power Control, not a device "No Discharge" mode

**Context.** Issue #20 asks for a switch that stops the home battery discharging, so an EV charges
from PV and grid but never from the battery, while the battery is still free to charge from PV
surplus. The issue proposed writing `power_control = Enabled No Discharge` to the Modbus Power
Control block at holding register `0x7C`, treating it as a fire-and-forget command: one write to arm
it for 8 hours, one write to release it, and nothing in between.

**What we found.** Desk verification against the upstream
[`plugin_solax.py`](https://github.com/wills106/homeassistant-solax-modbus/blob/main/custom_components/solax_modbus/plugin_solax.py)
map — the source the issue itself cites — contradicts that design in three places.

1. **`Enabled No Discharge` is not a device-level mode.** The `remotecontrol_power_control` entity is
   declared `WRITE_DATA_LOCAL`, meaning its option values (`11`, `12`, `110`, `120`, `130`) never
   reach the inverter — they are identifiers for client-side strategies. The device enum is only
   `0 = Disabled` and `1 = Enabled Power Control` (upstream lists `2 = Quantity Control` and
   `3 = SOC Target Control`, both commented out). Mode 8/9 at `0xA0` tells the same story: its
   `85: "Enabled No Discharge"` also resolves to a real device value of `8`.

2. **`active_power` is the mechanism, not an ignored field.**
   `autorepeat_function_remotecontrol_recompute` translates `Enabled No Discharge` into
   `power_control = Enabled Power Control` with `active_power = -min(house_load, pv_power)`. Because
   that target is derived from live house load and PV, it must be recomputed and rewritten
   continuously. A single 8-hour arming cannot express it.

3. **The block cannot be read back.** Holding register `0x7C` is overloaded: upstream *writes* the
   power-control command there but *reads* it as the inverter's ARM firmware version
   (`async_read_holding_registers(address=0x7B, count=2)`). No register exposes the active
   remote-control state — upstream tracks it with client-side timers only.

**Decision.** Implement the hold the way the hardware actually supports it: write
`power_control = Enabled Power Control` with `active_power = -min(house load, PV)`, recomputed each
poll from telemetry, and reissued when the target moves past a threshold or the armed command nears
expiry. This preserves both halves of the requirement — the battery is never asked to serve load
(the inverter is only ever told to push out power it is already generating), and PV beyond the house
load has nowhere to go but the battery, so surplus charging still works.

Consequences, deliberately accepted:

- **The "at most one write per 8 hours" acceptance criterion is dropped.** The write rate is instead
  bounded by `BatteryHold:TargetChangeThresholdWatts` (default 100 W) and the renewal interval. The
  command is not EEPROM-backed — upstream states these may be issued as often as desired — so this
  costs Modbus traffic, not hardware wear.
- **`Duration` is 60 s, not 8 hours.** With per-poll reconciliation a short duration is a *better*
  failsafe: if the service stops, the inverter resumes normal operation within a minute instead of
  within eight hours. Renewal happens at half the duration so a slow poll never leaves a gap. The
  8-hour figure survives only as the hardware ceiling (`u16`, 28,800 s) enforced in the encoder.
- **The Home Assistant switch reports our own armed state, not a device read-back.** The acceptance
  criteria around reading the hold back, surviving a restart by reading device state, and correcting
  a manual change made in the SolaX app are not implementable — and the last is moot anyway, since
  this is a command rather than a stored setting the app could show or alter.
- **Upstream's SOC ≥ 98 % branch is not implemented.** There, the target becomes
  `-pv_power - 150`, deliberately trickle-discharging the battery to keep SOC near 98 % and stop
  older inverters curtailing PV. That contradicts this issue's "battery power is never negative"
  requirement, so it is left out pending observation of whether PV curtailment actually occurs on
  this hardware.

**Why not the alternatives.** Unchanged from the issue, and reinforced by the above:

| Approach | Verdict |
|---|---|
| Computed Power Control (`0x7C`, mode 1) | **Chosen.** The only route that both blocks discharge and preserves PV → battery charging. Not EEPROM-backed. |
| Raise discharge cut-off / min SOC | Rejected. Modifies a stored parameter on a ~100,000-cycle EEPROM, 1 % granular, and drifts as SOC moves. |
| Battery Discharge Max Current = 0 | Rejected. Same EEPROM problem; also fights the inverter's own limits. |
| Manual Mode → "Stop charge and discharge" | Rejected. Freezes the battery in *both* directions, so PV surplus exports instead of charging the battery. Remains the manual fallback if the Modbus route fails verification. |

### Verified on hardware, 2026-07-27

First live write to the inverter. Conditions: dusk, PV ~360 W, SOC 87 %, no EV charging, house load
~1.5–2.9 kW.

**The mechanism works.** Arming the hold moved the house from battery to grid within one poll:

| | Battery | Grid | Solar |
|---|---|---|---|
| Before the write | **−2846 W** (discharging) | 0 W | 366 W |
| After the write | **−56 W** | **+1601 W** (importing) | 370 W |

Confirmed by this run:

- **`power_control = 1` with a computed `active_power` is accepted** and takes effect immediately —
  no Modbus exception, no rejected block. The encoded payload
  `[1,1,65170,65535,0,0,60,0,0,0,0,0,0]` at `0x7C` is correct as written.
- **Renewal at half the duration works.** Renewals were issued at ~33 s and ~72 s with the hold
  remaining continuously effective; no lapse or gap was observed between them.
- **PV was not curtailed.** Solar held steady at 358–370 W across the whole run, before, during and
  after arming. Weak evidence at 360 W in the evening — this needs repeating under strong midday sun
  before the curtailment risk can be closed.

**A working hold still leaves a residual 50–65 W trickle out of the battery.** This is inverter
standby draw, not load being served — it persisted regardless of house load swinging between 143 W
and 2877 W. Two consequences:

- Issue #20's acceptance criterion "`BatteryPowerWatts` is never negative" is **not literally
  achievable** on this hardware. The achievable guarantee is that the battery stops serving house
  load, which is what the feature is actually for.
- The "hold armed but battery discharging" warning originally triggered on any negative value, so it
  fired every single poll and drowned out the signal it existed to give. It now uses a 150 W deadband.

**Still to observe:** behaviour under strong PV (does the battery still charge from surplus while
held, and is PV curtailed at full output), behaviour with the EV actually charging, and what the
undocumented `timeout` field does relative to `duration`.

`BatteryHold:Enabled` remains off by default and `DryRun` still defaults to `true`, since none of the
above has been observed on any other firmware.
