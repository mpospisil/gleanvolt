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
- **Fast charge without the battery** — one mode for "I leave in an hour": maximum current from PV and grid, the home battery held out of it, and back to `Off` by itself when the car is full — or when the amount you asked for has been delivered, said in kilowatt-hours or as a battery target. Tell it when you leave and it waits, starting as late as it can and still finish — so a car charged above 80% sits there for minutes rather than all night.
- **Targeted charging** — "15 kWh in the car by 17:00, and use as little grid as you can": the car is paced across the whole window at the rate the deadline needs, taking every watt of sun above that rate for free — because the charger can only use the sun that shines while it runs. Ask in kilowatt-hours, or, with a car that reports its own battery, in state of charge — "80% by seven". Either way the plan is put in front of you and started only when you confirm it. Choose **just in time** instead of cheapest and the last stretch is held back so the car reaches 100% shortly before you leave rather than sitting full all night — the preview names what that costs in grid before you commit to it.
- **Solar forecasting** — a cached [Solcast](https://solcast.com/) forecast for the site, logged against actual generation.
- **Home Assistant integration** over MQTT discovery, with runtime control and telemetry.
- **Self-hosted web UI** (on by default, no configuration — see [Self-hosted web UI](#self-hosted-web-ui-the-web-section) below) — a Blazor dashboard served by the controller itself at `http://<host>:8090`: live telemetry, every control Home Assistant has, charging-session history and the forecast plan, all with no Home Assistant or MQTT broker required. Both surfaces are first-class: run either, both, or neither, and [`deploy/`](deploy/) can run the controller with neither Home Assistant nor a broker on a 1 GB board, at roughly a quarter of the memory the full stack needs.
- **HTTP API, described by OpenAPI** (off by default) — the same telemetry, history, forecast and
  actions the other two surfaces have, for programs rather than people: read the energies and the
  car, ask what a targeted charge *would* do without starting one, and start it when the answer is
  right. Built so that an **MCP server** can hand the lot to an LLM as generated tools — see
  [HTTP API](#http-api-the-api-section).
- **Vehicle telemetry** — optionally reads the **car's own** battery SOC and range, either from MQTT
  (normalised, so any vehicle Home Assistant can see becomes a source without new code) or, for a VW
  Group car, from the manufacturer's **EU Data Act portal** on the controller's own schedule. It shapes
  what you *ask* for — a targeted charge can be stated as a battery percentage — and never how it is
  delivered: no control decision depends on it, and a feed that dies changes nothing about how the
  charger is driven.
- **Charging session history** — every controlled session recorded to a local SQLite file: when it ran, which strategy drove it, and how much of the energy came from solar, the grid and the home battery.
- **Energy history at 15-minute resolution** — a monitoring service that does nothing but record, to its own database: for every quarter hour of every day, how much the roof made, how much the forecast said it would, how much crossed the meter each way, how much the car took, and where the home battery sat. Charging or not, plugged in or not — the series analytics is built on, with a
  day-at-a-time viewer in the web UI.
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
- Minimal APIs and [`Microsoft.AspNetCore.OpenApi`](https://learn.microsoft.com/aspnet/core/fundamentals/openapi/overview) for the optional HTTP API

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
│   │   ├── Monitoring/             # SQLite energy-interval store (the analytics tables)
│   │   ├── Sessions/               # SQLite charging-session store and its JSON contract
│   │   ├── Solcast/                # Solar-forecast HTTP client
│   │   └── Vehicles/               # The EV telemetry JSON contract and its parser
│   │
│   ├── Gleanvolt.Api/                  # The optional HTTP API (minimal-API endpoints + its OpenAPI document)
│   │   ├── Contracts/              # The wire DTOs, owned here rather than shared with Core
│   │   ├── Endpoints/              # One file per group: status, energy, sessions, forecast, plans, control
│   │   └── ApiOptions.cs           # The "Api" configuration section
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
│   │   ├── Monitoring/             # Energy-interval monitoring worker
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
│   ├── Gleanvolt.Api.Tests/            # The endpoints over TestServer, and the OpenAPI contract snapshot
│   ├── Gleanvolt.Web.Tests/            # Component rendering (bUnit) and options binding
│   └── Gleanvolt.Hosting.Tests/        # Coordinator, selector and HA discovery tests
├── deploy/                         # Raspberry Pi production stack (compose, broker config, deploy.sh)
├── dev/homeassistant/              # Local HA + MQTT dev stack (anonymous broker, host-run worker)
├── Dockerfile                      # Cross-compiled linux/arm64 image for the Pi
└── docs/                           # DECISIONS.md, IMPLEMENTATION_LOG.md (see below),
                                    # VW_PORTAL_SETUP.md (reading the car from VW's portal)
```

### Layering rules

- **Dependency direction is one-way:** `Gleanvolt.Worker` → `Gleanvolt.Hosting` → `Gleanvolt.Infrastructure` → `Gleanvolt.Core`. `Gleanvolt.Core` must never reference anything above it.
- **`Gleanvolt.Core` has no hardware or framework dependencies.** No Modbus libraries, no `Microsoft.Extensions.Hosting` types — only plain models, enums, and interfaces (`IModbusClient`, `IChargingController`, `IBatteryDischargeControl`). This is what keeps control/decision logic unit-testable without real hardware.
- **All decision-making logic lives in `Gleanvolt.Core`**, expressed against interfaces. Charging strategy, surplus calculations, and SOC-based rules belong here, not in `Gleanvolt.Infrastructure`, `Gleanvolt.Hosting` or `Gleanvolt.Worker`.
- **`Gleanvolt.Infrastructure` only implements `Gleanvolt.Core` interfaces.** Modbus TCP details and register maps stay isolated here; no business/decision logic.
- **`Gleanvolt.Hosting` is composition-only.** `AddGleanvolt()` wires up DI; `PollingService` orchestrates the poll/act loop by calling into `Gleanvolt.Core` abstractions — it should not contain control logic itself.
- **`Gleanvolt.Worker` is a host and nothing else.** The `.env` load, the logging configuration and the exit code. Anything it grows that a second host would also need belongs in `Gleanvolt.Hosting` instead — which is why it references that assembly alone and cannot reach `Gleanvolt.Core` directly.
- **`Gleanvolt.Api` references `Gleanvolt.Core` and nothing else**, on exactly the same terms as
  `Gleanvolt.Web` below: it is a third reporting/control *surface*, it drives the same Core seams the
  other two do, and it owns no decision logic. Composing a targeted request is decision logic, which
  is why that moved into `Gleanvolt.Core` when this surface arrived rather than being written twice.
- **`Gleanvolt.Web` references `Gleanvolt.Core` and nothing else.** It is a reporting/control *surface*, exactly like the Home Assistant integration: it reads `ChargeControlStatusHolder` and drives the Core selector interfaces, and owns no decision logic. `Gleanvolt.Hosting` hosts it; the dependency never runs the other way.
- **`Gleanvolt.Core.Tests` mocks the hardware boundary** (`IModbusClient`, etc.) to exercise control logic without a live device.

### The libraries as packages

Each `v*` tag produces a [GitHub Release](https://github.com/mpospisil/gleanvolt/releases) carrying self-contained builds for Windows, Raspberry Pi and x64 Linux — no .NET installation needed — alongside the five libraries as `.nupkg` files ([`release.yml`](.github/workflows/release.yml)). `Gleanvolt.Worker` is not packaged: it is the thing that runs the libraries, not one of them.

The packages are attached to the release rather than pushed to a feed. To build on the controller directly, take this repository as a git submodule and reference the projects — no feed, no credentials, and the submodule commit pins the version exactly.

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddGleanvolt();          // polling, control, sessions, Home Assistant, the web UI, the API

var app = builder.Build();
app.UseGleanvolt();              // the UI's and the API's endpoints, when they are enabled
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

Home Assistant uses around 500 MB in steady state — half of a 1 GB board before the OS and page cache
get a look in, against about 75 MB for the controller and its UI. Workflow A runs there, but with no
headroom — prefer **B** on 1 GB. Nothing is lost by doing so: the controller's
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
only when a charging button is pressed — Home Assistant or the web UI, whichever is enabled — and the
battery hold stays disabled and dry-run until
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
with it, and the controller restarts into charge mode `Off`, so charging has to be started
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
below). **The installation itself — where the array is and which boxes it is made of — is described
in one place, the `Pv` section**, documented in [The PV system](#the-pv-system-the-pv-section) below.
The vendor-named `Solax` section is gone; the poll cadence it used to hold is a controller setting.

The `Controller` section holds the settings that belong to no single feature:

```jsonc
"Controller": {
  "TimeZone": "",           // "" = ask the OS; an IANA or Windows zone id overrides it
  "PollIntervalSeconds": 5  // one poll/decide cycle per this many seconds
}
```

`TimeZone` is the zone every *local* decision is made in: the forecast day boundary, the daily loan
budget reset, and the zone id recorded on each charging session. Leave it empty on Linux, where the
`TZ` environment variable already sets it — that is what the deploy stack does. **Set it explicitly
on Windows**, where .NET ignores `TZ` and the process would otherwise run in UTC; on the Nano Server
image it must be a Windows id (`Central Europe Standard Time`), because resolving IANA ids needs ICU
and Nano Server has none. An id that cannot be resolved stops the worker at startup rather than
quietly reverting to UTC.

`PollIntervalSeconds` is how quickly the controller reacts, not a fact about the hardware — the
inverter has no opinion about how often it is asked — which is why it outlived the section the device
addresses moved out of.

The feature sections — `Solcast`, `ChargeControl`, `BatteryHold`, `SessionStore` and `HomeAssistant` —
are documented in the subsections that follow.

### The PV system (the `Pv` section)

One section describes the installation: where the array is, what it faces, what it is made of, and what
to call it ([issue #111](https://github.com/mpospisil/gleanvolt/issues/111)). Everything that needs a
coordinate, a device address or a name reads it from here, so there is one answer rather than one per
feature.

```jsonc
"Pv": {
  "Id": "home-roof",               // stable slug: the system's identity, on the broker and upstream
  "Name": "Home Roof",             // what a human sees
  "Address": "Krásného 12, Praha", // display only, never parsed
  "Latitude": 50.0755,
  "Longitude": 14.4378,
  "AzimuthDegrees": 172,           // compass bearing: 0 = north, 90 = east, 180 = south
  "TiltDegrees": 35,               // 0 = flat, 90 = vertical
  "CapacityKwp": 9.2,              // peak DC capacity of the array
  "InverterCapacityKw": 8.0,       // optional; AC side, where it clips the array
  "LossFactor": 0.9,               // fraction of DC yield that reaches the meter
  "InstallDate": "2024-05-01",

  "Inverter": {
    "Model": "SolaX X3-HYB-G4 PRO",
    "Host": "192.168.2.10", "Port": 502, "UnitId": 1
  },

  "Chargers": [                    // a list; exactly one entry is supported
    {
      "Id": "charger",             // slug: the charger's identity within the system
      "Name": "Garage wallbox",
      "Model": "SolaX X1/X3-HAC",
      "Host": "192.168.2.6", "Port": 502, "UnitId": 1
    }
  ]
}
```

**`Model` is documentation, not a selector.** The register maps are compiled for the two devices in
[Hardware targets](#hardware-targets) and are chosen by nothing, so writing another model here does not
make the controller speak that device's dialect — it only mislabels the one it is speaking to. What it
does do is answer "what is on the other end of 192.168.2.10?" from a log line rather than an ssh
session. The day a second register map exists, this is the field that picks it.

**`AzimuthDegrees` is stored, not sent.** Nothing computes from it today: the forecast is fetched by
Solcast resource id, which already encodes the orientation inside your Solcast account. Before it is
ever passed to a provider, that provider's own azimuth convention has to be checked against the compass
bearing used here — an array described 180° from where it points still produces a plausible-looking
forecast, which is what makes this the one field that goes silently wrong. Written as `-90` or `270`,
it is stored the same way; the resolved value is always in `[0, 360)`.

**One charger, in a shape that can hold more.** `Chargers` is a list from the start so that the
configuration does not have to change shape the day the control logic can drive two — which it cannot
today: there is one charge mode, one set of Home Assistant controls and one surplus to divide. A second
entry is therefore **a startup failure**, not a silently ignored one.

**What is validated at startup.** A site that cannot be described stops the worker with every problem
listed at once, each naming its key: an id that is not a slug, a latitude without a longitude, a tilt
outside 0–90, a loss factor outside (0, 1], an unparsable install date, a missing inverter or charger
address, two chargers, or two chargers sharing an id.

#### Keys that have moved

The `Solax` section is gone: an inverter and a wallbox are what a PV system is made of, and the one
setting left in it — the poll interval — is not a fact about the hardware but a choice about how often
we ask, so it belongs to the controller.

**A retired key that is still set stops the worker at startup**, naming its replacement in both
spellings. That is deliberate and it is the whole point: each of these decided something real, and a
build that ignored the old spelling would start, poll and charge — against a default, while your
configuration file names something else. That failure is invisible; a refused startup costs one
restart.

| Retired | Replaced by |
| --- | --- |
| `Solax:Inverter` | `Pv:Inverter` |
| `Solax:EvCharger` | `Pv:Chargers:0` |
| `Solax:PollIntervalSeconds` | `Controller:PollIntervalSeconds` |
| `Weather:Latitude` | `Pv:Latitude` |
| `Weather:Longitude` | `Pv:Longitude` |
| `HomeAssistant:DeviceName` | `Pv:Name` |

`Weather:ApiKey` and `Solcast:ResourceId` did **not** move: a provider's key and a provider's handle
for your roof belong beside that provider's other settings. The `Pv` section says what the array is; a
provider section says how to reach that provider about it.

Deployments are unaffected in the place it would cost most: `INVERTER_HOST` and `EV_CHARGER_HOST` keep
their names in `deploy/.env`, and the compose file maps them onto the new keys. See
[the upgrade note](deploy/README.md#upgrading-a-pi-deployed-before-the-pv-system-had-its-own-settings).

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

### Weather (the `Weather` section, optional)

Records **what the sky was actually doing** while a session ran, so a finished session can be read against the day it happened on rather than only against the forecast. Entirely optional and **off unless configured**: with no key and no coordinates the controller makes no weather calls at all, and every session is simply recorded without weather.

```jsonc
"Weather": {
  "BaseUrl": "https://api.openweathermap.org/",
  "Units": "metric",            // °C and metres; changing this changes what the stored numbers mean
  "RequestTimeout": "00:00:05"  // a slow provider is abandoned, never waited for
}
```

**Which site the weather is fetched for is [the `Pv` section's](#the-pv-system-the-pv-section) business**, not this one's: `Pv:Latitude` and `Pv:Longitude` are where the coordinates belong. `Weather:Latitude` and `Weather:Longitude` are retired, and setting either stops the worker at startup rather than being ignored.

The **API key is a secret**, on exactly the same terms as `Solcast:ApiKey` above — `.env`, an environment variable (`Weather__ApiKey`), or user-secrets. Deployments set `WEATHER_API_KEY` in `deploy/.env`, and the site's coordinates as `PV_LATITUDE` / `PV_LONGITUDE`.

**Two calls per charging session**, one when it opens and one when it closes, and none in between. There is no refresh worker and no cached forecast: weather is decoration on a record, not an input to any decision, so spending the provider's quota on hours when nothing is recording would buy nothing. Any free OpenWeatherMap plan covers a few sessions a day comfortably.

It uses the **current-weather endpoint** (`data/2.5/weather`), not One Call 3.0 — One Call needs its own paid subscription and answers `401` without one, and everything recorded here comes back from the free endpoint anyway. See [The weather a session ran in](#the-weather-a-session-ran-in) below for what is stored.

### EV charge control (writes to the charger)

When enabled, the worker drives the EV charger from **live solar surplus**, and only once the home battery is essentially full. Two things are written, by two different callers: the **charge-current setpoint**, by the control loop, on every cycle that calls for a change; and the charger's **use-mode**, once per action — `Fast` when a strategy is started, `Stop` when it is switched off (see "What is written, and by whom" below). It writes only current values that differ from what's already on the device and logs every change.

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

#### What is written, and by whom

Until #89 the controller wrote the current setpoint and nothing else: the owner was expected to have
left the charger in Fast by hand, and a mode selected over a charger sitting in Green did nothing at
all and said so only through **Control state** reading `Idle`. Charging is now started by an action
instead, and the action takes responsibility for the use-mode. The two writes stay separate:

- **The control loop writes the current setpoint (`0x628`) and only that**, from its
  `Surplus = PV − household load` calculation. It is still the minimum it can do.
- **An action writes the use-mode (`0x60D`), once.** Pressing a strategy button writes `Fast` and then
  selects the mode; pressing **Off** writes `Stop` and returns the mode to `Off`. Nothing else writes
  it, and nothing re-asserts it.
- **The loop still only acts when all three hold**: the SolaX device is reachable, its own use-mode
  reads **Fast**, and a strategy is running. If the charger is changed at the wallbox mid-session it
  drops out of Fast, every controller goes `Idle`, and **nothing writes `Fast` back** — the owner has
  the last word on their own hardware, and the controller does not fight them for it.

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

**Validate first with `DryRun`.** Set `DryRun: true` to run the full control loop and log exactly what it *would* write, without touching the charger — both writes, in the same shape:

```
[DRY RUN] would set charger use-mode: Fast (register 1). Solar started by Web UI.
[DRY RUN] would set charger current setpoint: 6A -> 16A (register 1600). Charge at 16A from 3700W surplus.
```

This is the safe way to confirm the register values against your device before allowing real writes. In dry-run the values that were "written" also stand in for the hardware on the next read, so a dry run behaves like a real one rather than reporting every mode `Idle` because the charger is still in Green.

There is deliberately **no `ChargeControl:Enabled`**: the service boots in mode `Off` and writes nothing at all until somebody presses a button, which is a stronger guarantee than a config flag and one nobody can leave switched on by accident.

In dry-run, **nothing is ever written to a SolaX device**. That's enforced twice: each write site is skipped, and the Modbus clients are wrapped in a read-only decorator that drops writes outright, so even a caller that forgot its guard cannot reach the hardware. A suppressed write logs a warning as a tripwire — it should never appear.

> ⚠️ **This feature writes to your charger — two registers.** The charge-current setpoint (`ChargeCurrentSetpoint 0x628`, written by the control loop) and the use-mode (`ChargerUseMode 0x60D`, **written** by a start/stop action: `0=Stop, 1=Fast`). Both come from the SolaX X1/X3-HAC protocol / the wills106 register map, but **GEN1/GEN2 and firmware differences exist** (GEN1 uses Datahub Charge Current `0x624`). `0x60D` in particular was read-only in this project until #89, so its write path has less mileage on it than the setpoint's. Also confirm your charger accepts `PauseCurrentAmps` (0 A by default). **Run with `DryRun: true` and check the logged register values against your hardware first.** Nothing is written until a button is pressed, which is why there is no enable flag to forget.

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

1. The **battery discharge hold is armed** — the pack does not serve the car (see
   [Battery discharge hold](#battery-discharge-hold-writes-to-the-inverter) for the mechanism). You
   can switch it off while the charge runs; see [The hold, and who owns it](#the-hold-and-who-owns-it).
2. The charger is pinned at **`MaxChargingCurrentAmps`** — written by the start action itself, before
   the first poll, and then re-commanded every cycle whatever the sun, the SOC, the forecast or the
   time of day. PV covers what it can and the **grid covers the rest**.

   Writing it at the press rather than leaving it to the poll loop closes a real gap: a finished
   charge ends by writing `PauseCurrentAmps` (0 A here), so without this the next fast charge spends
   up to a poll interval sitting in `Fast` with the car told to take nothing. A charger already at
   the maximum is left alone — the setpoint write is skipped when it would not move anything — and a
   setpoint write that fails does **not** fail the start, because the control loop commands the same
   current seconds later.
3. When the charge is over — the **amount you asked for** has been delivered, or the car stopped at
   **its own** charge limit — the setpoint drops to `PauseCurrentAmps`, the charger is written `Stop`,
   the mode returns itself to **`Off`**, and the hold is released — exactly the end state the **Off**
   button produces, in the one cycle.

Point 3 is what makes it safe to press. The state it creates is expensive — maximum current, grid
import, battery locked — and it ends by itself instead of sitting armed until somebody notices.

#### How much? (the amount)

By default the **car** decides when it has had enough, which is what this mode did for its whole life
before #119. You can instead say how much to deliver, in either of the two ways the targeted tab
already asks — and the answer is a **stopping condition and nothing else**:

| Basis | What you say | When it ends |
|---|---|---|
| **Until the car stops** *(default)* | nothing | the car reaches its own charge limit and stops drawing |
| **Energy to add** | `20` kWh | 20 kWh has been delivered, measured at the charger |
| **Battery target** | `60` % | the same, against kilowatt-hours converted from the gap |

- **It does not pace the charge.** The setpoint is at the maximum from the first cycle to the last;
  the amount only decides when to stop. Anything that waits for cheaper sun, or plans around a
  departure, is [`Targeted`](#targeted-charging-the-targeted-mode) — that is the whole difference
  between the two modes, and the reason both exist.
- **A battery target is converted once**, when you press the button, using
  `Ev:Vehicles:0:BatteryCapacityKWh` and `Ev:Vehicles:0:ChargeEfficiency`. A later reading does not
  move a charge that is already part delivered — the same rule, and the same reasoning, as
  [a targeted SOC request](#setting-a-target). It is offered only where it can be honoured:
  a configured capacity **and** a reading from the car. Without both, ask in kilowatt-hours.
- **Delivery is metered from the moment you start**, not from when the car was plugged in. Energy the
  car took under an earlier mode does not count towards this amount.
- **Whichever comes first wins.** A car that reaches its own limit at 12 kWh of the 20 asked for ends
  the mode there and says how far short it got.
- **It does not survive a restart**, like the mode itself and the targeted request.

#### When? (the departure)

By default the charge starts the moment you press the button. Give it a **departure** and it starts as
late as it can while still finishing in time:

```
start no later than  =  departure − safety margin − (energy still needed ÷ charging power)
```

30 kWh at 11 kW, wanted by 07:00, with the standard 15-minute margin: the charge waits until about
**04:02**, then runs flat out. Until then the charger sits in `Fast` at `PauseCurrentAmps` and the
controller says what it is waiting for.

**Why you would want this: the pack.** A lithium cell ages faster the longer it is held at a high state
of charge, so the 20 points above 80% are worth buying as late as possible. The charge itself takes two
or three hours; it is the nine hours of *sitting* at 95% that does the damage. Pressing the button at
22:00 for an 07:00 departure turns those nine hours into about twenty minutes.

- **It changes *when*, never *how*.** When it runs, it runs at `MaxChargingCurrentAmps` exactly as it
  always did. Nothing here paces the charge, waits for cheaper electricity, or consults a forecast —
  that is [`Targeted`](#targeted-charging-the-targeted-mode), and the difference is the whole reason
  both exist.
- **It needs an amount.** With **Until the car stops** there is no duration to work back from and so
  no such thing as the latest moment it could start; the combination is refused in words rather than
  quietly charging at once.
- **The schedule is rebuilt every poll**, so the start time moves *later* as energy goes in — and
  *earlier* the moment the car turns out to draw less than the charger offers. An 11 kW wallbox in
  front of a car with a 7.4 kW on-board charger is otherwise a plan an hour short of the time it
  needs, and this is the mode with no slack in it.
- **Before the car draws anything, the plan is a guess.** The car's on-board limit is not knowable
  until it charges, so the first schedule uses the installation's maximum and says so — *"the
  installation's maximum, not yet measured"*.
- **Not enough time?** It charges flat out from now and reports how far short it will fall, rather
  than pretending. The same honesty `Targeted` commits to.
- **Plugged in after the start time?** It begins at once. The plan is a "not before" gate, never a
  "not after" one.
- **The departure passing ends the mode**, reporting what was delivered — the same rule `Targeted`
  follows. A car that is not ready at the moment you said you were leaving has run out of the window
  it was given.

> ⚠️ **A deferred charge does not survive a restart.** Neither does the mode nor the amount, and the
> reasoning is the same throughout — but this is the sharpest form of it: a charge deferred to 04:02
> and lost to a 23:00 container restart is discovered at 07:00 with a car that never charged. If you
> restart the stack in the evening, set it again afterwards.

#### The hold, and who owns it

A fast charge **arms the battery discharge hold when it starts charging**, and ending it — for *any*
reason: you pressed Off, the car finished, the amount was delivered, the car was unplugged —
**releases it**.

*When it starts charging*, not when the mode is selected: a charge deferred to 04:02 would otherwise
lock the pack out of serving the house from 22:00, all night, with nothing being charged at all.

Between those two moments the switch is yours: turn **Battery discharge hold** off in Home Assistant
or the web UI and the pack is allowed to help, while the car goes on charging at maximum. That is a
legitimate thing to want and it is your call.

> Before #119 it was not. The mode's hold was a floor OR-ed over your switch, so flipping it off
> during a fast charge moved the switch on screen and changed nothing at all.

Two consequences worth knowing:

- **A hold you had switched on before starting is off when the charge ends.** The mode releases the
  hold on the way out rather than remembering what the switch said an hour ago — the alternative
  leaves the pack locked out of serving the house with nothing charging.
- **Taking the charger out of `Fast` at the wallbox is not an ending.** The mode stays selected, the
  controller goes `Idle`, and the hold stays armed. Press **Off** if that is what you meant.

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
- **It doesn't change the charger's use-mode while it runs.** Starting it wrote `Fast` once and
  ending it writes `Stop`; in between, like every other mode, it only moves the current setpoint.
- **It doesn't act on a charger that isn't in `Fast`.** If the read-back says anything else — someone
  moved it at the wallbox, or the charger did not hold the write — every cycle logs
  *"Charger use-mode is X, not Fast; leaving it untouched"* and **no current is commanded**. That line
  in the log is the first thing to check when a fast charge appears to do nothing.

With `BatteryHold:Enabled` false the mode still charges at maximum current, and logs a warning once on
selection: it cannot keep the battery out of the charge, which is half of what it promises.

```jsonc
"ChargeControl": {
  "MaxChargingCurrentAmps": 16,          // the ceiling this mode pins the charger at
  "CompletionPowerThresholdWatts": 200,  // below this draw, the car counts as not charging
  "CompletionDwell": "00:02:00"          // idle this long -> finished; pause and return to Off
}
```

The amount and the departure are not configuration — they belong to one charge — so there is nothing
to set here for them. A battery target reads `Ev:Vehicles:0:BatteryCapacityKWh` and
`Ev:Vehicles:0:ChargeEfficiency`, the same two figures a targeted SOC request uses; a departure reads
`ChargeControl:Targeted:SafetyMargin` and `ChargeControl:Targeted:MaxHorizon`. Those two are
deliberately shared rather than duplicated: *"ready at 07:00 must not mean still charging at 07:00"*
is a fact about your morning, not about which strategy happens to be running, and two settings for it
would only ever drift apart.

### Targeted charging (the `Targeted` mode)

The four other modes all ration the car to what the day happens to offer. None of them answers the
question an owner actually asks the night before a trip: **"I need 22 kWh in the car by 07:00 — do
that, and use as little grid as you can."**

`FastNoBattery` is the closest thing to it, and it is a blunt instrument even now that it can be given
an amount: it charges flat out from the moment it is selected, so a car plugged in at 21:00 for an
07:00 departure imports the whole lot overnight and ignores a sunny morning entirely. Its amount says
*when to stop*; this mode's says *when to start, and on whose energy*.

This mode takes an **energy in kWh** and a **departure time**, and plans backwards from the deadline.
The plan is rebuilt from a refreshed forecast and the **measured** delivery on every poll, and nothing
in it is ever committed to.

With [vehicle telemetry](#vehicle-telemetry-the-vehicle-section) configured you can state the target as
a **state of charge** instead — *"80% by seven"* — and the controller converts it to kilowatt-hours
once, when you start it. Energy is still what is promised and still what is metered; see
[Setting a target](#setting-a-target).

Everything turns on one comparison: what is still needed against `P_max × (departure − now)`, the
physical ceiling of the charger running flat out for every remaining minute.

**Not enough time** — the charger takes everything from now until departure, sun and grid together,
with the discharge hold armed so the pack stays out of it. The value of the plan here is honesty: it
reports how much will really reach the car, how far short of the request that is, and the departure
time that *would* have covered it.

**Enough time** — the car is **paced** across the whole window rather than run flat out.

```
pace = (energy still needed − sun forecast to reach the car) ÷ time left to the deadline
charge at  live surplus + pace,  clamped to the charger's 6–16 A range
```

Rate, not timing, is what decides the solar share, because **the charger can only use the sun that
shines while it runs**. At 16 A the car outruns the roof by several kW whatever the weather and then
stops: on 2026-08-22 a 13 kWh target on a day peaking at 8.5 kW was over in 87 minutes and took 9 kWh
from the grid. The same 13 kWh paced over a four-hour window is ~3.3 kW — a rate the roof matches for
much of the day.

Sun above the pace is taken in full: free energy, and it lowers the pace for every minute after it.
Sun below it is topped up from the grid. Falling behind raises the pace again on the next poll, so the
loop self-corrects in both directions and nothing is ever committed to.

A pace of **zero** means the forecast covers the target outright — then the car simply waits for the
sun and buys nothing. Activated at 02:00 for 15 kWh by 17:00 on a good forecast, nothing happens
overnight and charging starts at the first half-hour whose surplus clears the charger's 6 A minimum.

#### Sun first, grid allowed

Solar is the **preferred** source and the car takes as much of it as the window will give. Live
surplus is never refused for being unplanned: the plan schedules *imports*, not sunshine, so a
half-hour the forecast said nothing about — or underestimated — is charged from all the same. The
only thing that holds the car back from the sun is the plan's SOC floor, which is the home battery's
priority talking.

Grid energy is **allowed**, and there are two ways it arrives. The first is the planned block above.
The second is the **grid bridge**, which exists for one situation:

> On three phases the charger's 6 A floor is ~4.14 kW. A perfectly good 3.5 kW surplus therefore
> charges *nothing at all* — the car cannot sip it — so it is exported, and on a plan that already
> owes the grid, the same kilowatt-hours are bought back after dark.

The bridge runs the charger at its floor and buys the difference: 640 W now instead of 4.14 kW later,
with 3.5 kW of sun that would have been given away going into the car. It shrinks the planned grid
block watt for watt. It is the same trade the `Forecasted` mode makes with its
[battery loan](#the-battery-loan) — but funded from the grid, because this mode will not touch the
pack.

It never funds a session on its own. It is refused when the sun covers the whole target (no committed
import to bring forward, and the day was about to hand that energy over free), refused below the
plan's SOC floor, refused on a surplus under `ChargeControl:Forecast:MinBridgeSurplusWatts`, and sized
to reach the floor and not one watt past it. Set `ChargeControl:Targeted:GridBridge` to `false` to
keep imports strictly inside the blocks the plan drew.

#### What it does and doesn't do

- **The home battery keeps its priority.** Its need is reserved out of the forecast by the same
  backward pass [`SolarDayPlanner`](#the-shoulders-belong-to-the-battery-the-plateau-belongs-to-the-car)
  uses, so the car is only ever offered what is left; and the discharge hold arms **whenever the car is
  drawing more than the roof is giving**. Unlike `FastNoBattery`, the hold is scoped to the importing
  part of the cycle rather than to the whole mode. Below the plan's SOC floor the sun belongs to the
  pack outright: the car still gets its pace, but funded entirely by the grid, and the whole of it
  counts as imported so the hold arms for all of it.
- **There is still no battery loan in this mode.** The pack keeps priority and the grid is the honest
  source for the gap, bridge included.
- **Sub-minimum sun is still worth having.** A 3.2 kW half-hour cannot run the charger alone, but once
  the charger is held at its floor the roof supplies that 3.2 kW and only the ~0.9 kW difference is
  bought. Skipping it and buying the same energy after dark costs the whole 4.14 kW. The plan keeps two
  separate figures for this: what the sun could do **unaided** decides whether anything need be bought
  at all, and what it can do **assisted** decides how much.
- **No usable forecast is not a failure.** Every solar term goes to zero, the plan becomes grid-only,
  and the target is still met — live surplus is used opportunistically anyway. Where `Forecasted`
  degrades towards conservatism, this mode degrades towards *keeping the promise*.
- **Delivery is metered on measured charger power**, from the moment the request was activated. A car
  that limits itself to less than we asked for simply gets a longer grid block on the next poll, with
  no special case anywhere.
- **A safety margin** (`ChargeControl:Targeted:SafetyMargin`, 15 minutes by default) pulls the finish
  line in from the departure, so "ready at 07:00" doesn't mean "still charging at 07:00".
- **It returns itself to `Off`** when the target is met, when the departure passes, or when the car
  stops drawing at its own limit — the same arrangement `FastNoBattery` uses.

> ⚠️ **The request does not survive a restart.** Neither does the mode, and for the same reason: after
> a crash, a power cut or a deploy the charger is left exactly as its owner set it. But this is the
> one runtime setting whose loss is discovered at 05:00 with a flat car, so it is worth saying
> plainly. If the service restarts overnight, nothing will have told you, and nothing will be
> charging.

#### Charging priority: cheapest, or just in time

By default a targeted charge is delivered **as cheaply as possible**: paced across the whole window,
taking every watt of sun above that pace, and finished whenever the sun and the pace happen to land it
— usually well before you leave. That is exactly right for a partial charge and wrong for a full one.
On a sunny day with an 07:00 departure the car hits 100% by mid-afternoon and then sits full all night,
which is the one thing every manufacturer tells you not to do to a lithium pack.

So the request carries a **priority**:

| | |
|---|---|
| **Cheapest** | The default, and what every request did before this existed. Nothing about the mode changes. |
| **Just in time** | Everything up to a **rest SOC** is delivered exactly as *Cheapest* would deliver it, on the sun, whenever the sun is there. The **last stretch above that** is held back and scheduled to land shortly before departure. |

**Only the last stretch.** Delaying the whole charge would forfeit a sunny day to protect the top of
the pack and then buy the lot from the grid at 04:00. The split is at the rest SOC (80% by default,
`ChargeControl:Targeted:JustInTime:RestSocPercent`, and settable per request):

```
100% |                                          .--  departure
     |                                        .-'
 80% +========================================'      <- rest SOC: reached on
     |      /  sun-driven, paced, any time               sun, then held here
 45% +-----'
     +--------------------------------------------------------------
      12:00          16:00          20:00        04:00   06:45
                                                  ^ release
```

The release point is `deadline - tail / max charge power - ReleaseSlack`, and that is the whole of the
arithmetic. **There is no taper model and no reading of the car's own charge limit**, deliberately: the
plan is rebuilt every poll from *measured* delivery, so a tail the car takes more slowly than the
charger's rated maximum simply raises its own pace on the next poll until it saturates at maximum
current, and **Target shortfall** reports the gap before departure if even that will not do it. And if
the car stops on its own limit — set to 80% in the car, say, against a 100% target — the controller's
completion path says so in the words it always has: *"car stopped drawing for 12 min at 12.4 kWh of
15.0 kWh — its own limit, short of the target"*. Predicting any of that in advance would add ways to be
wrong without adding a way to be right.

> **It is allowed to cost money, and it says so first.** While the tail waits, sun is genuinely turned
> down — taking a bright afternoon would put the car at its target by teatime, which is what the
> priority exists to prevent. So *Preview* runs the planner **both ways** and names the difference:
> *"Just in time buys about 4.2 kWh more from the grid than charging as cheaply as possible would."*
> That trade — a few kilowatt-hours against cycle life — is yours to make with the number in front of
> you. The comparison is done in the preview only; running two plans every poll to report a
> counterfactual is not worth the cycles on a Pi.

**The deadline still outranks the priority.** The hold is given up entirely — and the plan reverts to
an ordinary paced charge — when the release point has already passed, when there is nothing above the
rest point to hold, or when holding would leave the rest of the charge more than the shortened window
can deliver. The departure was the promise; the timing was only a preference.

**Offered only when it can be honoured.** The rest point is a state of charge, so it needs the car's
reported SOC *and* `Ev:Vehicles:0:BatteryCapacityKWh` — the same rule the **Battery target (%)** basis
follows. Without both, the control does not appear on the web tab, and a Home Assistant activation logs
a warning and charges with nothing held rather than silently pretending to hold something.

While the hold is in force the charger sits **visibly idle**, sometimes for hours. Both surfaces say so
in as many words — *"the charger is idle on purpose"* — because that state is otherwise the single most
convincing impersonation of a fault this controller can produce.

#### Setting a target

From the web UI, the **Targeted** tab of **Charging plan**: what the car needs, the departure,
**Preview plan**, and then **Start charging**.

**The plan is shown before the charger moves.** *Preview* runs the same planner the poll loop runs,
against the same telemetry and the same forecast, and writes to nothing — no request, no mode, no
device. What comes back is the plan you will then watch, in the same words and the same figures, and
*Start charging* under it commits exactly that. Editing any field drops the preview, so it is never
possible to confirm a plan that no longer describes the form above it. This matters most in the case
that cannot be fixed afterwards: *"even flat out you get 24 of the 31 kWh you asked for; the departure
that covers it is 05:40"* is worth knowing **before** the charger starts.

**Two ways to say what you need.** The default, and the only one an install without a vehicle feed ever
sees, is **Energy to add (kWh)**. With a car reporting its SOC *and* a configured pack capacity,
a second basis appears — **Battery target (%)** — and the kilowatt-hours are worked out for you:

```
(target% − now%) / 100 × usable capacity ÷ charge efficiency
```

The tab shows what the car last said about itself above the form — **battery, range, plug state,
charge state and the age of the reading** — because those are the numbers the plan is about to be built
from. A reading past `Vehicle:MaxAge` is flagged rather than withdrawn: a parked car's SOC does not
drift, and refusing the basis outright would only push you into doing the same arithmetic in your head
from the same figure.

The conversion happens **once, at the moment you start it**, and is never re-derived. A parked car
reports when it feels like it, so a SOC that jumps six points at 02:00 because the car finally phoned
home would otherwise silently move a promise that is already half delivered. What is recorded is the
energy; the target and the SOC it was measured from are kept only so the request can be read back as
"42% → 80%".

From Home Assistant: the
**Target energy** number, the **Departure time** text (`07:00` means the next 07:00; `2026-08-11
07:00` means exactly that), and the **Activate target** button. Both drive the same two seams in
`Gleanvolt.Core` — the request selector and then the charge action, in that order — so the two
surfaces cannot disagree about what was asked for. Home Assistant speaks kilowatt-hours only: the
battery-target basis is a convenience over a contract it already speaks, and a `target_soc` entity can
follow if it is wanted. **Start charging** also puts the charger into `Fast`,
like every other way of starting charging; **Cancel** is the same `Off` action the page's own button
is, plus the one thing that button cannot know to do — dropping the request, so nothing is left
looking like a promise.

A departure may be at most `ChargeControl:Targeted:MaxHorizon` (36 hours) ahead. Solcast's cached
forecast runs days further, but a target four days out is a promise the forecast cannot keep — and
neither can a request that does not survive a restart.

```jsonc
"ChargeControl": {
  "Targeted": {
    "SafetyMargin": "00:15:00",   // finish this long before the stated departure
    "MaxHorizon": "1.12:00:00",   // the furthest ahead a departure may be set (d.hh:mm:ss -- "36:00:00" is 36 *days*)
    "GridBridge": true,           // may the grid lift a sub-floor surplus to 6A? see above
    "JustInTime": {
      "RestSocPercent": 80,       // where the car waits before the last stretch is released
      "ReleaseSlack": "00:30:00"  // how much earlier than strictly necessary that stretch starts
    }
  }
}
```

### Home Assistant (MQTT)

The worker can expose itself to Home Assistant over MQTT ([HA MQTT Discovery](https://www.home-assistant.io/integrations/mqtt/#mqtt-discovery)), so HA auto-creates a device with:

- **one button per strategy, plus Off.** Charging is started by an action and by nothing else: each
  button writes the charger's use-mode `Fast` and then selects its strategy, so a press works on a
  charger sitting in Green rather than waiting for the wallbox to have been set by hand.
  - **Charge solar** — modulate the charging current from live surplus while the battery is full;
    pause when there isn't enough sun.
  - **Charge forecasted** — as Solar, but the fixed battery-full gate is replaced by a forecast-driven
    day plan, so the car can start well before the battery is full. See
    [Forecast-driven charging](#forecast-driven-charging-the-forecasted-mode) below.
  - **Charge fast** — charge at the maximum configured current from PV and grid together, with the
    battery discharge hold armed, and stop when the car is full — or at an amount you set. See
    [Fast charge without the battery](#fast-charge-without-the-battery-the-fastnobattery-mode) below.
    This is one of the two that switch *themselves* off, so **Charge mode** will change under you when
    the charge ends. The amount is optional and lives on the three entities beside the button:
    **Fast basis**, **Fast energy**, **Fast target SOC** and **Fast departure**. Left at `Full` with
    no departure — the default — the button does exactly what it always did.
  - **Activate target** — deliver a stated amount of energy by a stated departure time, sun first and
    paced across the window so the roof covers as much of it as it can. See
    [Targeted charging](#targeted-charging-the-targeted-mode) below.
    It has no button of its own in the row because it needs a request: set **Target energy** and
    **Departure time**, and this button applies both and starts the mode. Like `FastNoBattery`, it
    stops itself once the target is met.
  - **Charge off** — writes `Stop` to the charger and returns the mode to `Off`, whatever was running.
    It always writes, even if the controller had never taken control. Not to be confused with **Stop
    service**, which takes the whole controller off the air.

  **The service always starts in `Off`**, whatever is in the config, and nothing persists a mode
  across restarts. After a crash, a power cut or a deploy the charger is therefore left exactly as its
  owner set it, rather than being grabbed by whichever mode a config file happened to name. A previous
  version published a **Charge mode** *select*; upgrading retires it, and Home Assistant removes the
  entity on its own — there is nothing to delete by hand.
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
- the fast-charge controls — the **Fast basis** select, the **Fast energy** and **Fast target SOC**
  numbers and the **Fast departure** text, applied by the **Charge fast** button that was already
  there — plus its sensors: **Fast delivered**, **Fast target** and **Fast start**. All absent while
  the mode runs with no amount, which is how a dashboard shows "the car decides". See
  [Fast charge without the battery](#fast-charge-without-the-battery-the-fastnobattery-mode) above.
- the targeted-charge controls — the **Target energy** number, the **Departure time** text, the
  **Charge priority** select, the **Target rest SOC** number and the
  **Activate target** button — plus its plan sensors: **Target plan state**, **Target solar energy**,
  **Target grid energy**, **Target expected**, **Target shortfall** and **Grid top-up start**. See
  [Targeted charging](#targeted-charging-the-targeted-mode) above.
- binary sensors: **Car connected** and **Charging now**.
- **Car feed** — only on an installation with a
  [manufacturer vehicle feed](#the-car-from-the-manufacturer-on-a-clock-the-vehicledataact-section)
  configured. `Ok`, `Degraded` or `NeedsOwner`, with the reason as an attribute: the entity a
  "the car feed wants me in a browser" notification keys off.
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
| **Charge mode** | sensor | Which strategy is driving the charger: `Off`, `Solar`, `Forecasted`, `FastNoBattery` or `Targeted`. Read-only — it reports what a button did, and it moves on its own when `FastNoBattery` or `Targeted` finish. Always `Off` after a restart. |
| **Charge solar** | button | Starts the `Solar` strategy: writes the charger's use-mode `Fast`, then selects the mode. A charger that refuses the write leaves the mode untouched and logs a warning, rather than reporting a strategy that is doing nothing. |
| **Charge forecasted** | button | The same, for `Forecasted`. |
| **Charge fast** | button | The same, for `FastNoBattery`, and it applies the three **Fast** entities below as it goes. Read the warning about `MaxChargingCurrentAmps` before pressing it — this one draws the site's supply limit for hours. An amount it cannot honour (a battery target with no configured capacity, a car already past it) logs a warning and starts nothing, rather than charging to full instead. |
| **Charge off** | button | Writes the charger's use-mode `Stop` and returns the mode to `Off`, releasing any hold a mode had armed. Always writes, even when the controller was already `Off` and never took control: the button says stop charging, so it stops charging. The current setpoint is left wherever the last cycle put it. This stops *the car*, not the controller — that is **Stop service**. |
| **Battery discharge hold** | switch | Stops the home battery serving household load, so the car charges from PV and grid while the battery can still charge from surplus. Shows the last command written successfully, not a read-back — the register can't be read, so a failed write shows up as the switch springing back to `OFF`. `FastNoBattery` turns it **on** when it starts and **off** when it ends, whatever ended it — and in between this switch is yours: turning it off really releases the hold, and the car goes on charging at maximum. `Targeted` is different: it arms its own hold only while the plan is importing — inside its grid block and while the grid bridge runs — and never touches this switch. |
| **Daily EV target** | kWh | How much energy the car should get on a normal day. The forecast plan measures its projected shortfall against this. Doesn't persist across restarts. |
| **Session energy target** | kWh | Stop charging once this much has gone into the car in one session (since it was plugged in). `0` means no limit. Doesn't persist across restarts. |
| **Minimum battery SOC** | % | The hard floor the forecast plan may never take the home battery below, however good the forecast looks. Doesn't persist across restarts. |
| **SOC resume margin** | % | How far above the floor the battery must recover before a paused session restarts — charging continues down to the floor itself, only coming back costs the margin. Raise it if the car starts and stops repeatedly on a marginal day. Never applied below the hold's release margin. Doesn't persist across restarts. |
| **Fast basis** | select | What a fast charge is aiming at: `Full` (the car decides — the default), `Energy` (reads **Fast energy**) or `Soc` (reads **Fast target SOC**). Held until **Charge fast** is pressed; a basis chosen and not pressed changes nothing. Doesn't persist across restarts. |
| **Fast energy** | kWh | How much to deliver before the fast charge stops itself, measured at the charger and metered from the press. Read only under the `Energy` basis. Doesn't persist across restarts. |
| **Fast target SOC** | % | The state of charge to stop a fast charge at. Read only under the `Soc` basis, and only honoured with `Ev:Vehicles:0:BatteryCapacityKWh` configured and a reading from the car — converted to kilowatt-hours once, at the press, and not re-derived from a later reading. Doesn't persist across restarts. |
| **Fast departure** | text | When the car has to be ready, as `HH:mm` (the next one) or `yyyy-MM-dd HH:mm`. Empty means charge straight away, which is the default. With a time the charge is held back and starts as late as it still can — see [When? (the departure)](#when-the-departure). Needs an amount to work back from, so a departure with the basis on `Full` is refused. Doesn't persist across restarts. |
| **Fast start** | sensor | When the deferred charge will begin, as `HH:mm`, or `none` when it starts immediately. Moves later as energy goes in, and earlier if the car turns out to draw less than the charger offers. Absent unless `FastNoBattery` is driving with an amount set. |
| **Fast delivered** | kWh | Energy delivered against the fast charge's amount, since it was started. Absent unless `FastNoBattery` is driving with an amount set. |
| **Fast target** | kWh | The amount that fast charge is working to — the number **Fast delivered** is counting towards. Absent on the same terms. |
| **Control state** | — | What charge control is doing right now. `Disabled`: nothing is running, the charger is the owner's. `Idle`: a strategy is running but not acting — most often because the charger has been taken out of Fast at the wallbox since it was started. `Charging`: a current is being commanded. `Paused`: the setpoint was dropped to the pause current, typically because the surplus fell below what the charger's 6 A floor needs. |
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
| **Car feed** | — | How the [manufacturer's vehicle feed](#the-car-from-the-manufacturer-on-a-clock-the-vehicledataact-section) is doing, with the sentence as a `reason` attribute. `Ok`: the last read produced a reading. `Degraded`: it is trying and not currently succeeding — a 5xx, a timeout, an expired session, or a delivery not filled yet; it backs off and clears itself. `NeedsOwner`: a refused password, a consent screen, an OTP or a portal setting only you can make — **the feed has stopped asking** and will not resume until you have cleared it and restarted the controller. That last state is the one worth a notification; the other two are not. Absent entirely on an installation with no such feed. |
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

The targeted-charge entities. The five controls are always live — a request can be prepared before
the mode is selected — while the nine sensors are populated only while **Targeted** is driving, by the
same rule the forecast plan follows.

| Entity | Unit | What it means |
| --- | --- | --- |
| **Target energy** | kWh | How much energy the car needs by the departure time, measured at the charger from the moment **Activate target** is pressed. Nothing happens until it is. |
| **Departure time** | text | When that energy has to be there. A bare `07:00` means the **next** 07:00 — which is what somebody typing it at 22:00 means by it; `2026-08-11 07:00` means exactly that. MQTT discovery has no datetime platform, which is why this is text. Read back as the resolved timestamp, so a day later it is still unambiguous. **Empty means no departure** — which is what it holds whenever no target is set, and clearing the box clears the pending departure. Anything else is refused with a warning in the log rather than guessed at. |
| **Charge priority** | select | `Cheapest` (the default, and what every request did before this existed) or `JustInTime`. Under `JustInTime` the last stretch of the target is held back so the car finishes shortly before departure instead of hours early. Applies to the next **Activate target**, like the two above. Needs the car's SOC and `Ev:Vehicles:0:BatteryCapacityKWh` to find the rest point; without both, the press logs a warning and charges with nothing held. |
| **Target rest SOC** | % | Where the car waits under `JustInTime` before the final stretch is released. Ignored under `Cheapest`. |
| **Activate target** | button | Applies the two above: sets the request, then starts the `Targeted` mode — which writes the charger's use-mode `Fast`, like every other way of starting charging. Pressed with either half missing, or with a departure already past, it logs a warning and does nothing. |
| **Target plan state** | — | One line on what the plan is doing and why — the same explanation the log carries. |
| **Target forecast surplus** | kWh | All the surplus the window is forecast to hold, after the house and the home battery's booking — whether or not the car can charge on it unaided. Read it against **Target solar energy**: the gap between them is sun the roof will produce but the charger cannot run on by itself. |
| **Target solar energy** | kWh | The share of what is still needed that the roof is expected to supply, after the home battery's own booking — including surplus below the charger's 6 A minimum, which the car can still use while the grid holds it at that minimum. Read it against **Target forecast surplus**. |
| **Target grid energy** | kWh | The share planned to come from the grid, and an **upper bound** rather than a prediction. Rebuilt every poll, so a better day than forecast shrinks it before the rest is bought — and where the import sits over sub-minimum surplus the charger runs at maximum while the roof quietly covers part of it, which this figure does not model. Expect to import less than it says. |
| **Target expected** | kWh | What will actually reach the car by the departure. Equal to what was asked for unless there isn't enough time. |
| **Target shortfall** | kWh | How far **Target expected** falls short of the request. Anything above zero means the departure is too soon for the amount asked for; **Target plan state** names the departure that would have covered it. |
| **Target charge pace** | kW | The average the grid must sustain to keep the promise: what is still needed, less the sun forecast to reach the car, over the time left. The charger runs at this **plus** whatever the roof is giving. Zero means the forecast covers the target outright and nothing will be bought. |
| **Charging from** | — | When the first grid-funded charging starts. `none` while the forecast covers the whole request. |
| **Target hold until** | — | When the held last stretch is released, under `JustInTime`. `none` whenever nothing is being held — which is every `Cheapest` plan, and any `JustInTime` one where holding would have put the departure at risk. |

#### Configuration

Disabled by default. Non-secret settings live in `appsettings.json`:

```jsonc
"HomeAssistant": {
  "Enabled": false,
  "BrokerHost": "localhost",
  "BrokerPort": 1883,
  "DiscoveryPrefix": "homeassistant", // HA's discovery prefix
  "BaseTopic": "gleanvolt",           // {BaseTopic}/{Pv:Id}/... is where everything is published
  "DeviceId": "",                     // the unique-id root; "" = take Pv:Id. See the warning below
  "RetireDeviceIds": [],              // former DeviceId values whose retained configs to blank
  "RetireTopicPrefixes": [],          // former {BaseTopic}/{id} prefixes whose retained state to clear
  "StatusInterval": "00:00:15"
}
```

Broker credentials are secrets — supply via `.env` / env var, not `appsettings.json`:

```
HomeAssistant__Username=<user>
HomeAssistant__Password=<pass>
```

All of it is **readable back** at [`/pv-system`](#pv-system--the-installation-read-only) — including
the topic prefix, which no single setting spells out — so "what is this controller publishing, and
where?" does not need an ssh session and a `grep` of `.env`. The password itself is shown there only
when the UI is behind a login.

#### Two names, and only one of them is dangerous to change

**The PV system's id namespaces the topics.** Everything this controller publishes hangs off
`{BaseTopic}/{Pv:Id}` — `gleanvolt/home-roof/state`, `/availability`, `/{entity}/set` — so two
installations can share one broker without overwriting each other, entity for entity, which is what
they did before. `Pv:Id` is therefore **required once `HomeAssistant:Enabled` is true**; a
controller-only deployment publishes nothing and needs none. The device page in Home Assistant is named
after `Pv:Name`.

**`HomeAssistant:DeviceId` is the unique-id root, and changing it costs the history.** Home Assistant
keys an MQTT entity to its `unique_id`: a new one is a *new entity*, created as `sensor.…_2` because the
old entity id is still taken, and every graph on your dashboard starts again. Nothing else here behaves
like that — topics, the device name and the device identity can all change freely, and the entities
follow — which is precisely why this one value does **not** follow `Pv:Id` automatically.

- Empty means "take `Pv:Id`" — one id, configured once, which is what a fresh installation wants.
- An installation that already has history **pins the id its entities were created with**.
  `deploy/docker-compose.yml` pins `solax_controller` for exactly that reason.
- To change it deliberately, set `RetireDeviceIds` to the old value in the same deploy, so the retained
  discovery configs left on the broker are blanked rather than re-creating yesterday's device on the
  next restart. The procedure that keeps the history is in
  [the deploy README](deploy/README.md#renaming-what-home-assistant-sees).

**`RetireTopicPrefixes`** clears retained *state* under a prefix you no longer publish on — set it to
`solax/solax_controller` once after upgrading from a release that predates `Pv:Id`, then remove it. It
changes nothing functional: it is the difference between a broker where `mosquitto_sub -t '#' -v` shows
one of everything and one where it shows two.

A ready-to-run broker + Home Assistant for local development lives in [`dev/homeassistant/`](dev/homeassistant/) (`docker compose up -d`). Watch the traffic with:

```bash
docker exec -it solax-dev-mosquitto mosquitto_sub -t 'homeassistant/#' -t 'gleanvolt/#' -v
```

### The car (the `Ev` section)

**What the car *is*, as distinct from the feed that reports on it.** The same arrangement
[the installation](#the-pv-system-the-pv-section) has: described in one place, validated once at
startup, and handed to everything that needs it — so nothing has to assemble the car from settings
that belong to something else.

```jsonc
"Ev": {
  "Vehicles": [                          // a list from day one; exactly one entry is supported
    {
      "Id": "id4",                       // stable identity: a slug, like Pv:Id
      "Name": "The ID.4",
      "Make": "Volkswagen",
      "Model": "ID.4 Pro",               // reported, never acted on
      "BatteryCapacityKWh": 77,          // the car's *usable* pack; 0 = unset, see below
      "ChargeEfficiency": 0.9,           // charger meter -> cells
      "Phases": 3,                       // what the CAR can use -- see the warning below
      "MinChargingCurrentAmps": 6,       // below this it will not start
      "MaxChargingCurrentAmps": 16,      // its on-board charger's ceiling
      "Telemetry": { "Topic": "gleanvolt/vehicle/id4/state" }
    }
  ]
}
```

**Nothing here is required, and an absent section changes nothing.** Every figure falls back to the
installation's, which is exactly how the controller behaved before this section existed.

#### Where to put it

Three routes, and which one you want depends on where the controller is running. All of them end at
the same `Ev:Vehicles:0:*` keys, so nothing behaves differently depending on how you got there.

| Running | File | Notes |
|---|---|---|
| **A deployed Pi** (the compose stack) | `deploy/.env` | Set `EV_ID`, `EV_NAME`, `EV_MAKE`, `EV_MODEL`, `EV_PHASES`, `EV_MIN_CHARGING_CURRENT_AMPS`, `EV_MAX_CHARGING_CURRENT_AMPS` — plus `VEHICLE_BATTERY_CAPACITY_KWH` and `VEHICLE_CHARGE_EFFICIENCY`, which keep their old names so an existing `.env` goes on working. `deploy/.env.example` documents each one; `docker-compose.yml` maps them onto the keys above. |
| **Locally, from your IDE or `dotnet run`** | `src/Gleanvolt.Worker/appsettings.Development.json` | The launch profile sets `DOTNET_ENVIRONMENT=Development`, so this file is read on a development machine and **never** by the deployed container. The right place for a car you only want configured while debugging. |
| **A plain, non-Development run** | `src/Gleanvolt.Worker/appsettings.json` | The shipped defaults, where the section exists with every field blank. |

Any single value can also be overridden by an environment variable using the double-underscore form —
`Ev__Vehicles__0__BatteryCapacityKWh=77` — which is what the compose file does under the covers, and
what to reach for when you want to change one figure without editing a file.

A car that is described but has **no telemetry feed** is a perfectly ordinary installation: the car
shows on the dashboard with its pack and its limits, no reading appears, and every kilowatt-hour
target works exactly as it always did. The `Ev` section says what the controller is charging; the
[`Vehicle` section](#vehicle-telemetry-the-vehicle-section) below is a separate, optional convenience
that reports on it.


`BatteryCapacityKWh` is the **usable** capacity — the figure the car's own SOC is a percentage of, not
the gross pack on the brochure (an ID.4 Pro is 77 usable of 82 gross). Unset by default, and it affects
one thing: the **Battery target (%)** basis on a
[targeted](#targeted-charging-an-amount-of-energy-by-a-time) or
[fast](#how-much-the-amount) charge, which cannot turn "80%" into kilowatt-hours without it and is
simply not offered until it is set. Guessing a pack size would make every such target quietly wrong
instead of visibly unavailable, which is the worse of the two failures.

`ChargeEfficiency` is the AC-side loss between the charger's meter and the cells, applied to that
conversion because the target is metered at the charger. It is **not**
`ChargeControl:Forecast:ChargeEfficiency`, which is the *home* battery's PV → pack figure.

#### The three that are the point: phases, and the two currents

`ChargeControl:Phases`, `MinChargingCurrentAmps` and `MaxChargingCurrentAmps` describe **the
installation** — the wallbox and the supply feeding it. Until now the controller had only those and
used them as if they described the car too.

> ⚠️ **`Phases` is the one that goes wrong quietly.** Every watts↔amps conversion in the controller
> runs through a phase count. If your car charges single-phase behind a three-phase wallbox and you do
> not say so, **every power figure the controller reasons with is overstated threefold** — a
> [deferred fast charge](#when-the-departure) starts hours late, and the day plan budgets energy the
> car can never take. Nothing looks broken; the numbers are simply wrong.

`MinChargingCurrentAmps` matters for a car that refuses low currents. Commanded 6 A when it needs 8, it
draws nothing at all — and a connected car taking no power is what the fast mode's completion dwell
reads as *finished*. A charge that never started, filed as one that completed.

**The controller works to the narrower of the two, always:**

```
effective minimum = max(charger minimum, car minimum)     // whichever refuses first
effective maximum = min(charger maximum, car maximum)     // whichever gives out first
effective phases  = min(charger phases,  car phases)      // whichever offers fewer
```

A car can only ever **lower** a limit. An installation limited to 16 A stays limited to 16 A behind a
car that would take 32, because that limit is the site's supply and the wiring in the wall — not a
preference. Anything the car leaves unstated is simply the installation's figure. All three columns
are on `/pv-system`, so "why is my 32 A car charging at 16?" is answerable in a browser.

A car whose minimum is above the installation's maximum could never charge, and every symptom of that
is silence — so it is **refused at startup** naming both keys, rather than discovered one evening as
"I pressed the button and nothing happened".

### Vehicle telemetry (the `Vehicle` section)

The **feed** that reports on the car above — as distinct from the car itself, and from the home battery
the inverter reports. Off by default:

```jsonc
"Vehicle": {
  "Enabled": false,
  "BrokerHost": "localhost",
  "BrokerPort": 1883,
  "MaxAge": "12:00:00"                  // past this, a reading is shown as stale
}
```

The **topic** is not here: it lives on the vehicle, as `Ev:Vehicles:0:Telemetry:Topic`, because two
cars on one broker are two topics. The broker, the credentials and the staleness guard stay here
because they describe the feed rather than the car — which is the whole split this section and the one
above exist to make.

`Vehicle:Username` / `Vehicle:Password` are supported for an authenticated broker and are secrets —
supply them via `.env` or an environment variable (`Vehicle__Username`), never in `appsettings.json`.

The feed as configured — broker, username, client id, and the topic it actually subscribed to — is
shown at [`/pv-system`](#pv-system--the-installation-read-only), which is also where a feed switched on
with no topic reads as the misconfiguration it is rather than as a car that never reports.

> **Upgrading?** `Vehicle:BatteryCapacityKWh`, `Vehicle:ChargeEfficiency` and `Vehicle:Topic` have
> moved into the `Ev` section, and a build that still finds them **refuses to start**, naming the
> replacement. Silently ignoring one would leave a capacity your file says is set and the controller
> has stopped reading — which makes every SOC-based target quietly wrong. If you deploy with
> `deploy/docker-compose.yml`, the `.env` variable names are unchanged and there is nothing to do.

Nothing in `ChargeControl` or `BatteryHold` consumes it, and no charge decision depends on it: a feed
that dies changes nothing about how the charger is driven. It appears on the web UI dashboard and on
the [targeted plan](#targeted-charging-an-amount-of-energy-by-a-time), where its SOC can be *converted*
into a request the owner then confirms — an input to what you ask for, never to how it is delivered.
It is not republished to Home Assistant, since Home Assistant is where it comes from.

#### It reads MQTT, not a car API

The default source is a topic, not a manufacturer. The controller subscribes to **one topic with one
JSON schema**, and each car is adapted onto that schema by a template or automation in Home Assistant:

```jsonc
{
  "captured_at":  "2026-08-17T10:44:23+00:00",   // required: the CAR's capture time
  "soc_percent":  28,                            // optional, 0-100
  "range_km":     176,                           // optional: the CAR's own range estimate
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

There is exactly one manufacturer integration in the codebase, and it is
[the VW Group portal below](#the-car-from-the-manufacturer-on-a-clock-the-vehicledataact-section). It
exists because the EU Data Act gives the *owner* their car's data directly, with no Home Assistant in
the middle — and it writes the same `VehicleState` this schema produces, through the same holder. It is
the exception, not the new default: anything that needs its own credentials, its own session and its
own failure modes has to earn a place here one manufacturer at a time.

Everything except `captured_at` is optional, and absent is a supported configuration rather than an
error. `captured_at` is required because it is the **car's** capture time, not the arrival time, and
without it staleness cannot be judged.

`range_km` is the car's own estimate off its own recent consumption — nothing here could compute it, and
it is the figure that actually answers *"is 80% enough for the trip?"* while you are setting a target.
Display only: no plan reads it. `0` is a real reading (a flat car reports it) and absent is null, so the
two are never confused; a figure beyond 2000 km is rejected as a template publishing metres.

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
             "range_km": {{ states('sensor.id_4_pro_performance_range') | float(0) }},
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
         "range_km": {{ states('sensor.id_4_pro_performance_range') | float(0) }},
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

Then set `Ev:Vehicles:0:Telemetry:Topic` to `gleanvolt/vehicle/id4/state`. The `condition` matters: it stops a payload
being published while the integration's entities read `unavailable`, which happens whenever its cloud
session expires.

**Check the range entity name against your own install** too — `sensor.<car>_range` is what
`volkswagen_connect` exposes here, but it is named differently by other integrations. Drop the line
entirely if yours has none: an absent `range_km` is a supported configuration, and the tile reads "—".

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

#### The car from the manufacturer, on a clock (the `Vehicle:DataAct` section)

The second way to feed the same card: the controller signs in to VW's own **EU Data Act portal** on a
schedule and writes what it finds into the same reading everything else reads. One car, one
manufacturer, and **off by default**:

```jsonc
"Vehicle": {
  "DataAct": {
    "Enabled": false          // read the portal on a clock, as opposed to only when the button is pressed
  }
}
```

The credentials are the four from
[docs/VW_PORTAL_SETUP.md](docs/VW_PORTAL_SETUP.md) — `Vehicle__DataAct__Username`,
`__Password`, `__Brand` and, with more than one car on the account, `__Vin`. Secrets, supplied via
`.env` or an environment variable exactly as `Solcast__ApiKey` is; the shorter `VW_*` names are honoured
beside them and the sectioned form wins where both are set. Being *blunt about that password*: it is
not a read-only credential. The same account that reads the car's state also unlocks and locates it,
there is no scoped token to use instead, and what protects it here is that `.env` is gitignored and the
client never logs or renders it. That is genuinely all the protection a LAN appliance offers, and
[#137](https://github.com/mpospisil/gleanvolt/issues/137) took the trade knowingly rather than casually.

**Having credentials is not switching it on.** The [`/vehicle-portal`](#vehicle-portal--the-car-from-the-manufacturer-on-demand)
button works as soon as they are set; reading the portal *on a clock* waits for `Enabled`. Press the
button first — that is what proves the credentials and that this car's fields are understood — and
switch the feed on afterwards.

**The interval belongs to the service, and is not a setting.** It asks every fifteen minutes, because
the portal is a batch delivery whose own continuous data request runs at that frequency and asking
faster achieves precisely nothing. No figure the host could pick would be right for both this and a car
that has to be woken up to answer, which is the reason the number lives in the service rather than in
your file.

**One reading is assembled from as many deliveries as it takes.** The portal sends *partial*
deliveries — each carries the reports that changed — so the newest one on its own is a coin toss over
which report type arrives. On the reference ID.4 it held the odometer, the charge target, the doors and
the climate, and no state of charge at all, while the car's SOC was perfectly well known elsewhere at
that moment. So a read takes the newest delivery and keeps merging older ones **only while the reading
still has no state of charge**, up to four (`Vehicle:DataAct:MaxDatasetsPerRead`, an hour of a
fifteen-minute request). A car whose newest delivery carries the battery costs exactly one download, as
it always did.

The merge needs no new rules — several snapshots, sentinels filtered first, newest real value wins is
what the mapper already does — but it does make one thing load-bearing: a reading is dated by **the
newest snapshot that actually contributed to it**, not by the newest one downloaded. A state of charge
from 10:14 stamped with a 10:29 status report's clock would be a stale reading wearing a fresh face,
and how fresh a reading is is the one thing this feed exists to let you judge.

**It holds one session rather than replaying a password.** The button signs in afresh on every press,
which is right by hand and wrong on a schedule — repeatedly replaying a password at a real identity
provider is how accounts get locked. The feed keeps one session and signs in again only when the portal
bounces it, and it **times how long each session lasted** and logs it: nobody knows what a portal
session's life is, because until this nothing had ever kept one.

**A failure the owner has to fix stops the feed rather than slowing it.** A refused password, a consent
screen, an emailed one-time code, a CAPTCHA, or an account with no continuous data request at all: the
service stops asking. Ordinary failures — a 5xx, a timeout, an expired session, a delivery that has not
been filled yet — are `Degraded` instead: they back off (to at most an hour) and clear themselves.

**A stopped feed is said four ways, because it does not clear itself.**

- A band across the top of **every page** of the web UI: *Sign-in required*, the service's sentence, and
  a link to the portal page. It follows the poll, so a browser left open overnight grows one.
- The dashboard's vehicle card, next to the reading it explains.
- **Health**, where "is anything wrong?" is actually asked, as a `Car feed` row.
- The log, as a **warning** — repeated every six hours for as long as it lasts, because a warning
  written once has scrolled out of `docker logs --tail` by the time anybody reads it, and a silent
  stopped feed is indistinguishable from a parked car. The reminder is a log line and never a request:
  nothing is fetched while the feed is blocked.

Plus the [**Car feed**](#what-each-entity-means) entity going to `NeedsOwner`, which is what a Home
Assistant notification keys off. Clear it in a browser, press the portal button to check, and restart
the controller to put the feed back on its clock.

**About that emailed code.** If the portal ever answers with a one-time code, a CAPTCHA or an
authenticator prompt, the client recognises it *before* posting anything and never retries — a password
put into a code form tells nobody anything, and a screen a program cannot answer is not made answerable
by asking again. It is recognised two ways: by the words on the page, and by a field asking for a code
(`otp`, `emailOtp`, `securityCode` and their like), which is what still works on a page in a language
the word list does not cover. On the reference account this has not appeared — six cold sign-ins with
only an email and a password, no code and no CAPTCHA — but the *first* sign-in in a browser does show a
consent screen, and holding one session rather than signing in ninety-six times a day is partly there to
keep new-device challenges rare.

> **With this on, the MQTT vehicle feed is not subscribed.** Two sources writing one reading is a race
> whoever wins it, so the manufacturer's service takes the holder and the MQTT worker is not started at
> all; the controller says so in its startup log. If you were publishing from a Home Assistant
> automation, that automation is now doing nothing — stop it, or leave it and know which one is live.

Everything else about the feed is unchanged: `MaxAge` still judges staleness, nothing in `ChargeControl`
or `BatteryHold` reads it, and an installation with `Enabled: false` behaves exactly as it did before
any of this existed.

### Self-hosted web UI (the `Web` section)

The controller can serve its own UI, as an alternative to Home Assistant or alongside it. It exists
because on a 1 GB board Home Assistant is the binding constraint — it alone uses around 500 MB of the
roughly 905 MB available, which a 4 GB board no longer feels — and because a controller that can be
looked at without a
second application is simpler to reason about. The two surfaces are independent adapters over the
same internal state, so all four combinations run: UI only, MQTT only, both, neither.

**What is built so far.** [Issue #44](https://github.com/mpospisil/gleanvolt/issues/44) lands
in phases. Phase 0 is the plumbing: `/health` shows the running build, the configured time zone, and
the time of the last completed poll — a liveness check (the timestamp updates itself as each poll
lands, so a page that sits still means the poll loop has stopped while the web host is fine).
Phase 1 adds `/`, a read-only telemetry dashboard, since regrouped (see
[The dashboard reports; the plan page decides](#the-dashboard-reports-the-plan-page-decides) below):
every figure carries its meaning inline, since MQTT discovery has nowhere to put one (see
[What each entity means](#what-each-entity-means) above; the wording is the same). Phase 2 adds
optional authentication — a single shared password that gates every page once one is configured, see
[Authentication](#authentication) — landing before phase 3 gives the UI anything that can write to
hardware.

Phase 3 adds the same controls Home Assistant has — a button per strategy with the running mode
reported read-only beside them, the **battery discharge hold** switch (shown only while
`BatteryHold:Enabled` is on) and the runtime numbers (**daily EV target**, **session energy target**,
**minimum battery SOC**, **SOC resume margin**). Phase 5 adds the `Forecasted` mode's day plan as one
coherent view instead of the dozen loosely related entities Home Assistant renders it as: day
outlook, plan state, charge window, EV energy budget, EV energy expected today, projected shortfall,
required SOC floor, forecast remaining today, tomorrow's forecast, forecast accuracy and battery
loaned today, each with an explanation next to it, plus a timeline chart plotting forecast surplus
against the charge window with the required-SOC-floor projection overlaid on a second axis. The
chart's data is computed once, in `Gleanvolt.Core`, by the same `SolarDayPlanner` that builds the plan
itself — the floor projection is the identical formula the live figure uses, evaluated at every
remaining forecast period instead of only the current instant — so the picture can never disagree
with the numbers next to it.

All of it drives the exact same Core interfaces the MQTT worker uses (`IChargeActions`,
`IBatteryHoldSelector`, `IForecastRuntimeSettings`, `ITargetedChargeSelector`, `IFastChargeSelector`), so there is no second
control path and the two surfaces cannot disagree about what the charger is doing — the last one to
press wins, visible on the other within a poll interval. The same semantics apply here as on the MQTT
side: nothing set from the UI persists across a restart, `FastNoBattery` and `Targeted` can switch the
mode back to `Off` on their own once the car finishes (the state line follows, and the "started from
the Web UI at 13:42" note goes with it), a charger that refuses the use-mode write says so on the page
instead of leaving a mode that quietly does nothing, and the battery-hold switch shows the last
command that was actually written to the inverter, not what was requested — a write that fails to take
shows the switch springing back on its own.

#### The system's name, on every page

The header carries the installation's name beside the product's, and so does the browser tab —
`Dashboard — Home Roof` rather than `Dashboard — Gleanvolt`. Two tabs onto two roofs is the case this
is for; the product name is the one thing both of them already agree on. The sign-in page is the
exception, and keeps the product name: it is reached before anyone has signed in.

#### `/vehicle-portal` — the car from the manufacturer, on demand

A button that asks VW's own [EU Data Act portal](docs/VW_PORTAL_SETUP.md) for the car and shows what
came back: battery, range, charge state, plug state and the *car's* capture time; then the delivery it
arrived in; then every field in it that nothing here recognises yet.

**A diagnostic, and the way you prove the credentials.** Nothing polls *this*: each press signs in
afresh, with its own session, and the reading it shows is not written into the dashboard's vehicle card
or into any charging decision. That is what makes it the right thing to press before switching the feed
on — and the right thing to press again after clearing a consent screen, since it costs nothing and
answers the same question.

The feed with its own clock is a separate switch,
[`Vehicle:DataAct:Enabled`](#the-car-from-the-manufacturer-on-a-clock-the-vehicledataact-section), and
it holds a session rather than replaying the password. Setup — the credentials and the browser steps
the portal needs first — is [docs/VW_PORTAL_SETUP.md](docs/VW_PORTAL_SETUP.md).

#### `/pv-system` — the installation, read-only

What this controller is and what it is talking to, from a browser rather than from the startup log:
the system's name, id and address, its coordinates, the array's bearing (with the compass point spelled
out, because `172°` is only checkable by someone who already thinks in degrees), tilt, capacity, loss
factor and commissioning date, and a table of the devices — the inverter and each charger, with model,
address and unit id. See [The PV system](#the-pv-system-the-pv-section) for what each value means.

**Read-only, and not merely for now.** Every value on it is resolved once at startup — the Modbus
clients are constructed from it, and anything unusable has already stopped the host — so a control that
edited it would be editing a copy of a decision that has already been made. Changing it means editing
the configuration and restarting, and the page says so rather than leaving someone hunting for a Save
button.

It is also where the **deprecation notices** live: any older key still supplying a value is listed
under *Configuration to move*. The same lines are logged once at startup, where nobody ever sees them
again.

**And the MQTT links.** The Devices table is what the controller *drives*; the **MQTT** section below
it is what the controller *talks through* — the two links kept apart, because they are separately
optional and may well be two different brokers. For [Home Assistant](#home-assistant-mqtt): the broker
it dials and as whom, the client id the broker's own log and ACL file know this controller by, the
discovery prefix, the unique-id root actually in force, the **topic prefix** — `gleanvolt/home-roof`,
composed at startup from `HomeAssistant:BaseTopic` and `Pv:Id`, and therefore guessable from neither —
the well-known topics under it (battery hold appearing only when the feature is on) with the
`{prefix}/{object_id}/state|set` pattern for everything else, the status interval, and the retirement
lists when an installation carries one. For [vehicle telemetry](#vehicle-telemetry-the-vehicle-section):
the same connection details, the topic the worker *actually subscribed to* (which comes from the car,
not from the feed), the staleness guard and the retry interval. Each link off reads as the default it
is, and says which key turns it on.

**The broker password is shown only when a login is enforced.** `MQTT_PASSWORD` is the account that
publishes to the `…/set` topics — the stop button on the wallbox by another route — and this UI is an
open LAN dashboard until a [`Web:PasswordHash`](#authentication) is configured. So the host hands the
page a null unless one is: the section cannot disclose what it was never given, and says that setting
a password is what makes it readable. Even then it renders masked, behind *Reveal* and *Copy* — a
login is not a closed door, and a shoulder or a screenshot is a different threat from the network.

None of it says whether either link is **up**: every value is read once at startup. That is a
different question, and no page answers it yet.

**And the API.** An **API** section states whether it is on — off is the default, so it says what turns
it on and that a key is part of that — the base URL built from the address *this page* arrived on
rather than from `Web:Port` (a proxy or a hostname in front means the configured port is not
necessarily the one that works, while the address that just delivered the page demonstrably is), links
to the index and the OpenAPI document, the configured keys by name, and the `curl` from the README with
this installation's address and key already in it. The keys are shown on the same terms as the broker
password: only behind a login, masked, with *Reveal* and *Copy*.

#### The dashboard reports; the plan page decides

Those phases left the UI in three places for one question. The dashboard was fourteen telemetry tiles
followed by a column of inputs; `/forecast` held the plan those inputs shape; `/targeted` held a mode
with a form of its own. Reading an outcome and adjusting its input meant changing pages.

**The nav is now Dashboard · Charging plan · Sessions · Energy · PV system · Health.** `/forecast` and
`/targeted` are gone as destinations; what was on them lives on **`/charging-plan`**, one tab per mode.

**`/` reports and no longer decides.** It carries no button, no input and no select at all — three
sections, in the order the questions are actually asked:

- **Energy** — solar power against what the forecast expected of this instant, solar surplus, battery
  SOC and power, grid power. True whatever is or isn't plugged in, which is why it comes first.
- **Vehicle** — the car, because it is *configured*: its name and pack, then the charger's own view of
  whether one is connected, then whatever feed reports on it. The feed is an attachment, and the card
  names which of four situations it is in — no feed configured (nothing is wrong), a reading and its
  age, a reading marked **stale** past `MaxAge`, or **sign-in required** with the sentence saying which
  screen to open. The last two must never look alike: *stale* clears itself and *sign-in required* never
  will. See [the car on a clock](#the-car-from-the-manufacturer-on-a-clock-the-vehicledataact-section).
- **Charging session** — charge mode, control state, charger status, session energy, EV charging power
  and current, target and active current, battery loan power. Shown **only while there is a session to
  report**: a mode is driving, or the car is drawing power under no mode at all (somebody put the
  charger into `Fast` by hand — the one case worth not hiding). Otherwise it is a single line naming
  the mode and the charger's state, and a link to start one, rather than a grid of dashes.

**`/charging-plan` is where charging is decided**, and its shape is a common header over a tab per
mode. The header carries what is true whatever is running — the mode itself, the **Off** that ends it,
and the **battery discharge hold**, which belongs to no single strategy: `FastNoBattery` turns it on
for the length of its charge and off again at the end, `Targeted` arms one of its own while its plan
imports, and it is worth arming by hand under any of them. Every action's note
and every charger refusal lands there too, next to the mode it moved.

Each tab then carries only what its own mode needs, which is what makes the modes legible against each
other:

| Tab | The button | What else is on it |
| --- | --- | --- |
| **Solar** | Charge from surplus | Nothing to configure — the current is whatever the sun leaves over. The surplus, the battery SOC that gates it, and what the car is drawing. |
| **Forecasted** | Charge to the forecast | The four runtime numbers the plan reads, then the day plan itself and the timeline chart. |
| **Fast (no battery)** | Charge at maximum | How much to deliver before it stops — until the car stops, an amount in kWh, or a battery target — and optionally when you need it, which holds the charge back so it finishes just in time. Then what the speed costs: the car's actual draw and current, the setpoint read back, grid power, and the amount delivered so far when there is one. |
| **Targeted** | Preview plan → Start charging / Cancel | What the car says about itself, then what you want — kilowatt-hours or a battery target — the charging priority and its rest level, the departure, the minimum battery SOC the planner works to, and the plan in words and figures *before* the charger moves. |

The tab is in the URL (`/charging-plan/forecasted`), so a bookmark and a refresh come back to the same
mode and the back button walks them; `/charging-plan` with no tab opens on whatever is actually
driving the charger, which is nearly always the one being reached for. A dot in the strip marks the
running mode, so the tab you need is findable without reading the state line above it.

The targeted tab is still the plan **in words** — what will come from the sun and between when, what
the grid will supply and from when, and, when there isn't enough time, how far short it will fall and
the departure that would have covered it. Prose rather than a chart, deliberately: the question at
22:00 is "is my car going to be ready, and why is nothing happening yet?", and no chart answers that
as directly as a sentence. (The timeline chart is a separate story, and will read the same plan's
blocks unchanged.) Its form is validated before anything is set — a positive amount, a departure in
the future and inside the horizon — and composed through the app's configured time zone rather than
the server's clock, so a DST boundary between now and the departure cannot move it by an hour. The
forecast tab shows an explicit empty state while any mode other than `Forecasted` is driving, rather
than the last stale plan, and the targeted tab does the same — but the controls on both stay reachable
whatever is running, because a target is prepared *before* the mode is on.

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
broker at all (roughly 75 MB against ~590 MB for the full stack — 8% of a 1 GB board, or 2% of a
4 GB one).

The host port is published unconditionally, which is the one thing to understand about
`WEB_ENABLED=false` there: the port stays bound on the Pi, but nothing inside the container listens,
so connections are refused. Earlier releases published it from a separate `docker-compose.web.yml`
that had to be merged via `COMPOSE_FILE`; that second step is gone, and the file is kept only so an
existing `COMPOSE_FILE` line doesn't break — merging it is now a no-op.

#### Browsing charging session history

Phase 4 adds `/sessions`: a list of recorded sessions (date, duration, driving strategy, energy
delivered, solar share), each linking to a detail page with the per-source energy split, the day's
forecast total with its p10–p90 band, the weather at each end of the session with the day's daylight
window, and a battery-SOC-over-time chart. It reads through `IChargingSessionStore`'s existing query methods —
nothing here reaches past the interface into SQLite — and degrades to "isn't available right now"
rather than an error page when `SessionStore:Enabled` is off or the file can't be opened. See
[Charging session history](#charging-session-history-the-sessionstore-section) below for what is
actually recorded.

The chart uses [uPlot](https://github.com/leeoniya/uPlot) (MIT licensed), vendored into
`Gleanvolt.Web/wwwroot/lib/` rather than fetched from a CDN — issue #44's decision, so the history stays
readable during an internet outage, which is exactly when a locally controlled system is most worth
looking at.

#### Browsing the energy history

`/energy` shows one recorded day at a time, as the table it is stored as: a row per interval with
solar, forecast, grid each way, energy to the car, the battery each way, the house residual, SOC and
coverage, and a day total beneath them. A date picker and prev/next buttons move the window; the
"next" button stops at today.

A **table rather than a chart, on purpose.** The point of this store is that the figures are exact and
that a partial row is *visibly* partial — a row that covers less than the full interval is marked and
counted in a note under the table, and a window no forecast covered shows an em dash rather than
`0.00`. A chart would smooth over precisely the two things worth seeing first. It reads through
`IEnergyIntervalStore.GetIntervalsAsync` and degrades to "isn't available right now" when
`EnergyMonitor:Enabled` is off or the file can't be opened, exactly as `/sessions` does.

Nothing was added to Home Assistant. This is history, not telemetry — see
[Energy history](#energy-history-the-energymonitor-section) below for what is recorded.

The published container image is now based on `dotnet/aspnet` rather than `dotnet/runtime` — about
25 MB more, on every platform, whether or not the UI is enabled. The framework reference is fixed at
build time, so there is no variant that avoids it.

### HTTP API (the `Api` section)

A third control surface beside Home Assistant and the web UI, for **programs rather than people**:
everything the controller can see, and the three actions that change something, described by
[OpenAPI](https://www.openapis.org/) so a client is generated from the document rather than written
against a moving target.

It exists for one use case in particular — an **MCP server**, so an LLM can answer questions about
this installation and act on it. That server is a separate process in a separate repository, and
nothing here is specific to it: the test of whether this API is the right shape is that it would suit
a script, a dashboard or an agent equally.

Like the web UI, it is an adapter over the same Core seams the MQTT worker drives
(`ChargeControlStatusHolder`, `IChargeActions`, `ITargetedChargePreview`, `ITargetedChargeSelector`,
`IBatteryHoldSelector`) and owns no control logic of its own. It invents no capability: every action
below is a button the web UI already has.

#### Switched on knowingly, and never open

| Setting | Default | Meaning |
|---|---|---|
| `Api:Enabled` | `false` | Master switch. While false **no route is mapped and no document is served** — there is nothing to find, not merely nothing permitted. |
| `Api:Keys` | *(empty)* | The keys that may call it, as `name → secret`. Enabled with none configured is a **startup failure**. |
| `Api:MaxQueryRange` | `31.00:00:00` | The widest span one history query may ask for. |
| `Api:MaxSessions` | `500` | The most sessions one listing may return. |

The API shares the web UI's port (`Web:Port`, 8090) — this is one appliance, not two services that
happen to be co-hosted — and either surface being enabled is what makes the process listen at all.

Keys are stored as the secret itself and supplied out-of-band through `.env` or an environment
variable, exactly like the broker password and the Solcast key:

```bash
Api__Enabled=true
Api__Keys__claude-mcp=$(openssl rand -hex 32)
```

The web UI's password is hashed because it is a password a *human* chose and may have reused; these
are generated, single-purpose and high-entropy, and a slow KDF on every request would buy nothing
against an attacker who can already reach the port. The **name** is not a credential — it is what
reaches the log and the recorded charging session as the source of an action, so
`Api__Keys__claude-mcp` produces *"API (claude-mcp) started Targeted"* rather than an anonymous
write. Several clients therefore get several keys.

Present one as a bearer token on every call that does anything:

```bash
curl http://gleanvolt.local:8090/api/v1/            # no key: what this is, and what it serves
curl -H "Authorization: Bearer $API_KEY" http://gleanvolt.local:8090/api/v1/status
```

All of it is **readable back** at [`/pv-system`](#pv-system--the-installation-read-only), including
that second line with this installation's own address and key already substituted in. The key itself is
shown only when the UI is behind a [login](#authentication); without one the page shows the names and
says what makes the secrets readable, because a key is bearer-equivalent to the stop button on the
wallbox and the UI is open on the LAN by default.

**Why the API defaults off when the UI defaults on.** Two of these endpoints write to hardware, and
the project's rule for anything that writes is that an operator switches it on knowingly. The UI can
afford to be an open LAN dashboard because it is a browser with a person in front of it; a
non-interactive control surface that any program on the network can drive cannot. A key is
bearer-equivalent to the stop button on the wallbox — treat it that way.

#### What it exposes

Everything is under `/api/v1/`. **Two endpoints need no key**, because a browser cannot send one and
neither of them carries anything to act on: `/api/v1/` itself, which says what this is, which
operations exist and how to authenticate, and the OpenAPI document at **`/api/v1/openapi.json`**,
which is the thing a client is generated from. Everything else is behind the key.

| Endpoint | Answers |
|---|---|
| `GET /` | What this is, where the document is, how to authenticate, and every operation this build serves. **No key.** |
| `GET /site` | Which installation this is: name, id, address, coordinates, the array's bearing, tilt, capacity and loss factor, and the devices it is made of with their models and addresses. Ask it first — every other endpoint reports what the controller is *doing*, and two installations answer those identically. |
| `GET /status` | The live snapshot: mode, state, PV, grid, battery power and SOC, EV power and current, the hold, session energy, the running plan. 503 until the first poll completes. |
| `GET /health` | Which system is answering, the build version, age of the last poll, whether a forecast and a vehicle reading are in hand, whether the two history databases can be read. |
| `GET /energy/intervals?from=&to=` | The recorded series — solar, forecast solar, import, export, EV, battery in and out, the SOC band, and each window's **coverage**. Defaults to the last 24 hours. |
| `GET /energy/days/{date}` | One local day added up, so "how was Tuesday?" is one call rather than 96 rows. |
| `GET /sessions?from=&to=&limit=` | Charging sessions, newest first, with the energy split by source. Defaults to the last 30 days. |
| `GET /sessions/{id}` | One session in full: every recorded poll and every notable moment. |
| `GET /forecast` | Today and tomorrow period by period, from the cached forecast the poll loop is deciding on. `?weather=true` also fetches current conditions. |
| `GET /vehicle` | What the car last said — SOC, range, plug and charge state — **and how old the reading is**. |
| `POST /plans/targeted/preview` | What a targeted charge *would* do. Writes to nothing. |
| `POST /charging/start` | Start a mode (`solar`, `forecasted`, `fastNoBattery`, `targeted`). `targeted` needs a `target`; `fastNoBattery` takes an optional `fast` amount and departure. |
| `POST /charging/start/targeted` | Start a targeted charge under an edited plan's limits. Same body as the preview. |
| `POST /charging/stop` | Stop charging and clear any standing target. |
| `PUT /battery-hold` | Arm or release the battery discharge hold. |

Two rules run across the read endpoints. **Ranges are bounded** — a caller will cheerfully ask for a
year of quarter-hours, and this runs on a Raspberry Pi. And **staleness is reported rather than
hidden**: `/vehicle` carries `ageSeconds` and `stale` beside the state of charge, because a cloud
reading arrives hours late as a matter of course and a caller that cannot see the clock will
otherwise treat it as current.

#### Quoting a plan without starting one

The endpoint this API is worth building for:

```bash
curl -sS -X POST http://gleanvolt.local:8090/api/v1/plans/targeted/preview \
  -H "Authorization: Bearer $API_KEY" -H 'content-type: application/json' \
  -d '{"targetSocPercent": 80, "departBy": "2026-08-24T07:00:00+02:00", "priority": "cheapest"}'
```

It returns the same plan the running mode would report — the strategy, the pace the charger must
hold, the solar and grid shares, the forecast surplus in the window, when the import would start,
what will actually arrive and, when it cannot be met, how far short and the departure that *would*
have covered it. Ask in `energyKWh` instead of `targetSocPercent` on an installation with no vehicle
feed; the conversion, the horizon check and the just-in-time split are the same
[`TargetedChargeRequestFactory`](src/Gleanvolt.Core/Strategies/TargetedChargeRequestFactory.cs) the web
form goes through, so a quote and the promise made from it can never disagree.

#### Editing a quoted plan, and charging to it

Every quote carries an **`editable`** object: the parts of the plan you may change, in the shape you
send back.

```jsonc
"editable": {
  "planId": "…",                     // advisory; see below
  "notBefore": null,                 // the charger may not run before this
  "notAfter": null,                  // ...nor after it
  "forbiddenWindows": null,          // stretches that must stay idle
  "maxGridEnergyWh": null            // the most that may be bought
}
```

Change a field, put the whole thing back under `editable`, and either **quote it again** (the same
preview endpoint) or **charge to it**:

```bash
curl -sS -X POST http://gleanvolt.local:8090/api/v1/charging/start/targeted   -H "Authorization: Bearer $API_KEY" -H 'content-type: application/json'   -d '{"energyKWh": 22, "departBy": "2026-08-27T07:00:00+02:00",
       "editable": {"notBefore": "2026-08-27T02:00:00+02:00", "maxGridEnergyWh": 8000}}'
```

The preview and the start take the **same body**, so what you quote is what you commit to — the limits
included. Sending an unedited quote straight back is a no-op: you get the plan you were shown.

> **What you edit are limits, not a schedule — and that is the feature, not a simplification of it.**
> The plan is rebuilt on every poll from a refreshed forecast and the measured delivery, which is what
> lets a sunnier afternoon than forecast shrink the grid block before any of it is bought. Hand back a
> list of blocks to replay and that stops happening — and worse, the blocks go on being executed
> against a delivered-energy figure that stopped being true the moment they were quoted, buying grid
> for energy already in the car. So the window is yours; what happens inside it is still the
> forecast's to decide.

- **A limit may reduce what is delivered. It may never make the plan lie about what will be.** Anything
  a limit puts out of reach comes back as `shortfallWh`, exactly as a departure that is too close
  already does. `maxGridEnergyWh: 0` is a real value and means *sun only* — the request is met from the
  roof or not at all, and the rest is reported rather than quietly imported.
- **Only the impossible is refused.** Limits leaving the charger no time at all to run are a 400 with
  the reason, before anything starts. Limits that merely make the request *partial* are accepted —
  "buy at most 8 kWh and I'll take what that gets me" is a legitimate thing to ask for.
- **`planId` is advisory.** Send it back and the response's `forecastMovedSinceQuote` says whether the
  forecast has been refreshed since you were shown that plan. Nothing is stored server-side, nothing
  expires, and a start never fails because of it.
- **The limits follow the request's rules**: cleared by `/charging/stop`, and not surviving a restart.

`POST /charging/start` with `mode: "targeted"` accepts the same `editable` inside its `target`, so an
existing caller gains this without moving endpoints.

It goes through `ITargetedChargePreview`, which **sets no request, selects no mode and writes to no
device**, and it does not disturb a plan already running. So *"what does 80% by seven cost, and would
leaving at eight make it free?"* is three calls and no hardware writes — a question that otherwise
needs a person to fill in a form three times. Under `justInTime` the response also carries the same
request priced as cheaply as possible, so what holding the last stretch back costs can be read off
the difference in `gridEnergyWh`.

#### Starting one

```bash
curl -sS -X POST http://gleanvolt.local:8090/api/v1/charging/start \
  -H "Authorization: Bearer $API_KEY" -H 'content-type: application/json' \
  -d '{"mode": "targeted", "target": {"energyKWh": 22, "departBy": "2026-08-24T07:00:00+02:00"}}'
```

One call does what one button does: the charger's Fast use-mode is written and the mode is then
selected, so it works on a charger sitting in Green. For `targeted` the request is set **before** the
mode — the controller reads both in the same cycle — and dropped again if the charger refuses.

`fastNoBattery` takes an optional `fast` on the same terms — set first, dropped if the charger
refuses. It says **when to stop**, not how to charge:

```bash
# 20 kWh, flat out, then stop.
-d '{"mode": "fastNoBattery", "fast": {"basis": "energy", "energyKWh": 20}}'

# To 60% of the pack, converted once, here.
-d '{"mode": "fastNoBattery", "fast": {"basis": "soc", "targetSocPercent": 60}}'

# Until the car says stop -- the default, and what omitting "fast" entirely means.
-d '{"mode": "fastNoBattery"}'
```

Add `departBy` and the charge is deferred instead — it starts as late as it can and still finish in
time:

```bash
-d '{"mode": "fastNoBattery", "fast": {"basis": "soc", "targetSocPercent": 90,
     "departBy": "2026-08-27T07:00:00+02:00"}}'
```

An amount that cannot be honoured is a **400 with the reason** — a battery target on an installation
with no configured pack capacity, no reading from the car, a car already past the figure asked
for, a departure in the past or beyond the horizon, or a departure with `full` and so nothing to time
— and nothing is started. Progress comes back on the status as `fastCharge`, with the schedule under
`fastCharge.schedule`, and a `full` start clears any amount left standing from an earlier charge.

Every action returns what it did *and the controller's state afterwards*, so a caller never has to
poll to find out what happened. A refused hardware write comes back as **200 with
`succeeded: false`** and a message rather than an HTTP error: the call was understood and the
controller is in a well-defined state — exactly the one it was in before. Read the flag, not the
status code.

`PUT /battery-hold` is refused with 409 when `BatteryHold:Enabled` is false, rather than recording an
intent that silently does nothing. And what comes back as `batteryHold.active` is **what was last
written to the inverter, not a read-back** — the command register cannot be read — so judge whether a
hold is really in force by `batteryPowerWatts`, never by the flag.

#### The document is the deliverable

For a human client the OpenAPI file is documentation; for a generated MCP tool surface it is the
*entire* interface, and the descriptions are what a model reads before choosing a tool. So:

- **The DTOs are the API's own**, not Core records serialised straight out. Core records change for
  internal reasons; the wire contract must not move when they do.
- **The XML comments are the descriptions.** What is written next to a type, a property or an enum
  member in C# is what reaches the document. `Directory.Build.props` turns the documentation file on
  for **every** project, unconditionally, because that is early enough for the SDK to act on — set in
  `Directory.Build.targets` instead, the flag reads `true`, the compiler writes the `.xml` into `obj/`,
  and nothing ever copies it next to the assembly. That is documentation which exists, reports itself
  as enabled, and cannot be found. It is how every enum in `Gleanvolt.Core` reached the document with
  no description at all (#126).
- **Enums carry a legend**, because .NET's generator does not give them one: a hoisted enum arrives as
  a bare list of values, and neither the type's summary nor any member's survives. A document
  transformer reads the same XML at runtime and adds both, naming each value as it appears **on the
  wire**. It only ever fills blanks — a description the generator produced is left untouched — so the
  day the SDK does this itself, it quietly stops having anything to do.
- **Units live in the names** (`...Wh`, `...Watts`, `...Percent`), because a model that has to guess
  whether a number is watts or kilowatts will guess wrong. Timestamps are ISO-8601 **with an offset**:
  a departure time here is a local-time promise.
- **Enums cross the wire as camel-cased names**, closed, so a reordered member cannot silently change
  what a stored request meant.
- **The contract is pinned by a test.**
  [`OpenApiContract.json`](tests/Gleanvolt.Api.Tests/OpenApiContract.json) records every operation id,
  parameter, response code and schema property with its type and nullability. An intended change shows
  up as a reviewable diff (regenerate with
  `GLEANVOLT_UPDATE_OPENAPI_CONTRACT=1 dotnet test tests/Gleanvolt.Api.Tests`); an accidental one — a
  renamed property, a dropped nullable — fails the build. Formatting changes from a new SDK do not,
  which is what keeps the check honest rather than merely noisy.

#### What it deliberately does not do

- **Stop the service.** Stopping the whole controller is a deliberate, physical act with a deploy-stack
  consequence (it stays stopped); it lives on the surfaces a person is looking at.
- **Write raw Modbus registers.** That is a debugging tool, and a way to brick an inverter over the
  network.
- **TLS, users, per-key scopes, rate limiting.** A LAN appliance with a shared secret, as the web UI
  already is. Scopes become interesting the day a read-only key is wanted.
- **Bundle a Swagger or Scalar viewer.** The document plus `curl` is enough to start.

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

A session **opens** when a controlling mode (`Solar`, `Forecasted`, `FastNoBattery`, `Targeted`) is
driving a connected car, and **closes** when that stops being true — the mode returns to `Off`, the
controller ends itself because the car is full or because the amount asked for has been delivered, the
car is unplugged, or the service stops.

Switching mode mid-session does **not** start a new one. "Forecasted all afternoon, then
`FastNoBattery` at 17:00" is one story about one car, and it is recorded as one session with a
`ModeChanged` event in it.

Note this is a *different* span from the one `Session energy` reports in Home Assistant, which counts
from the moment the car was plugged in whether or not anything is controlling it.

#### What is recorded

| | |
|---|---|
| **Session header** | start/end time (UTC, plus the IANA zone so a viewer can bucket by local day), start and end mode, why it ended, start/end SOC, peak power, the totals below, the forecast day plan as it stood at the start, **the whole day's forecast curve**, and **the weather at each end of the session** (both below) |
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

#### The day the session ran on

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
drives decisions — and the [session detail page](#browsing-charging-session-history) shows the day's
total with its band beside the other header facts.

This is database schema **v3** and `schemaVersion` 3, additive again: older rows keep `NULL`, which is
the truth for them.

#### The weather a session ran in

The day forecast above says what the sun was *expected* to do. This says what the sky **did** — because a session that under-delivered against a confident forecast otherwise has no explanation attached to it, and cloud that rolled in, a cold morning and snow on the panels all look identical afterwards.

Configured via the [`Weather` section](#weather-the-weather-section-optional); `NULL` throughout when it isn't, which is the default.

| Field | What it is |
|---|---|
| `WeatherAtStart` | The conditions when the session opened |
| `WeatherAtEnd` | The conditions when it closed. `null` while a session is open, and when that fetch failed — neither is an error |
| `Sunrise` / `Sunset` | The daylight bounds of the day it **started** on |

Each reading carries `TemperatureCelsius`, `PressureHpa`, `HumidityPercent`, `CloudsPercent`, `VisibilityMetres` (`null` when unreported), `Condition` (`Clear`, `Rain`, `Snow`…), `ConditionDescription` (`light rain`) and `ObservedAt` — the time the *provider* stamped the reading, since these are typically a few minutes old and a reading without its own timestamp can't be told from a live one.

**Two readings, not one.** A six-hour session can finish in entirely different weather from the one it started in, and only the *pair* can say so — the same reason the header has both a start and an end SOC. **Sunrise and sunset are stored once**, because they belong to the day rather than to either reading; a session running across midnight carries the starting day's pair, the same rule `StartedAt` and the day forecast already follow.

These are **columns, not a JSON document** — unlike the forecast curve, which is written once and read whole. Cloud cover and temperature are the axes the analysis groups by, and "solar share against cloud cover" should be SQL rather than a parse per row.

This is database schema **v4** and `schemaVersion` 4, additive: older rows keep `NULL`.

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

### Energy history (the `EnergyMonitor` section)

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

#### One row per quarter hour

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

#### Nothing is netted, so the balance closes

Import and export are separate columns, and so are battery charge and discharge. A quarter hour that
exported 0.4 kWh and imported 0.4 kWh is not the same event as one that did neither, and a net of zero
cannot tell them apart afterwards.

Keeping them apart is also what makes the row *balance*, so house consumption needs no column of its
own — it is the residual:

```
house load = solar + import - export - battery charge + battery discharge
other loads = house load - ev
```

#### `covered_seconds` is not optional reading

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

#### Why its own database file

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

#### Operational notes

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
- **A viewer is built in.** The web UI's `/energy` page shows a day at a time — see
  [Browsing the energy history](#browsing-the-energy-history) above.

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
