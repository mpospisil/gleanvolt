# Charging session and energy history

## Charging session history (the `SessionStore` section)

Everything above is *live*: the log line scrolls past and the Home Assistant entity is overwritten on
the next poll. This section is what survives — every controlled charging session is recorded to a local
SQLite file, so "how did last Tuesday's `Forecasted` session actually go, and did the plan hold?"
becomes a question with an answer.

It **only ever observes.** It reads no register the poll loop wasn't already reading and writes to no
device, so unlike `ChargeControl` and `BatteryHold` it is **on by default**.

```jsonc
"SessionStore": {
  "Enabled": true,
  "Path": "data/sessions.db",         // relative to the content root
  "SampleInterval": "00:00:30",       // how often a row is stored; changes force one anyway
  "FlushInterval": "00:01:00",        // how long rows may sit in memory before being committed
  "RetentionDays": 365,               // closed sessions older than this are pruned at startup
  "RecordUncontrolledSessions": false // also record a plugged-in car no mode is driving
}
```

### What a session is

A session **opens** when a controlling mode (`Solar`, `Forecasted`, `FastNoBattery`, `Targeted`,
`SolarGrid`) is driving a connected car, and **closes** when that stops being true. Its end reason says
how:

| End reason | What happened |
|---|---|
| `ModeOff` | Somebody switched the mode off. |
| `CarUnplugged` | The car was unplugged. |
| `ServiceStopped` | The service shut down with the session open. |
| `Interrupted` | The service crashed or lost power; see below. |
| `SessionComplete` | The mode ended itself because the car was full: the wallbox closed the session straight after the car drew, or the car stopped drawing on its own limit. |
| `TargetReached` | `Targeted`, or `FastNoBattery` with an amount, delivered what was asked for. |
| `DeparturePassed` | `Targeted`, or `FastNoBattery` with a departure, ran out of time. |
| `NoSunLeftToday` | `SolarGrid`, `Solar` or `Forecasted` ended for the day, however full the car is. Never `Forecasted` with **Until full** on, which stands down for the night instead. |
| `ChargerTakenOver` | The charger left Fast, so the mode stopped driving it: its owner took it. |

Sessions recorded before these reasons existed show `SessionComplete` for every session a mode ended
itself.

Switching mode mid-session does **not** start a new one. "Forecasted all afternoon, then
`FastNoBattery` at 17:00" is one story about one car, and it is recorded as one session with a
`ModeChanged` event in it.

Note this is a *different* span from the one `Session energy` reports in Home Assistant, which counts
from the moment the car was plugged in whether or not anything is controlling it.

### What is recorded

| | |
|---|---|
| **Session header** | start/end time (UTC, plus the IANA zone so a viewer can bucket by local day), start and end mode, why it ended, start/end SOC, peak power, the totals below, the forecast day plan as it stood at the start, **the whole day's forecast curve**, and **the weather at each end of the session** (both below) |
| **Totals** | energy delivered, split into **from solar / from grid / from battery**, plus what the forecast mode *commanded* the battery to lend |
| **Samples** | every 30 s and on every change: all meters, the four charging figures below, the smoothed surplus, the loan, the hold, the forecast power at that instant, plan figures, the running totals, and the site-wide progress figures below |
| **Events** | mode changed, charging started/paused, setpoint changed, hold armed/released, plan fell out of trust, session ended — each with the controller's own reason string |

### The four charging figures, and why they are kept apart

| Field | What it is |
|---|---|
| `EvChargerPowerWatts` | **Measured** power the charger is drawing — the ground truth, and the basis of every energy total |
| `EvChargingCurrentAmps` | The **actual** current, derived phase-aware from that power |
| `ActiveCurrentAmps` | The charger's setpoint **read back from the device** (null where the register isn't readable) |
| `TargetCurrentAmps` | What the controller **decided** to command |

The gaps are the diagnostic. **Target ≠ active** means the write didn't land, or was suppressed by
`CurrentChangeThresholdAmps`. **Active ≠ actual** means the *car* is the limiter — its charge curve,
its taper near full, its on-board charger ceiling — which is what separates "the strategy
under-delivered" from "the car wouldn't take more".

### Progress against the site, and against the car

The totals above are all about the car: what it received, and which source each watt is attributed to.
Three more are integrated over the same window and answer a different question — what the *site* did
while the car was charging — plus the car's own view of itself:

