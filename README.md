# SolaX Local Controller

A standalone, locally hosted background service for managing and monitoring a **SolaX X3-HYB-G4 PRO** hybrid inverter and a **SolaX X1/X3-HAC** EV charger.

The controller operates entirely within the local LAN via **Modbus TCP**, bypassing cloud dependencies to ensure continuous operation, instantaneous polling, and strict local data ownership. It polls real-time data (PV generation, battery SOC, grid power flow) and applies automated decision-making logic to optimize EV charging and battery utilization based on household energy surpluses.

## Status

🚧 Early development — not yet functional. This README describes the intended design and will evolve alongside the implementation.

## Why local?

Cloud-based SolaX monitoring/control (SolaX Cloud, third-party integrations) introduces latency, external dependencies, and data collection outside the user's control. This project talks directly to the inverter and EV charger over Modbus TCP on the local network, so:

- Control logic keeps working during internet outages.
- Polling and decision cycles run at LAN speed, not cloud round-trip speed.
- No telemetry leaves the local network unless explicitly configured.

## Key features (planned)

- **Real-time polling** of PV generation, battery state of charge, grid import/export, and EV charger status over Modbus TCP.
- **Surplus-aware EV charging** — automatically ramp EV charge current up/down based on available household energy surplus.
- **Battery utilization optimization** — coordinate charge/discharge behavior with EV charging demand.
- **Background service** — runs unattended as a long-lived process (e.g. systemd service / Windows Service / Docker container).
- **Local data ownership** — no cloud dependency for core operation.

## Hardware targets

| Device | Model | Interface |
|---|---|---|
| Hybrid inverter | SolaX X3-HYB-G4 PRO | Modbus TCP |
| EV charger | SolaX X1/X3-HAC | Modbus TCP |

## Tech stack

