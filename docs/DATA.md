# Charging session and energy history

Two local SQLite databases keep what the live figures can't:

- **`data/sessions.db`** — every controlled charging session: how it ran, where its energy came from,
  and the forecast and weather it ran under. Browse it on the web UI's [**Sessions**](WEB_UI.md#sessions)
  page.
- **`data/energy.db`** — the whole site in 15-minute rows, whether or not a car is plugged in. Browse it
  on the [**Energy**](WEB_UI.md#energy) page.

Both only observe — they write to no device — so both are **on by default**. Neither is published to
Home Assistant. **Back both up**: nothing can reconstruct them.

## Charging session history (the `SessionStore` section)

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

Switching mode mid-session does **not** start a new one: it is recorded as a `ModeChanged` event.

This is a different span from Home Assistant's `Session energy`, which counts from plug-in whether or
not a mode is driving.

### What is recorded

| | |
|---|---|
| **Session header** | start/end time (UTC, plus the IANA zone so a viewer can bucket by local day), start and end mode, why it ended, start/end SOC, peak power, the totals below, the forecast day plan as it stood at the start, **the whole day's forecast curve**, and **the weather at each end of the session** (both below) |
| **Totals** | energy delivered, split into **from solar / from grid / from battery**, plus what the forecast mode *commanded* the battery to lend |
| **Samples** | every 30 s and on every change: all meters, the four charging figures below, the smoothed surplus, the loan, the hold, the forecast power at that instant, plan figures, the running totals, and the site-wide progress figures below |
| **Events** | mode changed, charging started/paused, setpoint changed, hold armed/released, plan fell out of trust, session ended — each with the controller's own reason string |

### The four charging figures

| Field | What it is |
|---|---|
| `EvChargerPowerWatts` | **Measured** power the charger is drawing — the ground truth, and the basis of every energy total |
| `EvChargingCurrentAmps` | The **actual** current, derived phase-aware from that power |
| `ActiveCurrentAmps` | The charger's setpoint **read back from the device** (null where the register isn't readable) |
| `TargetCurrentAmps` | What the controller **decided** to command |

The gaps are the diagnostic. **Target ≠ active** means the write didn't land, or was suppressed by
`CurrentChangeThresholdAmps`. **Active ≠ actual** means the *car* is the limiter — its charge curve,
its taper near full, its on-board charger ceiling.

### Progress against the site, and against the car

Beside the car's totals, what the *site* did over the same window, and the car's own view of itself:

| Field | What it is |
|---|---|
| `SolarWh` | PV **produced on site** since the session opened. Not the same as `FromSolarWh`: the difference is what the house and the home battery took. |
| `ForecastSolarWh` | What the forecast expected the roof to make over that same window. Against `SolarWh` it is the session's own forecast-versus-reality line. `null` — not `0` — while no forecast has been available for any part of the session. |
| `GridImportWh` | Energy the **whole site** imported, export excluded rather than netted off, so a sunny hour can't cancel out an expensive evening. Again distinct from `FromGridWh`, the car's attributed share. |
| `VehicleSocPercent` | The **car's own** battery SOC from the [vehicle feed](VEHICLE.md#where-the-cars-state-of-charge-comes-from), or `null` when none is configured or nothing has arrived. Advisory: nothing in charge control reads it. |
| `VehicleChargeTimeRemaining` | How much longer the **car itself** reckons it needs, from `charge_time_remaining_minutes` (stored in minutes). **Zero when the car did not report it** — not an error, just a feed that doesn't publish it. Because `0` is also a legitimate reading ("done"), never read this without the flag below. |
| `VehicleChargeTimeRemainingReported` | Whether that zero is the car's answer or the absence of one. `false` means no estimate was provided. |
| `VehicleSocCapturedAt` | When the *vehicle* produced that reading, which often lags by hours. A feed that goes quiet mid-session leaves the last reading with an ageing capture time. |

### The day the session ran on

`DayForecast` on the header is **the whole local day's forecast curve** — every 30-minute period with
its p10/p90 band. `ForecastRemainingAtStartWh` says in one number how much sun was left; this is the
shape behind it.

