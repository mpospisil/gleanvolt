# Gleanvolt

[![CI](https://github.com/mpospisil/gleanvolt/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/mpospisil/gleanvolt/actions/workflows/ci.yml?query=branch%3Amain)
[![Publish image](https://github.com/mpospisil/gleanvolt/actions/workflows/publish-image.yml/badge.svg?branch=main)](https://github.com/mpospisil/gleanvolt/actions/workflows/publish-image.yml?query=branch%3Amain)

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
- **Self-hosted web UI** (on by default, no configuration — see [Self-hosted web UI](#self-hosted-web-ui-the-web-section) below) — a Blazor dashboard served by the controller itself at `http://<host>:8090`: live telemetry, every control Home Assistant has, charging-session history and the forecast plan, all with no Home Assistant or MQTT broker required. Both surfaces are first-class: run either, both, or neither, and [`deploy/`](deploy/) can run the controller with neither Home Assistant nor a broker on a 1 GB board, at roughly a quarter of the memory the full stack needs.
- **Vehicle telemetry** — optionally reads the **car's own** battery SOC from MQTT, normalised so any
  vehicle Home Assistant can see becomes a source without new code. Advisory only: no control decision
  depends on it.
- **Charging session history** — every controlled session recorded to a local SQLite file: when it ran, which strategy drove it, and how much of the energy came from solar, the grid and the home battery.
- **Background service** — runs unattended as a long-lived process (e.g. systemd service / Windows Service), and can be **stopped gracefully from the web UI or Home Assistant** — the charger released, the open session closed and written — instead of being killed. It stays stopped until you start it again, while a reboot or a power cut still brings it straight back; see [Stopping and starting the controller](deploy/README.md#stopping-and-starting-the-controller).
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
- [Blazor](https://learn.microsoft.com/aspnet/core/blazor/) (interactive server rendering) for the optional self-hosted UI

## Project structure

The solution is organized to keep domain/control logic testable and free of hardware and hosting concerns:

```
Gleanvolt.slnx
├── src/
│   ├── Gleanvolt.Core/                 # Domain logic and hardware abstractions
│   │   ├── Models/                 # Strongly typed models (EnergyState, DeviceConfig, ...)
│   │   ├── Enums/                  # Register addresses, charger modes, inverter control values
│   │   ├── Strategies/             # Pure decision logic (charging controller, discharge hold, smoothing)
│   │   └── Interfaces/             # IModbusClient, IChargingController, IBatteryDischargeControl, ...
│   │
│   ├── Gleanvolt.Infrastructure/       # External communication
│   │   ├── Modbus/                 # Concrete Modbus TCP client (and a read-only decorator)
│   │   ├── RegisterMaps/           # Hex address mappings for SolaX Gen4 and EV Charger
│   │   ├── Sessions/               # SQLite charging-session store and its JSON contract
│   │   ├── Solcast/                # Solar-forecast HTTP client
│   │   └── Vehicles/               # The EV telemetry JSON contract and its parser
│   │
│   ├── Gleanvolt.Web/                  # The optional self-hosted UI (a Blazor component library)
│   │   ├── Components/             # Pages, layout and the root document
│   │   ├── wwwroot/                # Stylesheet and other assets, served from the library
│   │   └── WebOptions.cs           # The "Web" configuration section
│   │
│   ├── Gleanvolt.Hosting/              # The composition root — everything the controller is
│   │   ├── GleanvoltHostingExtensions.cs  # AddGleanvolt() / UseGleanvolt()
│   │   ├── PollingService.cs  # The main background loop (IHostedService)
│   │   ├── NoListenServer.cs       # The "server" used when the UI is switched off
│   │   ├── Configuration/          # Options classes bound from appsettings.json
│   │   ├── Forecasting/            # The day plan and its runtime settings
│   │   ├── HomeAssistant/          # MQTT discovery and the HA worker
│   │   ├── Sessions/               # Charging-session recording worker
│   │   └── Vehicles/               # EV telemetry MQTT subscriber
│   │
│   └── Gleanvolt.Worker/               # The executable host, and nothing else
│       ├── Program.cs              # .env, Serilog, AddGleanvolt(), the exit code
│       ├── DotEnv.cs               # Secrets from an untracked .env, before configuration is built
│       └── appsettings.json        # The shipped defaults
├── tests/
│   ├── Gleanvolt.Core.Tests/           # Unit tests for the control logic (mocking hardware)
│   ├── Gleanvolt.Infrastructure.Tests/ # Register encoding and write-path tests
│   ├── Gleanvolt.Web.Tests/            # Component rendering (bUnit) and options binding
│   └── Gleanvolt.Hosting.Tests/        # Coordinator, selector and HA discovery tests
├── deploy/                         # Raspberry Pi production stack (compose, broker config, deploy.sh)
├── dev/homeassistant/              # Local HA + MQTT dev stack (anonymous broker, host-run worker)
├── Dockerfile                      # Cross-compiled linux/arm64 image for the Pi
└── docs/                           # DECISIONS.md, IMPLEMENTATION_LOG.md (see below)
```

### Layering rules

- **Dependency direction is one-way:** `Gleanvolt.Worker` → `Gleanvolt.Hosting` → `Gleanvolt.Infrastructure` → `Gleanvolt.Core`. `Gleanvolt.Core` must never reference anything above it.
- **`Gleanvolt.Core` has no hardware or framework dependencies.** No Modbus libraries, no `Microsoft.Extensions.Hosting` types — only plain models, enums, and interfaces (`IModbusClient`, `IChargingController`, `IBatteryDischargeControl`). This is what keeps control/decision logic unit-testable without real hardware.
- **All decision-making logic lives in `Gleanvolt.Core`**, expressed against interfaces. Charging strategy, surplus calculations, and SOC-based rules belong here, not in `Gleanvolt.Infrastructure`, `Gleanvolt.Hosting` or `Gleanvolt.Worker`.
- **`Gleanvolt.Infrastructure` only implements `Gleanvolt.Core` interfaces.** Modbus TCP details and register maps stay isolated here; no business/decision logic.
- **`Gleanvolt.Hosting` is composition-only.** `AddGleanvolt()` wires up DI; `PollingService` orchestrates the poll/act loop by calling into `Gleanvolt.Core` abstractions — it should not contain control logic itself.
- **`Gleanvolt.Worker` is a host and nothing else.** The `.env` load, the logging configuration and the exit code. Anything it grows that a second host would also need belongs in `Gleanvolt.Hosting` instead — which is why it references that assembly alone and cannot reach `Gleanvolt.Core` directly.
- **`Gleanvolt.Web` references `Gleanvolt.Core` and nothing else.** It is a reporting/control *surface*, exactly like the Home Assistant integration: it reads `ChargeControlStatusHolder` and drives the Core selector interfaces, and owns no decision logic. `Gleanvolt.Hosting` hosts it; the dependency never runs the other way.
- **`Gleanvolt.Core.Tests` mocks the hardware boundary** (`IModbusClient`, etc.) to exercise control logic without a live device.

### The libraries as packages

Each `v*` tag produces a [GitHub Release](https://github.com/mpospisil/gleanvolt/releases) carrying self-contained builds for Windows, Raspberry Pi and x64 Linux — no .NET installation needed — alongside the four libraries as `.nupkg` files ([`release.yml`](.github/workflows/release.yml)). `Gleanvolt.Worker` is not packaged: it is the thing that runs the libraries, not one of them.

The packages are attached to the release rather than pushed to a feed. To build on the controller directly, take this repository as a git submodule and reference the projects — no feed, no credentials, and the submodule commit pins the version exactly.

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddGleanvolt();          // polling, control, sessions, Home Assistant, the web UI

var app = builder.Build();
app.UseGleanvolt();              // the UI's endpoints, when it is enabled
app.Run();
```

`AddGleanvolt` also has an `IServiceCollection` overload taking an `IConfiguration`, for a host that is not built on `WebApplicationBuilder`.

## Getting started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Network access to the SolaX inverter and EV charger with Modbus TCP enabled

### Build and run

```bash
dotnet build Gleanvolt.slnx
dotnet test Gleanvolt.slnx
dotnet run --project src/Gleanvolt.Worker
```

Set your device addresses first (see [Configuration](#configuration)). On a first run nothing is
written to either device: the service always boots with the charge mode **Off** and the battery hold
**off**, and `BatteryHold:Enabled` is `false` as well, so it only polls and logs. That is the
recommended way to confirm the telemetry looks right before enabling anything that writes.

## Deployment

For unattended operation the whole system runs on a Raspberry Pi (Raspberry Pi OS Lite, 64-bit) as
Docker containers. **There are two deployment workflows, and how much RAM the Pi has decides which
one you want** — Home Assistant is the expensive part; the controller and its own web UI are not.

| | **A — Full stack** | **B — Controller only** |
|---|---|---|
| **RAM required** | **2 GB minimum, 4 GB recommended** | **1 GB is enough** |
| Containers | controller, broker, Home Assistant | controller |
| Control surfaces | Home Assistant `:8123` **and** web UI `:8090` | web UI `:8090` |
| Required config | site addresses, `HOMEASSISTANT_ENABLED`, MQTT credentials | site addresses only |

```bash
./deploy/deploy.sh                    # A: controller + Home Assistant + broker
./deploy/deploy-controller-only.sh    # B: controller alone, with its own web UI on :8090
```

Home Assistant reserves 600 MB of the full stack's 848 MB and really uses most of it, so on a 1 GB
board workflow A commits 94% of the machine before the OS gets a look in. It runs, but with no
headroom — prefer **B** there. Nothing is lost by doing so: the controller's
[own web UI](#self-hosted-web-ui-the-web-section) drives every control Home Assistant would, and
shows telemetry, charging-session history and the forecast plan. Switching later is a matter of
running the other script.

The Pi never builds anything: CI builds the image and pushes it to GHCR
(`ghcr.io/mpospisil/gleanvolt`), and the Pi pulls it. That one name is a multi-platform
manifest list covering **`linux/arm64`** (the Pi), **`linux/amd64`** (an x64 Linux host) and
**Windows Nano Server ltsc2022**, so the same tag pulls the right image everywhere; Windows needs
`Controller:TimeZone` set, for the reason given under [Configuration](#configuration). All state
lives on bind mounts under
`/opt/gleanvolt` — including the charging-session database (`data/sessions.db`) and the log files — so
the containers are disposable: upgrades, rollbacks and `docker compose down` lose nothing. The broker
requires authentication and is not published to the LAN.

Deploying writes **nothing** to your hardware: charge control boots in mode `Off` and takes control
only when you select a mode — Home Assistant or the web UI, whichever is enabled — and the battery
hold stays disabled and dry-run until
you turn it on deliberately.

**Updating a Pi that is already running is the same command.** From the developer machine, re-run
the script for the workflow already deployed:

```bash
./deploy/deploy.sh                              # newest build of main
IMAGE_TAG=1.0.0 ./deploy/deploy.sh              # a released version -- note: no "v"
IMAGE_TAG=sha-abc1234 ./deploy/deploy.sh        # one specific build; also how you roll back
```

It copies the compose files, pulls, and recreates only the containers that actually changed. All
state survives — `.env` is never even copied, and the session database, logs and Home Assistant's
configuration live on bind mounts under `/opt/gleanvolt` rather than inside any container. Two things
worth knowing: the pull covers *every* image in the stack, so on workflow A Home Assistant moves
with it, and the controller restarts into charge mode `Off`, so an active mode has to be selected
again afterwards. Full detail, including settings-only changes and rollbacks, is under
[Updating a running deployment](deploy/README.md#updating-a-running-deployment).

Full instructions — choosing between the two workflows, preparing the Pi, storage, backup/restore
and troubleshooting — are in **[deploy/README.md](deploy/README.md)**.

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

All settings live in `src/Gleanvolt.Worker/appsettings.json`; secrets are supplied out-of-band (see
below). Device addresses and the poll cadence sit in the `Solax` section:

```jsonc
"Solax": {
  "PollIntervalSeconds": 5,   // one poll/decide cycle per this many seconds
  "Inverter":  { "Host": "192.168.2.10",  "Port": 502, "UnitId": 1 },
  "EvCharger": { "Host": "192.168.2.6", "Port": 502, "UnitId": 1 }
}
```

The `Controller` section holds the settings that belong to no single feature:

```jsonc
"Controller": {
  "TimeZone": ""   // "" = ask the OS; an IANA or Windows zone id overrides it
}
```

`TimeZone` is the zone every *local* decision is made in: the forecast day boundary, the daily loan
budget reset, and the zone id recorded on each charging session. Leave it empty on Linux, where the
`TZ` environment variable already sets it — that is what the deploy stack does. **Set it explicitly
on Windows**, where .NET ignores `TZ` and the process would otherwise run in UTC; on the Nano Server
image it must be a Windows id (`Central Europe Standard Time`), because resolving IANA ids needs ICU
and Nano Server has none. An id that cannot be resolved stops the worker at startup rather than
quietly reverting to UTC.

The feature sections — `Solcast`, `ChargeControl`, `BatteryHold`, `SessionStore` and `HomeAssistant` —
are documented in the subsections that follow.

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

- **.NET user-secrets** — the `Gleanvolt.Worker` project has a `UserSecretsId`, so this works in Development too:

  ```bash
  cd src/Gleanvolt.Worker
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
TrajectoryFloor = 100 − (remaining surplus × efficiency ÷ capacity) × 100
SocFloor      = max(TrajectoryFloor, MinBatterySocFloorPercent)
```

The evening guarantee needs no scheduling code: as the day burns down, the remaining surplus falls, so
`SocFloor` climbs toward 100 % and squeezes the car out of the late afternoon by itself. A
`FinalGuardBefore` window (default 1 h) pauses the car outright as a backstop.

##### Not flapping on the floor

The floor is where the mode is easiest to get wrong. SOC arrives as **whole percent**, so a bare
`soc < floor` test turns on a single count — a morning that starts one percent above the floor would
otherwise stop and restart the car every few minutes, which is a contactor cycle and a vehicle wake
each time. Two things prevent that:

- **A resume margin.** Charging continues down to `SocFloor` itself, but a paused session only comes
  back at `SocFloor + FloorResumeMarginPercent` (default 5 %) — the SOC counterpart of
  `ResumeHysteresisWatts`. It is held at or above `HoldReleaseMarginPercent`, because a car allowed
  back before the auto-armed [battery hold](#battery-discharge-hold-writes-to-the-inverter) releases
  would simply pin SOC to the floor with the grid covering every dip.
- **The clamp and the trajectory are different events.** Falling through `TrajectoryFloor` puts the
  evening 100 % at risk and stops the session at once. Falling through the `MinBatterySocFloorPercent`
  clamp while the trajectory sits far below — the normal case on a sunny morning, when the sun could
  still recover a much deeper discharge — is a preference, not a physics problem, so it goes through
  the `MinRunTime` dwell timer like any other soft reason and the session is held at 6 A rather than
  cut short. Both floors are published, as `soc_floor` and `soc_floor_traj`.
- **A guard band, so the pack recovers instead of hovering.** Not flapping is not the same as getting
  better: a car that takes every watt the sun makes holds SOC *exactly* on the floor all morning, with
  the auto-armed hold sending every dip to the grid. So while SOC is inside the resume margin,
  `FloorGuardReserveWatts` (default 750) of surplus is withheld from the car and the battery lends
  nothing — a loan and a reserve at the same SOC would only cancel out. On the 9 kWh default that
  clears a 5 % band in about forty minutes and costs a three-phase session roughly one amp. Where the
  surplus is marginal it stops the session outright, which is the intent: the pack gets the whole
  surplus and the resume margin keeps the car off until it has climbed clear, so a morning that used to
  produce a dozen short cycles produces one long one. Only a *running* session is ever inside the band
  — a paused one is held below it by the resume margin itself.

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
    "FloorResumeMarginPercent": 5,   // SOC recovery required before a paused session restarts
    "FloorGuardReserveWatts": 750,   // surplus kept for the battery inside that band, 0 = off
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
- sensors: **Control state**, **Charger status** (Available / Charging / ChargePaused / …), **Solar power**, **Forecast solar power** and **Solar surplus**, **EV charging power** and **EV charging current** (actual draw), **Target/Active charging current** (setpoint), **Battery SOC**, **Battery power**, **Grid power** (positive = importing, negative = exporting), and **Battery hold target** (while the hold is enabled).
- forecast-plan sensors, populated while the **Forecasted** mode is driving: **Day outlook**,
  **Plan state**, **Charge window**, **EV energy budget**, **EV energy expected today**,
  **Projected shortfall**, **Required SOC floor**, **Trajectory SOC floor**, **Forecast remaining today**,
  **Tomorrow forecast**, **Forecast accuracy**, **Session energy**, **Battery loaned today** and
  **Battery loan power**. `Day outlook` and `Projected shortfall` are what a "not enough sun for the
  car today" notification automation keys off.
- numbers, settable at runtime: **Daily EV target** (kWh), **Session energy target** (kWh, 0 =
  unlimited), **Minimum battery SOC** (%) and **SOC resume margin** (%). Like the mode, changes don't
  persist across restarts.
- binary sensors: **Car connected** and **Charging now**.
- an availability topic, so HA marks the device unavailable if the controller stops.

The device also carries the running build as its **software version** (`1.0.0 (31bf347)`), shown on
HA's device page. That is device metadata rather than an entity, so it creates no row on any
dashboard and needs no automation. The same string is the first line of the worker's log at startup;
`0.0.0-dev` with no commit means a local build rather than anything CI published.

#### What each entity means

Entity names are kept short so a dashboard card stays readable — and Home Assistant has nowhere to put
a longer explanation anyway: hovering an entity shows its friendly name, and MQTT discovery has no
description or tooltip field. The meanings live here instead.

| Entity | Unit | What it means |
| --- | --- | --- |
| **Charge mode** | select | Which strategy drives the charger: `Off`, `Solar`, `Forecasted` or `FastNoBattery` (see the list above). Always starts at `Off` after a restart. |
| **Battery discharge hold** | switch | Stops the home battery serving household load, so the car charges from PV and grid while the battery can still charge from surplus. Shows the last command written successfully, not a read-back — the register can't be read, so a failed write shows up as the switch springing back to `OFF`. `FastNoBattery` arms it automatically; a mode never turns it off for you. |
| **Daily EV target** | kWh | How much energy the car should get on a normal day. The forecast plan measures its projected shortfall against this. Doesn't persist across restarts. |
| **Session energy target** | kWh | Stop charging once this much has gone into the car in one session (since it was plugged in). `0` means no limit. Doesn't persist across restarts. |
| **Minimum battery SOC** | % | The hard floor the forecast plan may never take the home battery below, however good the forecast looks. Doesn't persist across restarts. |
| **SOC resume margin** | % | How far above the floor the battery must recover before a paused session restarts — charging continues down to the floor itself, only coming back costs the margin. Raise it if the car starts and stops repeatedly on a marginal day. Never applied below the hold's release margin. Doesn't persist across restarts. |
| **Control state** | — | What charge control is doing right now. `Disabled`: no mode selected, the charger is the owner's. `Idle`: a mode is selected but not acting, most often because the charger's use-mode isn't Fast. `Charging`: a current is being commanded. `Paused`: the setpoint was dropped to the pause current, typically because the surplus fell below what the charger's 6 A floor needs. |
| **Charger status** | — | The charger's own state, straight from its register. `Available`: no car. `Preparing`: plugged in, not yet drawing. `Charging`. `SuspendedEv`: the *car* stopped the draw, usually at its own charge limit. `SuspendedEvse` / `ChargePaused`: the *charger* stopped it — what our pause write produces. `Finishing`: session closing. `Faulted` / `Unavailable`: not usable. |
| **Solar power** | W | PV production measured at the inverter, before the house, the battery or the car take any of it. |
| **Forecast solar power** | W | What the forecast expected the roof to be making **at this instant** — the direct counterpart to **Solar power**, so the two chart against each other. `0` when no forecast covers this moment: none fetched yet, the provider is down, or the instant is past the horizon. Published in every mode, not only `Forecasted`, because the comparison is how you decide whether to select that mode at all. |
| **Solar surplus** | W | PV left after household consumption, counting neither battery charging nor EV charging as house load — the power the car can take without importing or discharging. This is the **smoothed 3-minute average** the solar modes decide on, not the instantaneous value, so a passing cloud can't interrupt a session. `Unknown` when the selected mode doesn't decide on surplus. |
| **EV charging power** | W | What the charger reports the car actually drawing right now — not a setpoint. |
| **EV charging current** | A | Charging current derived from the charger's measured power, phase-aware: what the car really takes. A car may draw less than the setpoint allows, never more. |
| **Target charging current** | A | The current the controller decided on this cycle, before it was written. `Unknown` whenever it isn't charging — paused, idle, or in `Off`. |
| **Active charging current** | A | The charger's setpoint **read back** from the hardware: what it was actually left at. Compare it with the target — if they disagree for more than a poll or two, a write isn't landing (or the controller is in dry run). |
| **Battery SOC** | % | Home battery state of charge as the inverter reports it. 0–100% spans the capacity the pack will actually cycle — its usable energy, not its nameplate. |
| **Battery power** | W | Positive while the battery charges, negative while it discharges. With the discharge hold armed this should sit at or above roughly −60 W: a working hold still leaves a small standby trickle, but the pack is no longer serving the house. |
| **Grid power** | W | Positive while importing from the grid, negative while exporting. This is the opposite of the sign the SolaX register uses; it's negated on read so positive always means power flowing into the house. |
| **Battery hold target** | W | The power target commanded at the inverter's grid connection point to keep the battery out of house load: minus whichever is smaller, house load or PV. `Unknown` when no hold is armed. |
| **Car connected** | on/off | `ON` while a vehicle is plugged in — the charger reporting `Preparing`, `Charging`, `Suspended*`, `ChargePaused` or `Finishing`. Says nothing about whether the car is drawing. |
| **Charging now** | on/off | `ON` while *the controller* is commanding a charging current, as opposed to having paused or never taken control. This is our own decision, not the car's behaviour — a car can be plugged in and idle while this is `ON`. See **EV charging power** for what's actually flowing. |
| **Stop service** | button | Shuts the controller down gracefully: the charger is returned to its pause current, the open session is closed and written, and the store is flushed — none of which happens if the process is killed. **One-way from here.** The service is what speaks MQTT, so this device goes unavailable and nothing in Home Assistant can start it again; that needs a shell on the Pi (`docker compose start gleanvolt-controller`). Lives in the device's *Configuration* section rather than on the dashboard card, and only an exact `PRESS` on its topic triggers it. See [Stopping and starting the controller](deploy/README.md#stopping-and-starting-the-controller). |

The rest are populated only while the **Forecasted** mode is driving; in the other modes they report
nothing rather than stale numbers from a plan nobody is acting on.

| Entity | Unit | What it means |
| --- | --- | --- |
| **Day outlook** | — | How the rest of today looks for the car. `Surplus`: the day covers the house, the battery to 100% and the whole EV target. `Tight`: the car gets something, but less than its target. `Shortfall`: substantially less — the battery keeps priority. `NoChargeToday`: no window in which the car could charge at all. `Unknown`: no usable forecast. |
| **Plan state** | — | One line on why the day plan says what it says — the same explanation the log carries. |
| **Charge window** | — | The next stretch of today in which the surplus is forecast to clear the charger's minimum power for long enough to be worth starting. `none` when today offers no such window. |
| **EV energy budget** | kWh | How much of today's remaining sun the car may have: what's left once the house and a 100% battery by evening are served, then restricted to the periods where the surplus actually clears the charger's minimum power. The restriction matters because the car can't sip a budget slowly. |
| **EV energy expected today** | kWh | What the car can realistically receive in total today, including what it has already taken. |
| **Projected shortfall** | kWh | How far today's forecast falls short of the house plus a full battery plus the daily EV target. Above zero means the car won't get everything it wanted; the battery keeps priority regardless. |
| **Required SOC floor** | % | The SOC the battery must not fall below right now if the sun still to come is to return it to 100% by the evening deadline. It climbs towards 100% as the day runs out, which is what squeezes the car out of the late afternoon without any scheduling. Battery SOC dropping to this line is what arms the discharge hold automatically. |
| **Trajectory SOC floor** | % | The same figure before the **Minimum battery SOC** clamp is applied: what the forecast on its own says the battery could be drawn down to. Well below the floor in force on a sunny morning, equal to it once the day is short. The pair is the first thing to read when a session pauses — it says whether the car is being held back by the forecast or merely by your configured minimum, and the controller stops the session harder in the first case. |
| **Forecast remaining today** | kWh | Forecast PV still to come today, at the configured confidence band and already scaled by **Forecast accuracy**. |
| **Tomorrow forecast** | kWh | Tomorrow's forecast production. Purely informational: context for whether a shortfall today is worth waiting out. |
| **Forecast accuracy** | % | Actual production against forecast so far today. 100% is on the nose, above means the roof is beating the forecast. The rest of the day's forecast is scaled by this, and a sustained large miss makes the plan untrusted, so the mode falls back to live-solar behaviour. |
| **Session energy** | kWh | Energy delivered to the car since it was plugged in. Starts afresh when it's unplugged. |
| **Battery loaned today** | kWh | Energy the home battery has lent the car today so that a surplus below the charger's floor could still reach it. The loan is repaid from sun that would otherwise have been exported. Resets at local midnight. |
| **Battery loan power** | W | How much of what the car is drawing right now is being covered by the home battery rather than by live sun. Zero outside the `Forecasted` mode. |

#### Configuration

Disabled by default. Non-secret settings live in `appsettings.json`:

```jsonc
"HomeAssistant": {
  "Enabled": false,
  "BrokerHost": "localhost",
  "BrokerPort": 1883,
  "DiscoveryPrefix": "homeassistant", // HA's discovery prefix
  "BaseTopic": "solax",
  "DeviceId": "solax_controller",
  "DeviceName": "Gleanvolt",
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

### Vehicle telemetry (the `Vehicle` section)

The controller can read the **car's own** battery state — as distinct from the home battery the
inverter reports, and from the charger's view of what's plugged into it. Off by default:

```jsonc
"Vehicle": {
  "Enabled": false,
  "BrokerHost": "localhost",
  "BrokerPort": 1883,
  "Topic": "gleanvolt/vehicle/state",   // whatever your HA automation publishes to
  "MaxAge": "12:00:00"                  // past this, a reading is shown as stale
}
```

`Vehicle:Username` / `Vehicle:Password` are supported for an authenticated broker and are secrets —
supply them via `.env` or an environment variable (`Vehicle__Username`), never in `appsettings.json`.

This phase is **read-only**: nothing in `ChargeControl` or `BatteryHold` consumes it. It appears on the
web UI dashboard and nowhere else — in particular it is *not* republished to Home Assistant, since
Home Assistant is where it comes from.

#### It reads MQTT, not a car API

There is no integration with Volkswagen, Škoda, Tesla or anyone else in this codebase, deliberately.
Instead the controller subscribes to **one topic with one JSON schema**, and each car is adapted onto
that schema by a template or automation in Home Assistant:

```jsonc
{
  "captured_at":  "2026-08-17T10:44:23+00:00",   // required: the CAR's capture time
  "soc_percent":  28,                            // optional, 0-100
  "charge_time_remaining_minutes": 95,           // optional: the CAR's own estimate
  "charge_state": "charging",                    // optional: idle | charging | complete | unknown
  "plug_state":   "connected",                   // optional: connected | disconnected | unknown
  "source":       "id4"                          // optional, for display
}
```

Every source spells things differently — the VW EU Data Act portal emits
`CHARGE_STATE_CHARGING_HV_BATTERY`, `volkswagen_connect` emits `notReadyForCharging`, an OBD dongle
emits raw CAN. Doing that mapping in Home Assistant rather than here means **a second car costs no
code**: a Škoda Elroq, a Tesla, a Kia — anything Home Assistant can see becomes a source by copying
one automation.

Everything except `captured_at` is optional, and absent is a supported configuration rather than an
error. `captured_at` is required because it is the **car's** capture time, not the arrival time, and
without it staleness cannot be judged.

`charge_time_remaining_minutes` is the **car's own** estimate of how much longer it needs — it knows
its charge curve, its taper and its target, and nothing here does. Publish the key only when the car
actually reports it: `0` means "the car says it is finished", which is a different fact from "the car
didn't say", and the templates below omit the key rather than defaulting it. A value outside
0–10080 minutes is rejected as a broken template (usually seconds published as minutes) along with the
rest of that payload.

#### Publishing from Home Assistant

For a VW ID.4 via the [`volkswagen_connect`](https://github.com/rafaelhutter/ha-volkswagen-connect)
integration. Publish **retained**, so a controller restart is handed the last known reading instead of
waiting up to a quarter of an hour for the next one.

> **Two shapes, one automation.** Home Assistant accepts automations in two places, and they are
> indented differently — pasting the wrong shape fails validation, which is the most common way this
> step goes wrong. Use the second block below if you are working in the UI, which most people are.

In `configuration.yaml`, under a top-level `automation:` key:

```yaml
automation:
  - alias: Publish ID.4 state to Gleanvolt
    trigger:
      - platform: state
        entity_id:
          - sensor.id_4_pro_performance_battery
          - sensor.id_4_pro_performance_charging_state
          - sensor.id_4_pro_performance_plug
    condition:
      - "{{ states('sensor.id_4_pro_performance_battery') | int(-1) >= 0 }}"
    action:
      - service: mqtt.publish
        data:
          topic: gleanvolt/vehicle/id4/state
          retain: true
          payload: >-
            {% set cs = states('sensor.id_4_pro_performance_charging_state') %}
            {% set left = states('sensor.id_4_pro_performance_charging_time_left') %}
            {"captured_at": "{{ states('sensor.id_4_pro_performance_last_vehicle_report') }}",
             "soc_percent": {{ states('sensor.id_4_pro_performance_battery') | float(0) }},
             {% if left not in ['unknown', 'unavailable', 'none', ''] %}
             "charge_time_remaining_minutes": {{ left | float(0) }},
             {% endif %}
             "charge_state": "{{ 'charging' if cs == 'charging'
                                 else 'idle' if cs in ['notReadyForCharging','readyForCharging']
                                 else 'unknown' }}",
             "plug_state": "{{ states('sensor.id_4_pro_performance_plug') }}",
             "source": "id4"}
```

Or in the UI — **Settings → Automations & scenes → + Create automation → Create new automation**, then
the **⋮ menu → Edit in YAML**. That editor holds one automation's *body*, so there is no `automation:`
key and no leading `- `, and everything sits four spaces further left:

```yaml
alias: Publish ID.4 state to Gleanvolt
trigger:
  - platform: state
    entity_id:
      - sensor.id_4_pro_performance_battery
      - sensor.id_4_pro_performance_charging_state
      - sensor.id_4_pro_performance_plug
condition:
  - "{{ states('sensor.id_4_pro_performance_battery') | int(-1) >= 0 }}"
action:
  - service: mqtt.publish
    data:
      topic: gleanvolt/vehicle/id4/state
      retain: true
      payload: >-
        {% set cs = states('sensor.id_4_pro_performance_charging_state') %}
        {% set left = states('sensor.id_4_pro_performance_charging_time_left') %}
        {"captured_at": "{{ states('sensor.id_4_pro_performance_last_vehicle_report') }}",
         "soc_percent": {{ states('sensor.id_4_pro_performance_battery') | float(0) }},
         {% if left not in ['unknown', 'unavailable', 'none', ''] %}
         "charge_time_remaining_minutes": {{ left | float(0) }},
         {% endif %}
         "charge_state": "{{ 'charging' if cs == 'charging'
                             else 'idle' if cs in ['notReadyForCharging','readyForCharging']
                             else 'unknown' }}",
         "plug_state": "{{ states('sensor.id_4_pro_performance_plug') }}",
         "source": "id4"}
mode: single
```

`alias` is the automation's name, so Home Assistant fills that in for you on save.

Then set `Vehicle:Topic` to `gleanvolt/vehicle/id4/state`. The `condition` matters: it stops a payload
being published while the integration's entities read `unavailable`, which happens whenever its cloud
session expires.

**Check the remaining-time entity name against your own install** — it varies by integration and by
car, and `volkswagen_connect` does not expose one on every model. If yours has no such sensor, delete
the `{% if %}` block: the guard already omits the key when the entity is missing, and a feed that never
publishes it is a supported configuration, not a fault.

**Don't wait for the trigger to prove it works.** It fires on a *state change* of those three sensors,
and a parked car may not produce one for hours. Publish immediately with the automation's
**⋮ → Run actions**, which skips both the trigger and the condition. The controller logs the result:

```
[22:56:17 INF] First vehicle reading from gleanvolt/vehicle/id4/state:
               SOC=28% charge=Idle plug=Disconnected captured 2026-08-17T10:44:23+00:00
```

If instead you see `Ignoring vehicle telemetry ... 'soc_percent' was a JSON String, expected a number`,
the source entity was `unavailable` when the payload was built — which is what the `condition` prevents
on a real trigger but cannot prevent when you force it with *Run actions*.

Note the capture time comes from **`last_vehicle_report`**, not that integration's `data_captured`
sensor — the latter belongs to its EU Data Act source and reads `unknown` until that is separately
configured.

#### Why it is advisory only, and always will be

Measured on the reference install, not assumed:

- A parked car's report was **2 hours** old on one reading and **3.5 hours** on another. Hours stale is
  the normal case, not a fault — and not a problem either, since a parked car's SOC does not drift.
- The upstream cloud session **expired after ~15 hours**, taking every entity to `unavailable`.
  Recovery needed a human re-entering a password plus an email OTP.
- **Target SOC is not reliably available at all**: the portal's field is null unless the car has an
  active charge plan, and the EU Data Act equivalent needs a separate request that can take days to
  start delivering.

So `MaxAge` exists to catch a **dead feed**, not to reject merely old numbers, and a charge target
stays a Gleanvolt setting rather than something read from the car. Anything that writes to hardware
must behave identically when this feed is absent, stale, or gone — which in this phase is trivially
true, because nothing consumes it yet.

### Self-hosted web UI (the `Web` section)

The controller can serve its own UI, as an alternative to Home Assistant or alongside it. It exists
because on a 1 GB board Home Assistant is the binding constraint — it alone reserves 600 MB of the
roughly 905 MB available, which a 4 GB board no longer feels — and because a controller that can be
looked at without a
second application is simpler to reason about. The two surfaces are independent adapters over the
same internal state, so all four combinations run: UI only, MQTT only, both, neither.

**What is built so far.** [Issue #44](https://github.com/mpospisil/gleanvolt/issues/44) lands
in phases. Phase 0 is the plumbing: `/health` shows the running build, the configured time zone, and
the time of the last completed poll — a liveness check (the timestamp updates itself as each poll
lands, so a page that sits still means the poll loop has stopped while the web host is fine).
Phase 1 adds `/`, a read-only telemetry dashboard: charge mode, control state, charger status, car
connected, solar power and surplus, battery SOC and power, grid power, EV charging power and current,
and target/active current — each with its meaning inline, since MQTT discovery has nowhere to put one
(see [What each entity means](#what-each-entity-means) above; the wording is the same). Phase 2 adds
optional authentication — a single shared password that gates every page once one is configured, see
[Authentication](#authentication) — landing before phase 3 gives the UI anything that can write to
hardware.

Phase 3 adds the same controls Home Assistant has, on the same page: the **charge mode** select
(`Off` / `Solar` / `Forecasted` / `FastNoBattery`), the **battery discharge hold** switch — shown
only while `BatteryHold:Enabled` is on — and the runtime numbers (**daily EV target**, **session
energy target**, **minimum battery SOC**). They drive the exact same Core interfaces the MQTT worker
uses (`IChargeControlModeSelector`, `IBatteryHoldSelector`, `IForecastRuntimeSettings`), so there is
no second control path and the two surfaces cannot disagree about what the charger is doing — the
last one to write wins, visible on the other within a poll interval. The same semantics apply here as
on the MQTT side: nothing set from this page persists across a restart, `FastNoBattery` can switch
the mode back to `Off` on its own once the car finishes (the select follows it), and the battery-hold
switch shows the last command that was actually written to the inverter, not what was requested — a
write that fails to take shows the switch springing back on its own.

Phase 5 adds `/forecast`: the `Forecasted` mode's day plan as one coherent view instead of the dozen
loosely related entities Home Assistant renders it as. The same eleven figures — day outlook, plan
state, charge window, EV energy budget, EV energy expected today, projected shortfall, required SOC
floor, forecast remaining today, tomorrow's forecast, forecast accuracy, battery loaned today — each
with an explanation next to it, plus a timeline chart plotting forecast surplus against the charge
window, with the required-SOC-floor projection overlaid on a second axis. The chart's data is
computed once, in `Gleanvolt.Core`, by the same `SolarDayPlanner` that builds the plan itself — the floor
projection is the identical formula the live figure uses, evaluated at every remaining forecast
period instead of only the current instant — so the picture can never disagree with the numbers next
to it. Like the MQTT entities, the whole page shows an explicit empty state while any mode other than
`Forecasted` is driving, rather than the last stale plan.

The `/health` page also carries the one control that isn't about charging: **Stop service**, which
shuts the whole controller down gracefully — the charger returned to its pause current, the open
charging session closed and written, the session store flushed, Modbus and MQTT closed — rather than
leaving it to be killed, which revokes nothing and leaves the car drawing at the last current we
wrote. It takes two clicks, because it is one-way from the browser: the UI goes down with the
service, and only a shell on the Pi (`docker compose start gleanvolt-controller`) brings it back. See
[Stopping and starting the controller](deploy/README.md#stopping-and-starting-the-controller) for the
exit-code contract that keeps it stopped without also breaking restart-after-reboot. Note that the
button is as reachable as the rest of the UI: with no password configured, anyone on the LAN can stop
the controller — one more reason to consider [Authentication](#authentication) below.

```jsonc
"Web": {
  "Enabled": true,      // master switch; while false the process binds no socket at all
  "Port": 8090          // listens on every interface, plain HTTP
  // "RequireAuthentication"  // unset -- follow the password; see below, rarely worth setting
  // "PasswordHash": ""       // secret -- see below, never set it here
}
```

- **On by default, and needs no configuration.** Start the controller — from `dotnet run`, from the
  image, from the compose stack — and the UI is at `http://<host>:8090`. This is the surface a fresh
  install is operated through, and unlike the Home Assistant integration it needs no broker, no
  credentials and no onboarding to be useful, so there is nothing to gain by making it opt-in.
- **No login until you configure one.** See [Authentication](#authentication) below: it is an
  advanced option, off out of the box, and turning it on is a single setting.
- **`Web:Enabled=false` means nothing is listening** — not "listening but empty". An ASP.NET host
  would otherwise fall back to a default port; this one installs a server that binds nothing, so with
  the UI off the process is the same headless worker it has always been. `ss -ltnp` shows no socket
  inside the container. (In the compose stack the *host* port stays published either way — see
  [Deployment](#deployment) — so connections are refused rather than never accepted.)
- **Plain HTTP**, deliberately: this is a LAN appliance, and terminating TLS in front of it (or not)
  is the operator's decision rather than the controller's.

#### Authentication

**There is no login by default.** The UI serves every page to anyone who can reach the port. That is
the right default for a LAN appliance — it is what makes the thing work the moment it starts, with no
secret to generate first — and the wrong one if that LAN has guests on it, or if the port is reachable
from anywhere beyond it. Which of those you have is something only you know, so it is a setting rather
than an assumption.

Turning the login on is **one setting**: configure `Web:PasswordHash` and every page — including the
read-only dashboard — starts redirecting anonymous visitors to a login form. There is no second
switch to remember, and therefore no way to set a password and have it quietly not enforced. There is
also no per-user account: one shared password gates the whole UI, hashed with ASP.NET Core's
`PasswordHasher`, matching a LAN appliance with one or two operators rather than a multi-tenant
system.

The hash is a secret and must never live in `appsettings.json` — supply it via `.env` / an
environment variable, exactly like the MQTT broker credentials:

```
Web__PasswordHash=<hash>
```

Generate one with the worker binary itself, or with the image, without configuring anything (it
prints the hash and exits — no listening socket involved):

```bash
dotnet Gleanvolt.Worker.dll hash-password '<your password>'
docker run --rm ghcr.io/mpospisil/gleanvolt:latest hash-password '<your password>'
```

`Web:RequireAuthentication` overrides that inference in either direction and is rarely worth setting:

| `RequireAuthentication` | `PasswordHash` | Result |
|---|---|---|
| unset (default) | not set | UI served openly; a warning is logged at every startup |
| unset (default) | set | login required — **the normal way to protect the UI** |
| `true` | set | login required; identical to the row above |
| `true` | not set | **host refuses to start** — nobody could ever sign in |
| `false` | either | UI served openly even with a password configured; warning logged |

The one refusal is the combination that cannot be honoured: a login demanded with nothing to check it
against would leave the UI permanently unreachable, and a host that stops says so immediately where a
running one looks like a broken page. Everything else starts.

#### Deployment

The compose stack in [`deploy/`](deploy/) needs **nothing in `.env`** for the UI: `docker-compose.yml`
publishes port 8090 and leaves `Web__Enabled` at its default, so a fresh Pi ends a deploy at a working
`http://<pi>:8090`. See
[deploy/README.md § Running without Home Assistant](deploy/README.md#running-without-home-assistant-controller--web-ui-only)
for the memory budget of running the controller and its UI **without** Home Assistant or an MQTT
broker at all (roughly 200 MB against 848 MB for the full stack — 22% of a 1 GB board, or 5% of a
4 GB one).

The host port is published unconditionally, which is the one thing to understand about
`WEB_ENABLED=false` there: the port stays bound on the Pi, but nothing inside the container listens,
so connections are refused. Earlier releases published it from a separate `docker-compose.web.yml`
that had to be merged via `COMPOSE_FILE`; that second step is gone, and the file is kept only so an
existing `COMPOSE_FILE` line doesn't break — merging it is now a no-op.

#### Browsing charging session history

Phase 4 adds `/sessions`: a list of recorded sessions (date, duration, driving strategy, energy
delivered, solar share), each linking to a detail page with the per-source energy split and a
battery-SOC-over-time chart. It reads through `IChargingSessionStore`'s existing query methods —
nothing here reaches past the interface into SQLite — and degrades to "isn't available right now"
rather than an error page when `SessionStore:Enabled` is off or the file can't be opened. See
[Charging session history](#charging-session-history-the-sessionstore-section) below for what is
actually recorded.

The chart uses [uPlot](https://github.com/leeoniya/uPlot) (MIT licensed), vendored into
`Gleanvolt.Web/wwwroot/lib/` rather than fetched from a CDN — issue #44's decision, so the history stays
readable during an internet outage, which is exactly when a locally controlled system is most worth
looking at.

The published container image is now based on `dotnet/aspnet` rather than `dotnet/runtime` — about
25 MB more, on every platform, whether or not the UI is enabled. The framework reference is fixed at
build time, so there is no variant that avoids it.

### Charging session history (the `SessionStore` section)

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

#### What a session is

A session **opens** when a controlling mode (`Solar`, `Forecasted`, `FastNoBattery`) is driving a
connected car, and **closes** when that stops being true — the mode returns to `Off`, the controller
ends itself because the car is full, the car is unplugged, or the service stops.

Switching mode mid-session does **not** start a new one. "Forecasted all afternoon, then
`FastNoBattery` at 17:00" is one story about one car, and it is recorded as one session with a
`ModeChanged` event in it.

Note this is a *different* span from the one `Session energy` reports in Home Assistant, which counts
from the moment the car was plugged in whether or not anything is controlling it.

#### What is recorded

| | |
|---|---|
| **Session header** | start/end time (UTC, plus the IANA zone so a viewer can bucket by local day), start and end mode, why it ended, start/end SOC, peak power, the totals below, and the forecast day plan as it stood at the start |
| **Totals** | energy delivered, split into **from solar / from grid / from battery**, plus what the forecast mode *commanded* the battery to lend |
| **Samples** | every 30 s and on every change: all meters, the four charging figures below, the smoothed surplus, the loan, the hold, the forecast power at that instant, plan figures, the running totals, and the site-wide progress figures below |
| **Events** | mode changed, charging started/paused, setpoint changed, hold armed/released, plan fell out of trust, session ended — each with the controller's own reason string |

#### The four charging figures, and why they are kept apart

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

#### Progress against the site, and against the car

The totals above are all about the car: what it received, and which source each watt is attributed to.
Three more are integrated over the same window and answer a different question — what the *site* did
while the car was charging — plus the car's own view of itself:

| Field | What it is |
|---|---|
| `SolarWh` | PV **produced on site** since the session opened. Not the same as `FromSolarWh`: the difference is what the house and the home battery took. |
| `ForecastSolarWh` | What the forecast expected the roof to make over that same window. Against `SolarWh` it is the session's own forecast-versus-reality line. `null` — not `0` — while no forecast has been available for any part of the session. |
| `GridImportWh` | Energy the **whole site** imported, export excluded rather than netted off, so a sunny hour can't cancel out an expensive evening. Again distinct from `FromGridWh`, the car's attributed share. |
| `VehicleSocPercent` | The **car's own** battery SOC from the [vehicle feed](#vehicle-telemetry-the-vehicle-section), or `null` when none is configured or nothing has arrived. Advisory: nothing in charge control reads it. |
| `VehicleChargeTimeRemainingMinutes` | How much longer the **car itself** reckons it needs, from `charge_time_remaining_minutes`. **`0` when the car did not report it** — not an error, just a feed that doesn't publish it. Because `0` is also a legitimate reading ("done"), never read this without the flag below. |
| `VehicleChargeTimeRemainingReported` | Whether that `0` is the car's answer or the absence of one. `false` means no estimate was provided. |
| `VehicleSocCapturedAt` | When the *vehicle* produced that reading. Stored next to it because it routinely lags by hours — a SOC without its capture time cannot be told from a live one, and a chart would silently flatten. A feed that goes quiet mid-session leaves the last reading standing with a visibly ageing capture time rather than blanking the column. |

These arrived in database schema **v2**; a v1 file is migrated in place on startup and its existing
rows keep `0` for the three totals, which is the truth for them — nothing integrated those quantities
at the time. Published documents move to `schemaVersion` 2 for the same reason, purely additively.

#### How the energy is attributed to a source

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

#### Operational notes

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

#### Getting the data out

A closed session is immutable, which makes it safe to publish as one self-contained document —
`ChargingSessionDocument`, carrying a `schemaVersion`, the header, every sample and every event. That
shape is deliberately independent of the database's own tables, so uploading sessions to cloud object
storage (one object per session) can be added later without migrating anything. The
`sessions.synced_at` column is reserved for exactly that. Reading them from a web app is already
built — see [Browsing charging session history](#browsing-charging-session-history) above.

## License

Licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE).

**Free for any noncommercial purpose**, which includes running it on your own home installation,
hobby and amateur use, and use by charities, schools, public research bodies and government
institutions. No registration, no key, no telemetry — clone it, build it, run it.

**Commercial use requires a separate licence** from the copyright holder. If you install, operate or
resell this for clients, bundle it with hardware, or run it as part of a business, please
[open an issue](https://github.com/mpospisil/gleanvolt/issues) or email m.pospisil76@gmail.com
— it is usually a short conversation.

This applies to the published container images too: they carry the licence at `/app/LICENSE` and the
`org.opencontainers.image.licenses` label, and pulling one is not a commercial licence.

Source code up to and including commit `00de500` (tag `mit-final`) was published under the MIT License
and **remains available under it** ([LICENSE-MIT](LICENSE-MIT)) — a licence change cannot withdraw
permissions already granted. The new terms apply to everything released after that commit.

Third-party components keep their own licences, and nothing here restricts them — every dependency is
permissive (MIT or Apache-2.0), with the versions, copyright holders and full licence texts listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). That file ships inside the container images and the
packages too.

Contributions are welcome and need a sign-off; see [CONTRIBUTING.md](CONTRIBUTING.md) and
[CLA.md](CLA.md) for what that means and why this project asks for it.
