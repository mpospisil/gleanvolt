# SolaX Local Controller

A standalone, locally hosted background service for managing and monitoring a **SolaX X3-HYB-G4 PRO** hybrid inverter and a **SolaX X1/X3-HAC** EV charger.

The controller operates entirely within the local LAN via **Modbus TCP**, bypassing cloud dependencies to ensure continuous operation, instantaneous polling, and strict local data ownership. It polls real-time data (PV generation, battery SOC, grid power flow) and applies automated decision-making logic to optimize EV charging and battery utilization based on household energy surpluses.

## Status

Working, and running against live hardware. Polling, Solcast forecasting, live-solar EV charge
control, the battery discharge hold, and the Home Assistant integration are all implemented.

The two features that **write** to hardware — `ChargeControl` (the EV charger) and `BatteryHold` (the
inverter) — ship disabled by default, and `BatteryHold` additionally defaults to dry-run. Register
addresses vary between SolaX generations and firmware, so verify them against your own device before
enabling either.

## Why local?

Cloud-based SolaX monitoring/control (SolaX Cloud, third-party integrations) introduces latency, external dependencies, and data collection outside the user's control. This project talks directly to the inverter and EV charger over Modbus TCP on the local network, so:

- Control logic keeps working during internet outages.
- Polling and decision cycles run at LAN speed, not cloud round-trip speed.
- No telemetry leaves the local network unless explicitly configured.

## Key features