| Field | What it is |
|---|---|
| `SolarWh` | PV **produced on site** since the session opened. Not the same as `FromSolarWh`: the difference is what the house and the home battery took. |
| `ForecastSolarWh` | What the forecast expected the roof to make over that same window. Against `SolarWh` it is the session's own forecast-versus-reality line. `null` — not `0` — while no forecast has been available for any part of the session. |
| `GridImportWh` | Energy the **whole site** imported, export excluded rather than netted off, so a sunny hour can't cancel out an expensive evening. Again distinct from `FromGridWh`, the car's attributed share. |
| `VehicleSocPercent` | The **car's own** battery SOC from the [vehicle feed](VEHICLE.md#where-the-cars-state-of-charge-comes-from), or `null` when none is configured or nothing has arrived. Advisory: nothing in charge control reads it. |
| `VehicleChargeTimeRemaining` | How much longer the **car itself** reckons it needs, from `charge_time_remaining_minutes` (stored in minutes). **Zero when the car did not report it** — not an error, just a feed that doesn't publish it. Because `0` is also a legitimate reading ("done"), never read this without the flag below. |
| `VehicleChargeTimeRemainingReported` | Whether that zero is the car's answer or the absence of one. `false` means no estimate was provided. |
| `VehicleSocCapturedAt` | When the *vehicle* produced that reading. Stored next to it because it routinely lags by hours — a SOC without its capture time cannot be told from a live one, and a chart would silently flatten. A feed that goes quiet mid-session leaves the last reading standing with a visibly ageing capture time rather than blanking the column. |

These arrived in database schema **v2**; a v1 file is migrated in place on startup and its existing
rows keep `0` for the three totals, which is the truth for them — nothing integrated those quantities
at the time. Published documents move to `schemaVersion` 2 for the same reason, purely additively.

### The day the session ran on

`DayForecast` on the header is **the whole local day's forecast curve** — every 30-minute period, with
the p10/p90 bands each one carried, from before the car was plugged in to after it was unplugged. It is
what turns a session into an analysable one: `ForecastRemainingAtStartWh` says how much sun was left in
one number, and this is the shape behind it. A 4 kWh session reads very differently against a day
forecast at 6 kWh than against one forecast at 30.

Two things about how it is captured are worth knowing:

- **Solcast only ever forecasts forward.** Each refresh returns the periods still to come and nothing
  behind them, so by mid-afternoon the live forecast can no longer say what the morning was predicted
  to bring. The controller therefore **retains** every period a refresh has carried (7 days,
  in memory) and serves the day from that. What survives for a given period is the *last* prediction
  made about it — the closest thing to a nowcast the provider ever gave.
- **A day the controller wasn't running for all of is short by that much.** Restart at 14:00 and the
  morning is simply not there: nothing re-fetches the past. The curve is `null` rather than empty when
  nothing at all is held, because an empty curve sums to zero and would read as a day the sun never
  came up.