- **What is kept for a period is the last forecast made for it.** Solcast only forecasts forward, so
  the controller keeps every period it has fetched (7 days, in memory) and builds the day from that.
- **A day the controller wasn't running for all of is short by that much.** Restart at 14:00 and the
  morning is missing. The curve is `null`, not empty, when nothing at all is held.

It is written when the session opens and again when it closes. Nothing in charge control reads it.

### The weather a session ran in

What the sky **did**, beside what the forecast expected. Needs the
[`Weather` section](CONFIGURATION.md#weather-the-weather-section-optional); `NULL` throughout without it.

| Field | What it is |
|---|---|
| `WeatherAtStart` | The conditions when the session opened |
| `WeatherAtEnd` | The conditions when it closed. `null` while a session is open, and when that fetch failed — neither is an error |
| `Sunrise` / `Sunset` | The daylight bounds of the day it **started** on |

Each reading carries `TemperatureCelsius`, `PressureHpa`, `HumidityPercent`, `CloudsPercent`, `VisibilityMetres` (`null` when unreported), `Condition` (`Clear`, `Rain`, `Snow`…), `ConditionDescription` (`light rain`) and `ObservedAt` — the time the *provider* stamped the reading, since these are typically a few minutes old and a reading without its own timestamp can't be told from a live one.

Sunrise and sunset belong to the day the session **started** on. These are plain columns, so they can
be grouped by in SQL.

### How the energy is attributed to a source

Nothing measures which electron went where; power at the busbar is fungible. The split is an
*attribution* with a fixed rule, and its one hard guarantee
is that the three shares always add back up to the measured draw:

1. PV serves the rest of the house first; the car may take up to the **surplus** that is left — the
   same definition the `Solar` and `Forecasted` modes decide on, so the recorded solar share is
   measured against exactly what the controller was aiming at.
2. Draw beyond the surplus is credited to the **battery**, up to what it is actually discharging —
   battery before grid, because that is the inverter's own priority.
3. Whatever is still unaccounted for is **grid** import.

Measured `FromBatteryWh` is kept separate from the commanded `LoanedWh`, which is what the `Forecasted`
mode *told* the battery to lend.

### Operating the session history

- **Sampling is coarser than polling.** Every poll feeds the running totals; only the stored rows are
  thinned (every `SampleInterval`, and on any change).
- **A crash leaves nothing dangling.** A session still open at the next startup is closed at its last
  recorded sample and marked `Interrupted`, which is why every sample carries the running totals.
- **Failure is contained.** If the file can't be opened — a read-only mount, a bad permission —
  recording is disabled for the run with an error in the log, and polling, charge control and the Home
  Assistant integration carry on untouched.

### Getting the data out

A closed session never changes. The [HTTP API](HTTP_API.md)'s `GET /sessions/{id}` returns one in
full: the header, every sample and every event. For analysis, open `sessions.db` directly — see
[Querying both databases](#querying-both-databases).

## Energy history (the `EnergyMonitor` section)

The **site**, whether or not anything is plugged in: "how much did the roof make last March?", "how
well did the forecast track reality across the autumn?".

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

Everything is in **kWh**.

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

Import and export are separate columns, and so are battery charge and discharge, so each row
balances and house consumption is the residual:

```
house load = solar + import - export - battery charge + battery discharge
other loads = house load - ev
```

### `covered_seconds` is not optional reading

A silence longer than `MaxGap` — a restart, an unreachable inverter — is not filled in: the window is
closed short. **A row below full coverage is short on every energy figure by the same fraction**, so
without checking `covered_seconds` a restart at 09:07 reads as "the sun went out for seven minutes".

A planned stop loses nothing: the part-finished window is written on shutdown and completed by the next
start.

### Querying both databases

Attach one to the other:

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

- **It is small.** 96 rows a day, about 35,000 a year — a few megabytes.
- **`RetentionDays` defaults to 0, keep everything** (the session store's is 365).
- **An interval that can't tile a day is corrected, not fatal.** Anything that doesn't divide 24 hours
  evenly would leave a short stub bucket at midnight; the service logs a warning and records at 15
  minutes instead.
- **Failure is contained.** If the file can't be opened, this feature alone is disabled for the run
  with an error in the log; everything else carries on.