- **Real-time polling** of PV generation, battery state of charge, grid import/export, and EV charger status over Modbus TCP.
- **Surplus-aware EV charging** — automatically ramp EV charge current up/down based on available household energy surplus.
- **Battery discharge hold** — stop the home battery serving house load, so the EV charges from PV and grid while the battery still charges from surplus.
- **Fast charge without the battery** — one mode for "I leave in an hour": maximum current from PV and grid, the home battery held out of it, and back to `Off` by itself when the car is full.
- **Solar forecasting** — a cached [Solcast](https://solcast.com/) forecast for the site, logged against actual generation.
- **Home Assistant integration** over MQTT discovery, with runtime control and telemetry.
- **Background service** — runs unattended as a long-lived process (e.g. systemd service / Windows Service).
- **Local data ownership** — no cloud dependency for core operation.

## Hardware targets

| Device | Model | Interface |
|---|---|---|
| Hybrid inverter | SolaX X3-HYB-G4 PRO | Modbus TCP |
| EV charger | SolaX X1/X3-HAC | Modbus TCP |
| Home battery | SolaX T-BAT H 2.5 modules + BMS (**10 kWh** nominal on the reference install) | via the inverter — no direct connection |

The battery has no interface of its own: everything about it reaches us through the inverter's
registers (SOC from `BatteryCapacity 0x1C`, power from `BatteryPowerCharge1 0x16`) and every command
that affects it goes through the inverter's power-control block. Its **usable** capacity is the one
site-specific number the forecast-driven mode cannot work without — see
[`BatteryCapacityKWh`](#forecast-driven-charging-the-forecasted-mode).

## Tech stack

- [.NET 10](https://dotnet.microsoft.com/) — target framework
- Hosted as a [.NET Worker Service](https://learn.microsoft.com/dotnet/core/extensions/workers) (background service)
- Modbus TCP client for inverter/charger communication

## Project structure

The solution is organized to keep domain/control logic testable and free of hardware and hosting concerns:

```
SolaxLocalController.slnx
├── src/
│   ├── Solax.Core/                 # Domain logic and hardware abstractions
│   │   ├── Models/                 # Strongly typed models (EnergyState, DeviceConfig, ...)
│   │   ├── Enums/                  # Register addresses, charger modes, inverter control values
│   │   ├── Strategies/             # Pure decision logic (charging controller, discharge hold, smoothing)
│   │   └── Interfaces/             # IModbusClient, IChargingController, IBatteryDischargeControl, ...
│   │
│   ├── Solax.Infrastructure/       # External communication
│   │   ├── Modbus/                 # Concrete Modbus TCP client (and a read-only decorator)
│   │   ├── RegisterMaps/           # Hex address mappings for SolaX Gen4 and EV Charger
│   │   └── Solcast/                # Solar-forecast HTTP client
│   │
│   └── Solax.Worker/               # The executable host
│       ├── Program.cs              # Dependency Injection setup
│       ├── SolaxPollingService.cs  # The main background loop (IHostedService)
│       ├── Configuration/          # Options classes bound from appsettings.json
│       └── HomeAssistant/          # MQTT discovery and the HA worker
├── tests/
│   ├── Solax.Core.Tests/           # Unit tests for the control logic (mocking hardware)
│   ├── Solax.Infrastructure.Tests/ # Register encoding and write-path tests
│   └── Solax.Worker.Tests/         # Coordinator, selector and HA discovery tests
└── docs/                           # DECISIONS.md, IMPLEMENTATION_LOG.md (see below)
```

### Layering rules

- **Dependency direction is one-way:** `Solax.Worker` → `Solax.Infrastructure` → `Solax.Core`. `Solax.Core` must never reference `Solax.Infrastructure` or `Solax.Worker`.
- **`Solax.Core` has no hardware or framework dependencies.** No Modbus libraries, no `Microsoft.Extensions.Hosting` types — only plain models, enums, and interfaces (`IModbusClient`, `IChargingController`, `IBatteryDischargeControl`). This is what keeps control/decision logic unit-testable without real hardware.
- **All decision-making logic lives in `Solax.Core`**, expressed against interfaces. Charging strategy, surplus calculations, and SOC-based rules belong here, not in `Solax.Infrastructure` or `Solax.Worker`.
- **`Solax.Infrastructure` only implements `Solax.Core` interfaces.** Modbus TCP details and register maps stay isolated here; no business/decision logic.
- **`Solax.Worker` is composition-only.** `Program.cs` wires up DI; `SolaxPollingService` orchestrates the poll/act loop by calling into `Solax.Core` abstractions — it should not contain control logic itself.
- **`Solax.Core.Tests` mocks the hardware boundary** (`IModbusClient`, etc.) to exercise control logic without a live device.

## Getting started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Network access to the SolaX inverter and EV charger with Modbus TCP enabled

### Build and run

```bash
dotnet build SolaxLocalController.slnx
dotnet test SolaxLocalController.slnx
dotnet run --project src/Solax.Worker
```

Set your device addresses first (see [Configuration](#configuration)). On a first run nothing is
written to either device: the service always boots with the charge mode **Off** and the battery hold
**off**, and `BatteryHold:Enabled` is `false` as well, so it only polls and logs. That is the
recommended way to confirm the telemetry looks right before enabling anything that writes.

## Workflow & Project Management
You are authorized and expected to use the GitHub CLI (`gh`) to manage this project. 
When asked to manage tasks or submit code, use the following commands:
- `gh issue list`: To check current tasks.
- `gh issue view <id>`: To read the requirements of a specific task.
- `gh issue create -t "<title>" -b "<body>"`: To create new tasks.
- `gh pr create -t "<title>" -b "<body>"`: To submit your implemented code for review.
Do not use `git push` directly to the main branch; always create a branch and use `gh pr create`.

## Documentation Organization
All project notes live in the `docs/` directory. You are responsible for keeping them updated:
1. `DECISIONS.md`: Append a record when we adopt a library or establish a core pattern — and when hardware verification contradicts a planned design, which is the more common case here. Include what was found, what was decided, and the consequences accepted.
2. `IMPLEMENTATION_LOG.md`: Before submitting a Pull Request via `gh pr create`, you MUST add a reverse-chronological entry to the top of this file detailing the implementation specifics, hardware quirks encountered (e.g. Modbus limitations), and the files changed. Use this entry to generate a detailed PR body that explains the architecture decisions, not just the changed files.

## Configuration

All settings live in `src/Solax.Worker/appsettings.json`; secrets are supplied out-of-band (see
below). Device addresses and the poll cadence sit in the `Solax` section:

```jsonc
"Solax": {
  "PollIntervalSeconds": 5,   // one poll/decide cycle per this many seconds
  "Inverter":  { "Host": "192.168.2.6",  "Port": 502, "UnitId": 1 },
  "EvCharger": { "Host": "192.168.2.10", "Port": 502, "UnitId": 1 }
}
```

The feature sections — `Solcast`, `ChargeControl`, `BatteryHold` and `HomeAssistant` — are documented
in the subsections that follow.

### Solcast solar forecast

The worker fetches a solar-generation forecast for your site from [Solcast](https://solcast.com/) and caches it locally, refreshing on a configurable interval (default 12 hours). Non-secret settings live in `appsettings.json` under the `Solcast` section:

```jsonc
"Solcast": {
  "BaseUrl": "https://api.solcast.com.au/",
  "ResourceId": "your-solcast-resource-id", // the rooftop site id from your Solcast account
  "RefreshInterval": "12:00:00"             // hh:mm:ss between refreshes
}
```

The **API key is a secret and must not be committed**. Provide it out-of-band, using whichever of these fits how you run the app:

- **`.env` file (recommended for local dev)** — copy `.env.example` to `.env` (which is gitignored) in the repo root and set your key. On startup the worker loads the nearest `.env` into the process environment, so it works both from `dotnet run` **and** the VS Code debugger without any shell setup:

  ```bash
  cp .env.example .env
  # then edit .env:  Solcast__ApiKey=<your-api-key>
  ```

- **Environment variable** — set it in your shell/service manager (double underscore separates config sections). A real environment variable always takes precedence over `.env`:

  ```bash
  export Solcast__ApiKey="<your-api-key>"
  ```

- **.NET user-secrets** — the `Solax.Worker` project has a `UserSecretsId`, so this works in Development too:

  ```bash
  cd src/Solax.Worker
  dotnet user-secrets set "Solcast:ApiKey" "<your-api-key>"
  ```

If the API key or resource id is missing, the worker logs a warning and skips forecast refreshes; the rest of the service continues to run. The free Solcast hobbyist tier caps daily API calls, which is why the forecast is cached and refreshed only every 12 hours by default — keep the interval within your plan's quota.

### EV charge control (writes to the charger)

When enabled, the worker drives the EV charger from **live solar surplus**, and only once the home battery is essentially full. It writes **only the charge-current setpoint** — it never changes the charger's use-mode and never sends a start/stop command, so you keep the charger in Fast mode and the controller modulates the current under it (see "Current-only control" below). It writes only values that differ from what's already on the device and logs every change.

The current setpoint is always constrained to what the hardware accepts (**6–32 A**): the configured min/max are clamped into that range up-front, so the controller can never even target an illegal value, and the write path clamps again as a final guard.

#### How the surplus is calculated

```
Surplus = Solar production − household consumption
```

where household consumption **excludes battery charging and EV charging** — so whatever the house isn't using is what the car may have. Charging from it therefore neither imports from the grid nor discharges the battery, and the car is free to outbid battery charging.

Household consumption is the "Other Loads" residual from the energy balance:

```
OtherLoads = PV + Grid − EV − Battery        (Grid +ve = importing, Battery +ve = charging)
Surplus    = Solar − OtherLoads
```

**This requires the grid meter, not the inverter's output.** `Grid` comes from **`FeedinPower` (`0x0046`, int32, low word first, positive = export)** — the CT/meter reading at the utility connection, the only register that sees the whole house. It lives inside the telemetry block already fetched, so it costs no extra round-trip.

> ⚠️ The per-phase registers `0x6C/0x70/0x74` (mapped as `GridPowerR/S/T`) are **not** the grid meter — they report the **inverter's AC output**. Verified live: they track `Solar − Battery` at ~94–96% (inverter efficiency), while `FeedinPower` simultaneously read a genuine 388 W export. Using them for household load produces nonsense (a 2.4 kW-of-sun reading once yielded a 13 kW "surplus"). They are kept in the map for reference only.

Worked example from a live run: Solar 2498 W, exporting 388 W, battery idle, EV idle →
`OtherLoads = 2498 − 388 = 2110 W`, so `Surplus = 2498 − 2110 = 388 W` — exactly the exported power.

#### Smoothing: moving average and hysteresis

Raw solar generation is erratic, so the controller never reacts to instantaneous data. Two buffering strategies keep it stable:

**1. The 3-minute moving average.** Every poll, the surplus (`PV − Load`) is pushed into a rolling time window and the *average* drives every decision. A single 15-second dark cloud therefore can't interrupt a 3-hour charging session — only a sustained drop moves the average enough to matter. The window is `SurplusAverageWindow` (default `00:03:00`); samples older than it are evicted each poll.

**2. The 1-amp hysteresis threshold.** A Modbus write is only issued when the new target differs from the charger's active setpoint by at least `CurrentChangeThresholdAmps` (default 1 A ≈ 230 W per phase). If the car is charging at 10 A, no command is sent until the average calls for 11 A or 9 A. Raise the threshold to damp the charger further (e.g. 3 A means 10 A → 12 A is ignored, 10 A → 13 A is written).

These stack with the existing state hysteresis — the asymmetric start/stop thresholds on both the surplus and the battery SOC gate — so the charger is never nudged by noise.

You can watch all of it in the log; each control cycle prints the raw surplus, the average, the sample count, the charger's active setpoint, and the target:

```
Charge control: Mode=Fast Surplus=4180W Avg=3990W (12 samples) Setpoint=16A Action=Charge Target=17A. Live surplus 3990W -> charge at 17A.
```

and the telemetry line carries the full energy picture plus the charger's active current:

```
SOC=96% BatteryPower=-56W Solar=4180W Grid=-388W EvCharger=Charging EvMode=Fast EvCurrent=16A EvPower=3680W
```

#### Current-only control: what it changes, and what it doesn't

The controller runs its own Modbus loop and sets the charging **current** from its `Surplus = PV − household load` calculation. It deliberately does the **minimum**:

- It **only writes the current setpoint** (`0x628`). It **never** changes the charger's use-mode (Green/ECO/Fast) and **never** sends a start/stop command.
- It **only acts when all three hold**: the SolaX device is reachable, its own use-mode reads **Fast**, and the HA mode is **Solar**. In any other mode (Green/ECO/Stop) it leaves the charger completely alone — you keep the charger in Fast; the controller just modulates the current under it.

#### The 6 A hard cutoff — pause by dropping the current

An EV won't accept a 2 A or 4 A charge — **6 A is the floor** (IEC 61851). So (on the *averaged* surplus):

| Surplus | Decision | Current written |
|---|---|---|
| ≥ 6 A equivalent | `Charge` | the computed current (whole amps, clamped to the min/max) |
| < 6 A equivalent | `Pause` | `PauseCurrentAmps` (default **0 A**) |

If it simply left the charger at its 6 A minimum when the surplus dropped below it, the charger would **make up the shortfall from the grid** — exactly what solar-only charging avoids. So the pause drops the current to `PauseCurrentAmps`: **0 A**, which suspends the car the way Green mode does, without changing the mode or ending the session — charging resumes when surplus returns. (SolaX documents the current register as 6–32 A; if your charger doesn't accept 0, set `PauseCurrentAmps` to a sub-6 A value the car refuses instead.)

The threshold is **phase-aware** — the 6 A floor is ~1.4 kW single-phase but ~4.2 kW three-phase (see `Phases`), so on a three-phase charger the cutoff triggers far earlier in watt terms. Hysteresis is asymmetric on purpose: charging continues down to exactly the 6 A floor, but only *restarts* once the surplus clears `6 A + ResumeHysteresisWatts`.

A **battery-SOC gate** with hysteresis fronts the whole thing: charging engages only at/above `BatteryFullSocPercent` (so the car never competes with charging the home battery) and, once charging, keeps going until SOC falls below `BatteryReleaseSocPercent` — the band stops the car's own draw from flapping the gate.

```jsonc
"ChargeControl": {
  "DryRun": false,              // when Enabled: log intended writes but don't write (validation)
  "NominalVoltage": 230,
  "Phases": 3,                  // 1 = single-phase, 3 = three-phase (e.g. X3-HAC)
  "MinChargingCurrentAmps": 6,
  "MaxChargingCurrentAmps": 16, // setpoint is clamped to this (see "vehicle limit" below)
  "CurrentStepAmps": 1,         // whole-amp granularity the charger accepts
  "PauseCurrentAmps": 0,        // current written to pause (0 = suspend like Green mode)
  "SurplusAverageWindow": "00:03:00",  // rolling window the surplus is averaged over
  "CurrentChangeThresholdAmps": 1,     // min amp change before re-commanding the charger
  "ResumeHysteresisWatts": 200, // extra surplus needed to (re)start, to avoid flapping
  "BatteryFullSocPercent": 95,  // SOC at/above which charging engages
  "BatteryReleaseSocPercent": 90 // SOC it must fall below to disengage
}
```

**The vehicle is usually the binding limit, not the charger.** Charging negotiates down to the *lowest shared capability*, so `MaxChargingCurrentAmps` should reflect whichever of the car and the wallbox is lower. For a **VW ID.4** (the reference setup here):

| Setup | Car's limit | Configure |
|---|---|---|
| Three-phase (X3-HAC 11/22 kW) | **16 A/phase → 11 kW** — the ID.4's onboard charger caps here even on a 22 kW/32 A wallbox | `Phases: 3`, `MaxChargingCurrentAmps: 16` |
| Single-phase (X1-HAC-7) | **32 A → 7.2 kW** — the ID.4 pulls the wallbox's full current | `Phases: 1`, `MaxChargingCurrentAmps: 32` |

Setting a max above what the car will accept isn't dangerous (it simply won't draw it), but it makes the surplus maths optimistic — the controller thinks it has more headroom than the car will use. The defaults above are the three-phase ID.4 values.

**Set `Phases` to match your charger.** The 6 A EVSE minimum is a *current* limit; its power floor depends on phase count — ~1.4 kW single-phase vs **~4.2 kW three-phase** — and the watts↔amps setpoint uses `watts / (NominalVoltage × Phases)`. A three-phase charger left at `Phases: 1` would start on a ~1.4 kW surplus while the car pulls ~4.2 kW, importing the difference from the grid.

The current setpoint is encoded to the SolaX hardware's requirements automatically: rounded to a whole amp, clamped to `0…32 A` (0 for pause), and written with the register's **0.01 A scale** (value = amps × 100).

**Validate first with `DryRun`.** Set `Enabled: true` and `DryRun: true` to run the full control loop and log exactly what it *would* write — e.g. `[DRY RUN] would set charger current setpoint: 6A -> 16A (register 1600)` — without touching the charger. This is the safe way to confirm the register values against your device before allowing real writes.

In dry-run, **nothing is ever written to a SolaX device**. That's enforced twice: each write site is skipped, and the Modbus clients are wrapped in a read-only decorator that drops writes outright, so even a caller that forgot its guard cannot reach the hardware. A suppressed write logs a warning as a tripwire — it should never appear.

> ⚠️ **This feature writes to your charger.** It writes only the charge-current setpoint (`ChargeCurrentSetpoint 0x628`) and reads the use-mode (`ChargerUseMode 0x60D`) as a precondition — both from the SolaX X1/X3-HAC protocol / the wills106 register map, but **GEN1/GEN2 and firmware differences exist** (GEN1 uses Datahub Charge Current `0x624`). Also confirm your charger accepts `PauseCurrentAmps` (0 A by default). **Verify against your charger before setting `Enabled: true`.** Disabled by default for exactly this reason.

### Battery discharge hold (writes to the inverter)

A switch that stops the home battery discharging, so charging the EV never drains it. PV covers what
it can, the grid covers the rest, and the battery is left alone — but it can **still charge** from PV
surplus, which is the whole point of using this rather than simply freezing the battery.

It is deliberately orthogonal to charge control, not a third charge mode:

- **Hold on + charge mode `Off`** — the charger runs at whatever current you set in Fast mode. PV
  covers what it can, the grid tops up, the battery is untouched.
- **Hold on + charge mode `Solar`** — the surplus loop runs unchanged, with a safety net underneath it
  for the moments its estimate is briefly wrong.
- **Hold on with no EV charging** — a general "preserve the battery" switch, e.g. ahead of an
  expensive tariff period or a known outage.

#### How it works

The inverter decides where the EV's power comes from, not this controller — in Self Use mode it sees
the EV as household load and discharges the battery to cover it, whatever charging current we set. So
the hold doesn't touch the charger at all. It uses the inverter's **Modbus Power Control** command
(holding register `0x7C`) to drive the inverter's grid-connection point to a commanded power target of
`-min(house load, PV)`:

- **PV covers the house** — push out the whole load. The house runs on sun, and the PV it doesn't need
  has nowhere to go but the battery, so surplus charging is preserved.
- **PV falls short** — push out all the PV there is. The inverter is already at its maximum, so the
  shortfall can only come from the grid. The battery is never asked to contribute.

> **This is not the SolaX "No Discharge" mode.** That option exists in the upstream Home Assistant
> integration but never reaches the inverter — it is a client-side strategy, and the formula above is
> what it actually sends. See [docs/DECISIONS.md](docs/DECISIONS.md).

Because the target follows live house load and PV, it is recomputed every poll and reissued whenever
it moves past `TargetChangeThresholdWatts`, plus a renewal at half of `Duration`. The command is *not*
a stored setting — nothing is written to EEPROM, and it lapses on its own.

**That expiry is the failsafe.** If the service stops, nothing renews the command and the inverter
returns to normal operation within `Duration` (60 s by default). There is no shutdown hook and no
cleanup path — the inverter provides the guarantee.

Turning the switch off writes a release immediately; it never waits for the duration to run out.

#### Persistence and reported state

The hold does **not** survive a restart, and cannot be armed by configuration: the service always
comes back with the switch **off**, so the battery charges and discharges normally until somebody asks
otherwise. The inverter will already have resumed normal operation by then anyway, since the armed
command expires after `Duration`. That is deliberate — the hold is a command with a lifetime, not a
stored setting, so an unattended restart that re-armed it would silently keep the pack idle.

The Home Assistant switch reports **what the controller last successfully wrote**, not a reading from
the inverter — register `0x7C` reports the firmware version when read, so the command state cannot be
read back. A failed write therefore shows up as the switch springing back to OFF rather than as an
assumed success. As a cross-check, the controller logs a warning if the battery discharges by more
than 150 W while it believes the hold is armed.

**A working hold still leaves a 50–65 W trickle out of the battery** — inverter standby draw, not
load being served; measured here across house loads from 143 W to 2877 W. So the guarantee is that
the battery stops *serving the house*, not that battery power reaches exactly zero. The 150 W
deadband above exists for this reason: warning on any negative value fires every poll and drowns out
the signal it is there to give.

#### Configuration

```jsonc
"BatteryHold": {
  "Enabled": false,                 // master switch — while off, inverter writes are impossible
  "DryRun": true,                   // decide and log, but write nothing
  "Duration": "00:01:00",           // how long each command stays armed; also the failsafe window
  "TargetChangeThresholdWatts": 100 // how far the target must move before reissuing
}
```

`Enabled` is a true master switch: while it is off no Home Assistant
switch is published, the poll loop skips the feature, and the inverter's Modbus client is wrapped
read-only so a write is structurally impossible rather than merely skipped.

> ⚠️ **This is the only feature that writes to your inverter.** The register address, field layout and
> mode values come from the wills106 homeassistant-solax-modbus map, not from a SolaX document, and
> upstream reports behaviour differing across firmware versions.
>
> It **has been verified on the reference hardware** (X3-HYB-G4 PRO, 2026-07-27): arming the hold
> moved the house from 2846 W of battery discharge to 1601 W of grid import within one poll, renewal
> held it continuously, and PV was not curtailed. Full measurements are in
> [docs/DECISIONS.md](docs/DECISIONS.md). Two things remain unobserved: behaviour under strong midday
> PV (does the battery still charge from surplus, and is PV curtailed at full output), and behaviour
> with the EV actually charging.
>
> None of that transfers to a different inverter or firmware, so `Enabled` stays `false` and `DryRun`
> stays `true` by default. **Validate with `DryRun: true` first** — it logs the exact block it would
> write (`[DRY RUN] would hold battery discharge: active power target -2000W for 60s (registers [...]
> at 0x7C)`) without touching the inverter.

### Forecast-driven charging (the `Forecasted` mode)

`Solar` waits for the home battery to be essentially full before the car gets anything. That costs
real energy: on a good day the battery only fills around midday, so the car starts as production is
already falling, and on three phases the charger's **6 A floor is ~4.2 kW** (no phase switching), so
any surplus below that charges *nothing* and is exported once the battery is full.

`Forecasted` replaces the fixed gate with a day plan built from the Solcast forecast, recomputed every
poll. It keeps one promise: **the home battery reaches 100 % by the configured evening deadline.**
Everything else follows from that.

#### The shoulders belong to the battery; the plateau belongs to the car

A kilowatt-hour at 08:00 is not interchangeable with one at 13:00, because the two consumers can't
take the same power:

| | Surplus below ~4.2 kW ("shoulder") | Surplus at or above it ("plateau") |
|---|---|---|
| Home battery | takes it — it accepts any power | takes it |
| EV charger | **cannot charge at all** | can charge |

So filling the battery from the plateau wastes the one scarce window, while filling it from the
shoulders costs the car nothing. The plan splits the remaining forecast by power level, books the
battery's need **backwards from the deadline** (the latest production first, so the afternoon shoulder
and, only if needed, the plateau tail), and hands the car whatever is left at a usable power.

```
EvBudget      = RemainingPv − ExpectedHouse − BatteryToFull
FeasibleEv    = the part of that arriving at ≥ the charger's minimum power, after the battery's booking
SocFloor      = 100 − (remaining surplus × efficiency ÷ capacity) × 100,  clamped to MinBatterySocFloorPercent
```

The evening guarantee needs no scheduling code: as the day burns down, the remaining surplus falls, so
`SocFloor` climbs toward 100 % and squeezes the car out of the late afternoon by itself. A
`FinalGuardBefore` window (default 1 h) pauses the car outright as a backstop.

#### The battery loan

On three phases a 3 kW surplus charges nothing. If the forecast shows the day can repay it, the
battery lends the difference — sized to reach **exactly** the 6 A floor, never more — turning
would-be export into charge. It is bounded four ways:

- never below the plan's SOC floor, nor below `MinBatterySocFloorPercent` (default 50 %);
- `MaxLoanPowerWatts` (default 2500) caps the bridge and the discharge rate;
- `MaxDailyLoanKWh` (default 4) caps a day's lending, reset at local midnight;
- **no loan below `MinBridgeSurplusWatts`** (default 2000) and **none at all on a shortfall day** —
  the loan tops up a genuine surplus, it never funds a session from the pack, which would pay a round
  trip and a cycle on both batteries for nothing.

With `BatteryHold:Enabled` on, the [battery discharge hold](#battery-discharge-hold-writes-to-the-inverter)
is armed automatically once SOC reaches the floor, so an estimate error can't dig below it — the grid
covers the gap instead of the pack. A manual hold from HA always wins.

#### When the sun can't cover everything

The priority order is fixed and not configurable:

> **1. House load → 2. Battery to 100 % by the deadline → 3. EV.**

The car absorbs the entire shortfall, and **no grid charging is ever initiated**. A partial charge
does an EV pack no harm — an NMC pack is happier at mid SOC than topped up daily — but a shortfall
discovered at dusk with a silently paused charger is unhelpful, so it is announced instead: a
`Day outlook` of `Surplus | Tight | Shortfall | NoChargeToday`, a projected shortfall in kWh, and what
the car can still expect today, all published to HA and logged as soon as the day can be judged.
Tomorrow's forecast rides along in the same Solcast response, so a bad day comes with the context of
whether waiting is worth it.

#### Forecast versus reality

A plan is only as good as the forecast under it, so the controller checks continuously:

- **House load is learned per hour of day**, not as one rolling average. A household's load has a
  strong daily shape, and a trailing average of it is always wrong in the same direction: measured at
  15:00 it reports the afternoon peak, which then gets projected across the evening. On the reference
  site that turned a 05:00 plan of "33.6 kWh available, window 08:00–16:30" into "no EV charging
  today" by noon, on a day whose forecast was accurate to within 5%. The profile is seeded from
  `BaselineHouseLoadWatts` and logged once a day (`House profile:`) so the learned shape is visible.
- **Realised bias** — `actual ÷ forecast` over elapsed daylight, clamped to `[0.5, 1.2]` and applied
  to the remaining forecast. Asymmetric on purpose: under-production scales the rest of the day down
  (raising the floor, throttling the car early — the conservative direction), while a sunny morning
  can't talk the planner into over-committing the afternoon. It stays at 1.0 until `BiasMinPeriods`
  daylight periods have closed.
- **Per-period reconciliation** — one log line per closed 30-minute period.
- **Trust guard** — if the bias leaves `[0.6, 1.4]` for `TrustBreachPeriods` consecutive periods, the
  plan is abandoned for the day with a warning and the mode falls back to `Solar` behaviour. The same
  fallback covers a missing or stale forecast: an absent forecast must never read as headroom.

The plan is built on the **p10** band (`pv_estimate10`), not the median — planning a guarantee against
a p50 forecast means missing it about half the time. The forecast refresh drops to **3 hours** and is
skipped overnight (a fresh forecast can't change a decision made in the dark), which is ~5 calls a day
against the free tier's 10.

#### What it logs

```
Day outlook: Shortfall — Forecast=7.2kWh House=4.1kWh BattToFull=5.3kWh EvTarget=15.0kWh Short=17.2kWh …
Day plan: Shoulder=3.4kWh Plateau=11.0kWh … EvBudget=10.5kWh Feasible=10.5kWh Window=12:30-15:50 SocFloor=62% Bias=0.94 (P10)
Forecast check: Period=12:00-12:30 Forecast=3120Wh Actual=2890Wh Delta=-230Wh (-7%) … Bias=0.94
Charge control: Mode=Forecasted … Action=Charge Target=6A Loan=1140W Session=8.4kWh LoanedToday=1.8kWh. …
Day summary: ForecastToday=28.1kWh ActualToday=26.4kWh (-6%) BatterySoc@19:00=100% EvDelivered=14.2kWh …
```

The day plan logs at `Information` only when it actually changes (at a 5-second poll, anything else
would bury the log) and at `Debug` otherwise; the outlook logs on transitions; the summary once, at
the deadline.

```jsonc
"ChargeControl": {
  // ... the live-solar settings above still apply; Forecasted reuses the same charger limits
  "Forecast": {
    "FullByTime": "19:00:00",        // evening deadline (local time) for a 100% battery
    "BatteryCapacityKWh": 9.0,       // REQUIRED: USABLE capacity (see the warning below), not nameplate
    "ChargeEfficiency": 0.95,
    "BaselineHouseLoadWatts": 350,   // seed for the learned hour-of-day house-load profile
    "ForecastConfidence": "P10",     // P10 | P50 | P90 — P10 is what makes the guarantee honest
    "MinBatterySocFloorPercent": 50, // hard floor, whatever the forecast says
    "DailyEvTargetKWh": 15,          // what the car should get; the shortfall is measured against it
    "SessionEnergyTargetKWh": 0,     // per-session ceiling, 0 = unlimited (stands in for "charge to 80%")
    "EnableBatteryLoan": true,
    "MaxLoanPowerWatts": 2500,       // must be able to bridge a real surplus up to ~4.2 kW
    "MinBridgeSurplusWatts": 2000,   // no loan below this — never fund a session from the pack
    "MaxDailyLoanKWh": 4,
    "LoanSocMarginPercent": 2,
    "MinViableWindow": "00:30:00",   // shortest forecast window worth starting a session for
    "MinRunTime": "00:10:00",        // dwell timers: no start/stop churn faster than these
    "MinPauseTime": "00:15:00",
    "FinalGuardBefore": "01:00:00",  // pause the car this long before the deadline if SOC < 100%
    "StaleForecastAfter": "04:00:00",// older than this → fall back to Solar behaviour
    "AutoArmBatteryHoldAtFloor": true,
    "HoldReleaseMarginPercent": 2,
    "BiasMinPeriods": 4,             // closed daylight periods before the bias is trusted
    "BiasClampMin": 0.5,
    "BiasClampMax": 1.2,
    "TrustBandMin": 0.6,             // sustained breach → abandon the plan for the day
    "TrustBandMax": 1.4,
    "TrustBreachPeriods": 3
  }
}
```

> ⚠️ **`BatteryCapacityKWh` is the pack's *usable* capacity, not its nameplate.** It is the one value
> with no safe default: the SOC floor, the battery's booking and the shortfall all scale off it, and a
> wrong figure makes the plan wrong in a way nothing else catches.
>
> The reference install is a **SolaX T-BAT H 2.5** stack — 10 kWh nominal, so **9.0 kWh** usable at the
> ~90 % depth of discharge these packs allow (confirm the exact figure against your datasheet). The
> distinction matters because the inverter reports SOC across the range it will actually cycle: 0–100 %
> spans the *usable* energy, not the nameplate.
>
> **If you must guess, guess high.** Both uses move in the safe direction when the figure is
> overstated — the battery books more of the forecast, and the SOC floor sits higher. Understating it
> is what risks missing the evening 100 %.
>
> You can measure it from the logs you are already producing. Over one uninterrupted climb with no
> discharge in between (say 30 % → 90 %), integrate the logged `BatteryPower` over time and divide by
> the SOC delta: `usable kWh = (∫ BatteryPower dt) / 0.60`. That also validates `ChargeEfficiency`,
> since the integral is measured at the battery terminals.

**Validate it read-only first.** The accuracy tracker runs in every mode, so leaving the service on
`Off` or `Solar` for a week still fills the log with `Forecast check` and `Day summary` lines. That
answers the two questions that decide the settings — is Solcast p10 systematically low for this roof,
and does the shoulder/plateau split match the real curve — before anything acts on them.

### Fast charge without the battery (the `FastNoBattery` mode)

The "I leave in an hour" button. Where `Solar` and `Forecasted` ration the car to what the sun can
spare, this mode does the opposite: **charge as fast as the installation allows, and keep the home
battery out of it.** While it is selected:

1. The **battery discharge hold is armed automatically** — the pack never serves the car (see
   [Battery discharge hold](#battery-discharge-hold-writes-to-the-inverter) for the mechanism).
2. The charger is pinned at **`MaxChargingCurrentAmps`**, every cycle, whatever the sun, the SOC, the
   forecast or the time of day. PV covers what it can and the **grid covers the rest**.
3. When the car stops drawing because it reached **its own** charge limit, the setpoint drops to
   `PauseCurrentAmps`, the mode returns itself to **`Off`**, and the hold it armed is released.

Point 3 is what makes it safe to press. The state it creates is expensive — maximum current, grid
import, battery locked — and it ends by itself instead of sitting armed until somebody notices.

> ⚠️ **`MaxChargingCurrentAmps` is a supply limit in this mode, not a preference.** The other modes
> only reach it when the sun is that generous; this one sits there for hours. On the reference install
> 16 A × 230 V × 3 phases ≈ **11 kW** drawn continuously from PV and grid together. Set it to what
> your supply and main breaker actually allow.

#### When is the car "finished"?

The charger reports the car's state, and what it reports at the end of a session is firmware-specific,
so the rule leans on power and treats the status as a corroborating signal:

- the car counts as **idle** while it draws no more than `CompletionPowerThresholdWatts` (200 W —
  well above standby, well below the 6 A floor), **or** while the charger reports `SuspendedEv` or
  `Finishing`, which is the car saying it is done even if it is still trickling;
- the session is **finished** once it has been idle continuously for `CompletionDwell` (2 min);
- but only if the car **has drawn power at least once** since it was plugged in. Without that, a car
  still negotiating — or waiting on its own departure timer — would end the mode seconds after it was
  selected;
- **unplugging ends it immediately**, on the same path.

`ChargePaused` and `SuspendedEvse` are deliberately *not* treated as "the car is done": those are the
charger's own doing, which is exactly what our pause write produces.

#### What it doesn't do

- **It doesn't survive a restart.** Like every mode, the service comes back in `Off` — a service that
  restarted mid-session and silently resumed drawing 11 kW from the grid is not a behaviour anyone
  wants unattended. The charger keeps its last setpoint until a mode is selected; the inverter's hold
  lapses within `BatteryHold:Duration`.
- **It doesn't touch a hold you asked for yourself.** On completion it releases only the hold it
  armed; the Home Assistant switch stays exactly as you set it.
- **It doesn't change the charger's use-mode.** As with the other modes, the owner keeps the charger
  in Fast and this only moves the current setpoint.

With `BatteryHold:Enabled` false the mode still charges at maximum current, and logs a warning once on
selection: it cannot keep the battery out of the charge, which is half of what it promises.

```jsonc
"ChargeControl": {
  "MaxChargingCurrentAmps": 16,          // the ceiling this mode pins the charger at
  "CompletionPowerThresholdWatts": 200,  // below this draw, the car counts as not charging
  "CompletionDwell": "00:02:00"          // idle this long -> finished; pause and return to Off
}
```

### Home Assistant (MQTT)

The worker can expose itself to Home Assistant over MQTT ([HA MQTT Discovery](https://www.home-assistant.io/integrations/mqtt/#mqtt-discovery)), so HA auto-creates a device with:

- a **Charge mode** select — change the mode **at runtime**, no restart:
  - **Off** — the controller doesn't touch the charger; its current setpoint is left exactly as it is.
  - **Solar** — modulate the charging current from live surplus while the battery is full (and only while the charger's own use-mode is Fast); pause when there isn't enough sun.
  - **Forecasted** — as Solar, but the fixed battery-full gate is replaced by a forecast-driven day
    plan, so the car can start well before the battery is full. See
    [Forecast-driven charging](#forecast-driven-charging-the-forecasted-mode) below.
  - **FastNoBattery** — charge at the maximum configured current from PV and grid together, with the
    battery discharge hold armed automatically, and return to `Off` when the car is full. See
    [Fast charge without the battery](#fast-charge-without-the-battery-the-fastnobattery-mode) below.
    This is the one mode that switches *itself* off, so the select will change under you when the car
    finishes.

  **The service always starts in `Off`**, whatever is in the config, and nothing persists a mode
  across restarts. After a crash, a power cut or a deploy the charger is therefore left exactly as its
  owner set it, rather than being grabbed by whichever mode a config file happened to name.
- a **Battery discharge hold** switch, when `BatteryHold:Enabled` is on — see
  [Battery discharge hold](#battery-discharge-hold-writes-to-the-inverter) above for what it does and
  why its state reflects the last successful write rather than a device read-back.
- sensors: **Control state**, **Charger status** (Available / Charging / ChargePaused / …), **Solar power** and **Solar surplus**, **EV charging power** and **EV charging current** (actual draw), **Target/Active charging current** (setpoint), **Battery SOC**, **Battery power**, **Grid power** (positive = importing, negative = exporting), and **Battery hold target** (while the hold is enabled).
- forecast-plan sensors, populated while the **Forecasted** mode is driving: **Day outlook**,
  **Plan state**, **Charge window**, **EV energy budget**, **EV energy expected today**,
  **Projected shortfall**, **Required SOC floor**, **Forecast remaining today**,
  **Tomorrow forecast**, **Forecast accuracy**, **Session energy**, **Battery loaned today** and
  **Battery loan power**. `Day outlook` and `Projected shortfall` are what a "not enough sun for the
  car today" notification automation keys off.
- numbers, settable at runtime: **Daily EV target** (kWh), **Session energy target** (kWh, 0 =
  unlimited) and **Minimum battery SOC** (%). Like the mode, changes don't persist across restarts.
- binary sensors: **Car connected** and **Charging now**.
- an availability topic, so HA marks the device unavailable if the controller stops.

Disabled by default. Non-secret settings live in `appsettings.json`:

```jsonc
"HomeAssistant": {
  "Enabled": false,
  "BrokerHost": "localhost",
  "BrokerPort": 1883,
  "DiscoveryPrefix": "homeassistant", // HA's discovery prefix
  "BaseTopic": "solax",
  "DeviceId": "solax_controller",
  "DeviceName": "SolaX Local Controller",
  "StatusInterval": "00:00:15"
}
```

Broker credentials are secrets — supply via `.env` / env var, not `appsettings.json`:

```
HomeAssistant__Username=<user>
HomeAssistant__Password=<pass>
```

A ready-to-run broker + Home Assistant for local development lives in [`dev/homeassistant/`](dev/homeassistant/) (`docker compose up -d`). Watch the traffic with:

```bash
docker exec -it solax-dev-mosquitto mosquitto_sub -t 'homeassistant/#' -t 'solax/#' -v
```

## License

Licensed under the [MIT License](LICENSE).