It is written when the session opens and rewritten when it closes, so what ends up stored is the
fullest version of the day known by then. Nothing in charge control reads it — the plan, not this,
drives decisions — and the [session detail page](WEB_UI.md#sessions) shows the day's
total with its band beside the other header facts.

This is database schema **v3** and `schemaVersion` 3, additive again: older rows keep `NULL`, which is
the truth for them.

### The weather a session ran in

The day forecast above says what the sun was *expected* to do. This says what the sky **did** — because a session that under-delivered against a confident forecast otherwise has no explanation attached to it, and cloud that rolled in, a cold morning and snow on the panels all look identical afterwards.

Configured via the [`Weather` section](CONFIGURATION.md#weather-the-weather-section-optional); `NULL` throughout when it isn't, which is the default.

| Field | What it is |
|---|---|
| `WeatherAtStart` | The conditions when the session opened |
| `WeatherAtEnd` | The conditions when it closed. `null` while a session is open, and when that fetch failed — neither is an error |
| `Sunrise` / `Sunset` | The daylight bounds of the day it **started** on |

Each reading carries `TemperatureCelsius`, `PressureHpa`, `HumidityPercent`, `CloudsPercent`, `VisibilityMetres` (`null` when unreported), `Condition` (`Clear`, `Rain`, `Snow`…), `ConditionDescription` (`light rain`) and `ObservedAt` — the time the *provider* stamped the reading, since these are typically a few minutes old and a reading without its own timestamp can't be told from a live one.

**Two readings, not one.** A six-hour session can finish in entirely different weather from the one it started in, and only the *pair* can say so — the same reason the header has both a start and an end SOC. **Sunrise and sunset are stored once**, because they belong to the day rather than to either reading; a session running across midnight carries the starting day's pair, the same rule `StartedAt` and the day forecast already follow.

These are **columns, not a JSON document** — unlike the forecast curve, which is written once and read whole. Cloud cover and temperature are the axes the analysis groups by, and "solar share against cloud cover" should be SQL rather than a parse per row.

This is database schema **v4** and `schemaVersion` 4, additive: older rows keep `NULL`.

### How the energy is attributed to a source

Nothing measures which electron went where; power at the busbar is fungible. The split is an
*attribution* with a fixed, documented rule (`ChargingSourceAttribution`), and its one hard guarantee
is that the three shares always add back up to the measured draw:

1. PV serves the rest of the house first; the car may take up to the **surplus** that is left — the
   same definition the `Solar` and `Forecasted` modes decide on, so the recorded solar share is
   measured against exactly what the controller was aiming at.
2. Draw beyond the surplus is credited to the **battery**, up to what it is actually discharging —
   battery before grid, because that is the inverter's own priority.
3. Whatever is still unaccounted for is **grid** import.

Measured `FromBatteryWh` is kept separate from the commanded `LoanedWh` on purpose: they answer
different questions, and the gap between them is worth being able to see.

### Operating the session history

- **Back up `data/sessions.db`.** It is the only history that exists; nothing reconstructs it.
- **Sampling is coarser than polling on purpose.** Every poll still feeds the running energy totals —
  only the stored rows are thinned, so a four-hour session is a few hundred rows rather than ~2,900,
  and no transition is blurred because any change forces a sample.
- **A crash leaves nothing dangling.** A session still open at the next startup is closed at its last
  recorded sample and marked `Interrupted`, which is why every sample carries the running totals.
- **Failure is contained.** If the file can't be opened — a read-only mount, a bad permission —
  recording is disabled for the run with an error in the log, and polling, charge control and the Home
  Assistant integration carry on untouched.
- **Nothing is published to Home Assistant.** No new entities; this feature is history, not telemetry.

### Getting the data out

A closed session is immutable, which makes it safe to publish as one self-contained document —
`ChargingSessionDocument`, carrying a `schemaVersion`, the header, every sample and every event. That
shape is deliberately independent of the database's own tables, so uploading sessions to cloud object
storage (one object per session) can be added later without migrating anything. The
`sessions.synced_at` column is reserved for exactly that. Reading them from a web app is already
built — see [Browsing charging session history](WEB_UI.md#sessions) above.

## Energy history (the `EnergyMonitor` section)

The session store answers questions about *charges*. This one answers questions about the **site**,
and it runs whether or not anything is plugged in — because most of the year has no session in it, and
"how much did the roof make last March?" or "how well did the forecast track reality across the
autumn?" are not questions session data can be made to answer.

It is a **separate service with its own tables in its own database file**, and it does nothing but
record. Like the session store it only ever observes — it reads no register the poll loop wasn't
already reading and writes to no device — so it too is **on by default**.

```jsonc
"EnergyMonitor": {
  "Enabled": true,
  "Path": "data/energy.db",   // its own file, beside sessions.db — see below
  "Interval": "00:15:00",     // one row per quarter hour; must divide 24 h evenly
  "MaxGap": "00:05:00",       // longer silences are lost time, not a steady reading
  "RetentionDays": 0          // 0 = keep everything, which is the point of this table
}
```

### One row per quarter hour

Each row is a **bucket**, not a sample: it covers a fixed window and its figures belong to that window
alone, so summing a day's buckets gives the day exactly. Buckets align to the interval measured from
the UTC epoch, so they land on :00, :15, :30 and :45 whatever time the service started, and a
daylight-saving change cannot produce a 45- or 75-minute one.

Everything is in **kWh** — the one place in this codebase that isn't in watt-hours, deliberately: this
is the analytics surface, read by spreadsheets and notebooks rather than by control code.

| Column | Meaning |
|---|---|
| `period_start` / `period_end` | The window, UTC. `period_start` is the primary key. |
| `time_zone_id`, `local_date` | The IANA zone, and the local calendar day — so "group by day" is a column, not timezone maths in every query. |
| `solar_kwh` | PV produced on site. |
| `forecast_solar_kwh` | What the forecast expected over the same window. **NULL, not 0**, when no forecast covered it. |
| `grid_import_kwh`, `grid_export_kwh` | Each direction separately. |
| `ev_kwh` | Energy delivered to the car, measured at the charger. |
| `battery_charge_kwh`, `battery_discharge_kwh` | The home battery, each direction separately. |
| `soc_start_percent`, `soc_end_percent` | Home battery SOC at each end of the window. Consecutive buckets join up: one bucket's end SOC is the next one's start. |
| `soc_min_percent`, `soc_max_percent` | The range covered inside the window. |
| `soc_mean_percent` | SOC averaged over **time**, not over samples — a reading that stood for ten minutes counts ten times one that stood for one. |
| `covered_seconds` | How much of the window was actually observed. **Read this first**; see below. |
| `sample_count` | How many poll snapshots fed the window. Diagnostic only. |

### Nothing is netted, so the balance closes

Import and export are separate columns, and so are battery charge and discharge. A quarter hour that
exported 0.4 kWh and imported 0.4 kWh is not the same event as one that did neither, and a net of zero
cannot tell them apart afterwards.

Keeping them apart is also what makes the row *balance*, so house consumption needs no column of its
own — it is the residual:

```
house load = solar + import - export - battery charge + battery discharge
other loads = house load - ev
```

### `covered_seconds` is not optional reading

Energy is integrated by holding each power reading until the next one arrives, which is how the poll
loop observes the hardware. Beyond `MaxGap` that stops being true: the service was restarting, or the
inverter was unreachable, and holding a reading across the gap would invent energy nobody measured.
So the open bucket is **closed short** and a fresh one opens when polling resumes.

That is what leaves `covered_seconds` below the full interval, and why it is a stored column rather
than an assumption. **A row below full coverage is short on every energy figure by the same fraction**
— without checking it, a restart at 09:07 reads as "the sun went out for seven minutes".

A *planned* stop loses nothing: the worker writes the part-finished bucket on shutdown, and an append
for a window that already exists **adds to it rather than replacing it**. The stopping process
contributes the minutes it saw, the starting one contributes the rest, and the row ends up whole with
`covered_seconds` back at the full interval.

### Why its own database file

The two stores have separate writers, separate retention and separate reasons to fail, and
`SqliteChargingSessionStore` serialises its writes on an in-process lock — a second writer holding a
*different* lock over the same file would turn that arrangement back into the `SQLITE_BUSY` retry loop
it exists to avoid.

Nothing is lost by the split. An analysis that wants both opens one and attaches the other:

```sql
ATTACH DATABASE 'sessions.db' AS s;

-- Every quarter hour of a session, against what the whole site was doing at the time
SELECT e.period_start, e.solar_kwh, e.forecast_solar_kwh, e.ev_kwh, e.soc_mean_percent
FROM energy_intervals e
JOIN s.sessions ss ON e.period_start >= ss.started_at AND e.period_start < ss.ended_at
WHERE ss.id = '…';
```

Some queries the table is shaped for:

```sql
-- Daily production against forecast, and what the car's share of it was
SELECT local_date,
       ROUND(SUM(solar_kwh), 2)          AS solar,
       ROUND(SUM(forecast_solar_kwh), 2) AS forecast,
       ROUND(SUM(ev_kwh), 2)             AS to_car,
       ROUND(SUM(grid_import_kwh), 2)    AS imported,
       ROUND(SUM(grid_export_kwh), 2)    AS exported
FROM energy_intervals
WHERE covered_seconds >= 890            -- full 15-minute buckets only
GROUP BY local_date
ORDER BY local_date DESC;

-- The average shape of a day, by time of day
SELECT strftime('%H:%M', period_start) AS utc_slot,
       ROUND(AVG(solar_kwh), 3) AS avg_solar,
       ROUND(AVG(soc_mean_percent), 1) AS avg_soc
FROM energy_intervals
GROUP BY utc_slot ORDER BY utc_slot;
```

### Operating the energy history

- **Back up `data/energy.db` too.** Like the session store it is the only copy that exists, and
  nothing reconstructs it. Both files sit under the same `./data` bind mount in `deploy/`.
- **It is cheap.** 96 rows a day, ~35,000 a year, a few megabytes — one write per quarter hour, which
  is why this store needs none of the batching the session recorder does.
- **`RetentionDays` defaults to 0 (keep everything)**, the opposite of the session store's 365. This
  table exists to be looked at years later, and at 15-minute resolution a decade of it is still a file
  you could email.
- **An interval that can't tile a day is corrected, not fatal.** Anything that doesn't divide 24 hours
  evenly would leave a short stub bucket at midnight; the service logs a warning and records at 15
  minutes instead.
- **Failure is contained.** If the file can't be opened, this feature alone is disabled for the run
  with an error in the log — polling, charge control, session recording and Home Assistant carry on
  untouched.
- **Nothing is published to Home Assistant.** No new entities; this feature is history, not telemetry.
- **A viewer is built in.** The web UI's `/energy` page shows a day at a time, drawn as a chart and
  printed as rows — see [Browsing the energy history](WEB_UI.md#energy) above.