- [.NET 10](https://dotnet.microsoft.com/) — target framework
- Hosted as a [.NET Worker Service](https://learn.microsoft.com/dotnet/core/extensions/workers) (background service)
- Modbus TCP client for inverter/charger communication

## Project structure

The solution is organized to keep domain/control logic testable and free of hardware and hosting concerns:

```
SolaxLocalController.sln
├── src/
│   ├── Solax.Core/                 # Domain logic and hardware abstractions
│   │   ├── Models/                 # Strongly typed models (EnergyState, DeviceConfig)
│   │   ├── Enums/                  # Register addresses, Charger modes, Inverter states
│   │   └── Interfaces/             # IModbusClient, IChargingStrategy
│   │
│   ├── Solax.Infrastructure/       # External communication
│   │   ├── Modbus/                 # Concrete Modbus TCP client implementation
│   │   └── RegisterMaps/           # Hex address mappings for SolaX Gen4 and EV Charger
│   │
│   └── Solax.Worker/               # The executable host
│       ├── Program.cs              # Dependency Injection setup
│       ├── SolaxPollingService.cs  # The main background loop (IHostedService)
│       └── Dockerfile              # Container definition targeting ARM architecture
└── tests/
    └── Solax.Core.Tests/           # Unit tests for the control logic (mocking hardware)
```

### Layering rules

- **Dependency direction is one-way:** `Solax.Worker` → `Solax.Infrastructure` → `Solax.Core`. `Solax.Core` must never reference `Solax.Infrastructure` or `Solax.Worker`.
- **`Solax.Core` has no hardware or framework dependencies.** No Modbus libraries, no `Microsoft.Extensions.Hosting` types — only plain models, enums, and interfaces (`IModbusClient`, `IChargingStrategy`). This is what keeps control/decision logic unit-testable without real hardware.
- **All decision-making logic lives in `Solax.Core`**, expressed against interfaces. Charging strategy, surplus calculations, and SOC-based rules belong here, not in `Solax.Infrastructure` or `Solax.Worker`.
- **`Solax.Infrastructure` only implements `Solax.Core` interfaces.** Modbus TCP details and register maps stay isolated here; no business/decision logic.
- **`Solax.Worker` is composition-only.** `Program.cs` wires up DI; `SolaxPollingService` orchestrates the poll/act loop by calling into `Solax.Core` abstractions — it should not contain control logic itself.
- **`Solax.Core.Tests` mocks the hardware boundary** (`IModbusClient`, etc.) to exercise control logic without a live device.

## Getting started

> Implementation has not started yet. This section will be filled in with build, configuration, and run instructions once the initial service scaffold lands.

## Workflow & Project Management
You are authorized and expected to use the GitHub CLI (`gh`) to manage this project. 
When asked to manage tasks or submit code, use the following commands:
- `gh issue list`: To check current tasks.
- `gh issue view <id>`: To read the requirements of a specific task.
- `gh issue create -t "<title>" -b "<body>"`: To create new tasks.
- `gh pr create -t "<title>" -b "<body>"`: To submit your implemented code for review.
Do not use `git push` directly to the main branch; always create a branch and use `gh pr create`.

## Documentation & Implementation Notes
You must maintain a living record of your implementation choices in `docs/IMPLEMENTATION_NOTES.md`.
Whenever you complete a task or write a significant piece of logic (like the Modbus polling loop or MQTT discovery):
1. Append a short entry to `IMPLEMENTATION_NOTES.md` detailing *what* you built, *why* you chose that approach, and any technical debt or edge cases (like SolaX hardware limitations).
2. When using `gh pr create`, use these notes to generate a highly detailed Pull Request body. The PR description must explain the architecture decisions, not just list the changed files.

## Documentation Organization
All project notes live in the `docs/` directory. You are responsible for keeping them updated:
1. `ARCHITECTURE.md`: Update this ONLY when the fundamental structure, network topology, or data models change.
2. `DECISIONS.md`: Append a new record here if we choose a new library (like MQTTnet) or establish a new core pattern.
3. `IMPLEMENTATION_LOG.md`: Before submitting a Pull Request via `gh pr create`, you MUST add a reverse-chronological entry to the top of this file detailing the implementation specifics, hardware quirks encountered (e.g., Modbus limitations), and the files changed.

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Network access to the SolaX inverter and EV charger with Modbus TCP enabled

## Configuration

Configuration (device IP addresses, Modbus ports/unit IDs, polling intervals, charging strategy parameters) will be documented here once implemented.

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

When enabled, the worker drives the EV charger from **live solar surplus**, and only once the home battery is essentially full. Once the conditions are met (battery full + enough surplus) it **starts a session** — including from an idle `Available`/`Stop` charger, which is exactly where its own reset leaves things — by setting Fast mode with the computed current and issuing a `Start Charging` command. The command is sent only on the transition into charging, not on every poll. It writes only values that differ from what's already on the device and logs every change.

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
Charge control: Surplus=4180W Avg=3990W (12 samples) Setpoint=16A Target=17A Action=Charge. ...
```

and the telemetry line now carries the charger's active current:

```
SOC=96% ... EvCharger=Charging EvMode=Fast EvCurrent=16A EvPower=3680W
```

#### The 6 A hard cutoff — why the controller must explicitly stop

This project bypasses the charger's native surplus modes: it runs its own Modbus loop and sets the exact current from its own `Surplus = PV − Load` calculation. That makes one rule critical.

An EV will not accept a 2 A or 4 A charge — **6 A is the floor** (IEC 61851). So the logic engine has a hard cutoff (applied to the *averaged* surplus):

- **Surplus ≥ 6 A** → write the charger's amperage to match the surplus.
- **Surplus < 6 A** → **explicitly send a Stop/Pause command.**

That second branch is the important one. If the controller simply left the charger running at its lowest setting (6 A) when the surplus dropped below it, the charger would **make up the missing power from the grid** — silently importing exactly what solar-only charging is meant to avoid.

**How this is implemented here:**

| Surplus | Decision | What is written |
|---|---|---|
| ≥ 6 A equivalent | `Charge` | Fast mode + the computed current (whole amps, clamped 6–32 A) |
| < 6 A equivalent | `Pause` | use-mode `Stop` + current `6 A` — charging is suspended, **not** terminated |

The pause is explicit — the charger is actively suspended rather than left sitting at its 6 A minimum, so it can never make up a shortfall from the grid. It is *not* a session teardown: no `Stop Charging` command is sent, so charging resumes as soon as surplus returns. Note the threshold is **phase-aware** — the 6 A floor is ~1.4 kW single-phase but ~4.2 kW three-phase (see `Phases`), so on a three-phase charger the cutoff triggers far earlier in watt terms.

Two related details:
- **Hysteresis is asymmetric on purpose:** charging continues down to exactly the 6 A floor, but only *starts* once the surplus clears `6 A + ResumeHysteresisWatts`. So the cutoff is sharp while start-up isn't twitchy.
- **We only pause what we started.** If the controller isn't holding control (it never started the session), it leaves the charger completely alone rather than suspending a session you began manually.

When the surplus runs out (or the service shuts down) charging is **paused, never terminated**: use-mode is set to `Stop` and the current setpoint to `6 A`, written only if they differ. The `Stop Charging` command (`0x627`) is deliberately **not** sent — that would end the session, which on many cars needs a re-plug to restart. Suspending via the use-mode halts the draw while leaving the session intact so it can resume the moment surplus returns.

A **battery-SOC gate** with hysteresis fronts the whole thing: charging engages only at/above `BatteryFullSocPercent` (so the car never competes with charging the home battery) and, once charging, keeps going until SOC falls below `BatteryReleaseSocPercent` — the band stops the car's own draw from flapping the gate.

```jsonc
"ChargeControl": {
  "Enabled": false,             // master switch — OFF by default (see warning)
  "DryRun": false,              // when Enabled: log intended writes but don't write (validation)
  "NominalVoltage": 230,
  "Phases": 3,                  // 1 = single-phase, 3 = three-phase (e.g. X3-HAC)
  "MinChargingCurrentAmps": 6,
  "MaxChargingCurrentAmps": 16, // setpoint is clamped to this (see "vehicle limit" below)
  "CurrentStepAmps": 1,         // whole-amp granularity the charger accepts
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

The current setpoint is encoded to the SolaX hardware's requirements automatically: rounded to a whole amp, clamped to the charger's **6–32 A** range, and written with the register's **0.01 A scale** (value = amps × 100). Pausing switches the use-mode to `Stop` and leaves the current setpoint untouched (0 A would be below the hardware minimum).

**Validate first with `DryRun`.** Set `Enabled: true` and `DryRun: true` to run the full control loop and log exactly what it *would* write — e.g. `[DRY RUN] would set charger current setpoint: 6A -> 16A (register 1600)` — without touching the charger. This is the safe way to confirm the register values against your device before allowing real writes.

In dry-run, **nothing is ever written to a SolaX device**. That's enforced twice: each write site is skipped, and the Modbus clients are wrapped in a read-only decorator that drops writes outright, so even a caller that forgot its guard cannot reach the hardware. A suppressed write logs a warning as a tripwire — it should never appear.

> ⚠️ **This feature writes to your charger's Modbus holding registers.** The control-register addresses (`ChargerUseMode 0x60D`, `ChargeCurrentSetpoint 0x628`) and `EvChargerMode` values come from the SolaX X1/X3-HAC protocol / the wills106 register map, but **GEN1/GEN2 and firmware differences exist** — GEN1 uses Datahub Charge Current `0x624`, some GEN2 units use EVSE Mode `0x669`. **Verify them against your specific charger before setting `Enabled: true`.** It is disabled by default for exactly this reason.

### Home Assistant (MQTT)

The worker can expose itself to Home Assistant over MQTT ([HA MQTT Discovery](https://www.home-assistant.io/integrations/mqtt/#mqtt-discovery)), so HA auto-creates a device with:

- a **Charge mode** select — change the mode **at runtime**, no restart:
  - **Off** — stop and **terminate** any session we were driving (sends the Stop-charging command, which on many cars needs a re-plug to restart); a charger we weren't already driving is left untouched. (This is a hard stop, unlike Solar's low-surplus *pause*, which keeps the session alive.)
  - **Solar** — automatic surplus charging (charge on live surplus while the battery is full).
  - **Force** — charge now at the maximum allowed current, ignoring surplus and the SOC gate (may pull from the grid / discharge the battery).

  The config `ChargeControl:Enabled` is only the boot default (`true` → Solar, `false` → Off); a runtime change doesn't persist across restarts.
- **Control state**, **Solar surplus**, **Target/Active charging current**, **Battery SOC** sensors, and a **Driving charger** binary sensor.
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
