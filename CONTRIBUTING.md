# Contributing

Bug reports, hardware observations and register-map corrections are the most valuable things you can
send. This project talks to real inverters whose firmware varies between generations, and a report of
"register 0x25 reads differently on my X3-HYB-G4 with firmware N" is worth more than most code.

## Before you open a pull request

**Sign off your commits.** Every commit needs a `Signed-off-by` line, which `git commit -s` adds:

```bash
git commit -s -m "Your message"
```

That line means you have read and agreed to the [Contributor Licence Agreement](CLA.md). It matters
here more than in most projects: the licence offers commercial use separately, and that is only
possible if one person can license the whole codebase. See [CLA.md](CLA.md) for the reasoning — you
keep your copyright either way.

## Building and running from source

You need:

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Network access to the inverter and the EV charger, with Modbus TCP enabled (or, for a go-e, its local HTTP API)

```bash
dotnet build Gleanvolt.slnx
dotnet test Gleanvolt.slnx
dotnet run --project src/Gleanvolt.Worker
```

For local settings, copy `.env.example` to `.env` in the repository root: it is gitignored and loaded
at startup, from `dotnet run` and the debugger alike. Secrets can also go in .NET user-secrets
(`dotnet user-secrets set "Solcast:ApiKey" "<key>"` in `src/Gleanvolt.Worker`). Every local start
fetches the Solcast forecast once: with your installation's key, that comes out of its daily quota.

Then open `http://localhost:8090`: a fresh clone starts in [setup mode](docs/WEB_UI.md#setup-mode), which asks for
a password and runs the [setup wizard](docs/WEB_UI.md#the-setup-wizard) for the device addresses and the site (or use
[the command line](docs/WEB_UI.md#setting-up-from-the-command-line)). Nothing is polled until that is done. After
that, nothing is written to either device either: the service always boots with the charge mode **Off** and the battery hold
**off**, and `BatteryHold:Enabled` is `false` as well, so it only polls and logs. That is the
recommended way to confirm the telemetry looks right before enabling anything that writes.

## What the code expects

- **Read [`docs/DECISIONS.md`](docs/DECISIONS.md) first** if you are changing anything structural. It
  records why things are the way they are, and several of the answers are non-obvious.
- **Respect the layering.** `Gleanvolt.Worker` → `Gleanvolt.Hosting` → `Gleanvolt.Infrastructure` → `Gleanvolt.Core`,
  one way only. Decision logic lives in `Gleanvolt.Core`, expressed against interfaces, so it stays
  testable with no hardware. The full rules are under [Layering rules](#layering-rules).
- **Comments explain *why*, not *what*.** The existing ones are dense on purpose: most of them record
  a fact about the hardware, a failure that was actually observed, or an alternative that was tried
  and rejected. Match that, and prefer adding the reason to restating the code.
- **Tests come with the change.** `dotnet test Gleanvolt.slnx` must pass. Control logic is
  tested against mocked `IModbusClient`, never a live device.

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
│   ├── Gleanvolt.Devices.Abstractions/ # The driver contract: IModbusClient, IInverterDriver, the battery hold
│   ├── Gleanvolt.Devices.Solax/        # The SolaX Gen4 inverter driver: register map, decoding, power control
│   ├── Gleanvolt.Devices.Huawei/       # The Huawei SUN2000 driver: readings, the EMMA topology, the battery hold; and the SCharger
│   ├── Gleanvolt.Devices.GoE/          # The go-e Charger driver, over its local HTTP API v2
│   │
│   ├── Gleanvolt.Core/                 # Domain logic, free of hardware and hosting
│   │   ├── Models/                 # Strongly typed models (EnergyState, DeviceConfig, ...)
│   │   ├── Enums/                  # Charger registers and modes, control states
│   │   ├── Strategies/             # Pure decision logic (charging controller, discharge hold, smoothing)
│   │   └── Interfaces/             # IChargingController, IEnergyStateReader, ...
│   │
│   ├── Gleanvolt.Infrastructure/       # External communication
│   │   ├── Modbus/                 # Concrete Modbus TCP client (and a read-only decorator)
│   │   ├── RegisterMaps/           # Hex address mappings for the SolaX EV charger
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
│   ├── Gleanvolt.Discovery/            # The LAN scan: finds the SolaX and Huawei devices and their Modbus address
│   ├── Gleanvolt.Discovery.Cli/        # gleanvolt-discover, a thin console over Discovery's DiscoverCommand
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
│   ├── Gleanvolt.Web.Screenshots/      # Renders the pages the user manual's screenshots show
│   ├── Gleanvolt.Hosting.Tests/        # Coordinator, selector and HA discovery tests
│   ├── Gleanvolt.Discovery.Tests/      # The scan against a fake Modbus TCP server
│   ├── Gleanvolt.Devices.Testing/      # The driver test kit: Modbus and HTTP replays, and the conformance tests
│   ├── Gleanvolt.Devices.Tests/        # The dump format, the snapshot and the replay
│   ├── Gleanvolt.Devices.Solax.Tests/  # The SolaX driver against the conformance tests and its recordings
│   ├── Gleanvolt.Devices.Huawei.Tests/ # The Huawei drivers against the same tests, on synthetic recordings
│   └── Gleanvolt.Devices.GoE.Tests/    # The go-e driver against the charger tests, on synthetic HTTP recordings
├── deploy/                         # Raspberry Pi production stack (compose, broker config, deploy.sh)
├── dev/homeassistant/              # Local HA + MQTT dev stack (anonymous broker, host-run worker)
├── Dockerfile                      # Cross-compiled linux/arm64 image for the Pi
└── docs/                           # DECISIONS.md, IMPLEMENTATION_LOG.md and the user guides
    └── manual/                     # The user manual: a Starlight site, published to GitHub Pages
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
- **`Gleanvolt.Discovery` references `Gleanvolt.Infrastructure` and nothing above it**, so the scan
  runs with no Gleanvolt installed: the Modbus client and the register maps, never the composition root
  or the configuration. `Gleanvolt.Discovery.Cli` is its console and nothing else.
- **`Gleanvolt.Core.Tests` mocks the hardware boundary** (`IModbusClient`, etc.) to exercise control logic without a live device.

## A local Home Assistant to develop against

A ready-to-run broker + Home Assistant for local development lives in [`dev/homeassistant/`](dev/homeassistant/) (`docker compose up -d`). Watch the traffic with:

```bash
docker exec -it solax-dev-mosquitto mosquitto_sub -t 'homeassistant/#' -t 'gleanvolt/#' -v
```

## Building and testing the Linux package

`release.yml` builds, installs, smoke-tests and purges the package on both architectures on every
run, so this is only for working on the packaging itself.

```bash
# 1. A self-contained publish at the fixed path nfpm.yaml reads
dotnet publish src/Gleanvolt.Worker/Gleanvolt.Worker.csproj -c Release -r linux-x64 \
  --self-contained true -o publish/deb
chmod -R u=rwX,go=rX publish/deb

# 2. The package (nfpm: https://nfpm.goreleaser.com/install/)
GLEANVOLT_VERSION=1.0.0~dev GLEANVOLT_ARCH=amd64 \
  nfpm package --config packaging/linux/nfpm.yaml --packager deb --target deb/

# 3. Install it somewhere systemd runs. A throwaway container works:
docker run -d --name gv --privileged --cgroupns=host -v /sys/fs/cgroup:/sys/fs/cgroup:rw \
  -v "$PWD/deb:/deb:ro" <an image with systemd as PID 1>
docker exec gv apt-get install -y /deb/gleanvolt_1.0.0~dev_amd64.deb
```

The version uses `~dev`, not `-dev`: dpkg sorts `1.0.7~dev` below `1.0.7`, so a test build never
outranks the release that follows it.

`PackagingTests` guards the one list here that drifts: every `…Path` setting in `appsettings.json`
must be pointed at `/var/lib/gleanvolt` or `/var/log/gleanvolt` by `gleanvolt.service`, because the
content root in `/opt/gleanvolt` is read-only to the service.

## The HTTP API's contract

For a generated MCP tool surface the OpenAPI document is the *entire* interface, and its descriptions
are what a model reads before choosing a tool. So:

- **The DTOs are the API's own** (`Gleanvolt.Api/Contracts`), not Core records serialised straight out:
  the wire contract must not move when Core changes for internal reasons.
- **The XML comments are the descriptions.** What is written on a type, a property or an enum member
  reaches the document. `Directory.Build.props` turns the documentation file on for every project; set
  in `Directory.Build.targets` instead, the `.xml` is written to `obj/` and never copied next to the
  assembly, so the descriptions silently vanish.
- **Enums get a legend** from a document transformer, which fills in the type's and members'
  descriptions that .NET's generator drops, naming each value as it appears on the wire.
- **Units live in the names** (`...Wh`, `...Watts`, `...Percent`); timestamps carry an offset; enums
  cross the wire as closed camel-cased names.
- **The contract is pinned by a test.**
  [`OpenApiContract.json`](tests/Gleanvolt.Api.Tests/OpenApiContract.json) records every operation id,
  parameter, response code and schema property with its type and nullability. An intended change is a
  reviewable diff — regenerate with
  `GLEANVOLT_UPDATE_OPENAPI_CONTRACT=1 dotnet test tests/Gleanvolt.Api.Tests` — and an accidental one
  fails the build.

## Anything that writes to hardware

`ChargeControl` and `BatteryHold` are the two features that write — to the EV charger and to the
inverter respectively. Both ship disabled, and `BatteryHold` additionally defaults to dry-run,
because a wrong register address on a hybrid inverter is not a failing test but a house with no power.

If your change touches a write path:

- Say in the pull request **which hardware and firmware you verified it against**, or say plainly that
  you did not.
- Keep the defaults off. A feature that writes must be something the operator switched on knowingly.
- Do not widen what a `ReadOnlyModbusClient` protects. The dry-run guarantee is structural — a client
  that may not write physically cannot — and it should stay that way.

## The user manual

[`docs/manual`](docs/manual/README.md) is the user manual, built with Node, separately from the .NET
build. Its screenshots are generated from the real pages. A pull request that changes what one of them
shows fails the manual's screenshot check: run `npm run screenshots` in `docs/manual` and commit the
PNGs. The README there has the rest.

## Cutting a release

Not something a pull request does, and not something a tag does either: `release.yml` is dispatched,
and it writes the tag itself once everything it built has been started and passed. See
[`docs/RELEASING.md`](docs/RELEASING.md) before reaching for `git tag`.

## The libraries as packages

A release is a dispatch of [`release.yml`](.github/workflows/release.yml), which decides the build number itself, publishes self-contained builds for Windows, Raspberry Pi and x64 Linux — no .NET installation needed — alongside the five libraries as `.nupkg` files, and only then tags the commit and creates the [GitHub Release](https://github.com/mpospisil/gleanvolt/releases). One job per target, on the platform it names, so a broken platform names itself and every binary is started before it ships. `Gleanvolt.Worker` is not packaged: it is the thing that runs the libraries, not one of them.

Every release carries a `SHA256SUMS` file — `sha256sum -c --ignore-missing SHA256SUMS` in the directory you downloaded into — and each zip and `.nupkg` carries a build provenance attestation, so `gh attestation verify <file> --repo mpospisil/gleanvolt` says which workflow run and which commit produced it without taking the release page's word for anything.

The packages are attached to the release rather than pushed to a feed. To build on the controller directly, take this repository as a git submodule and reference the projects — no feed, no credentials, and the submodule commit pins the version exactly.

How a release is cut — and what to check before and after one — is [`docs/RELEASING.md`](docs/RELEASING.md). The short version: dispatch the workflow, do not reach for `git tag`.

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddGleanvolt();          // polling, control, sessions, Home Assistant, the web UI, the API

var app = builder.Build();
app.UseGleanvolt();              // the UI's and the API's endpoints, when they are enabled
app.Run();
```

`AddGleanvolt` also has an `IServiceCollection` overload taking an `IConfiguration`, for a host that is not built on `WebApplicationBuilder`.

## Reporting a problem instead

An issue with the controller's log around the event, the device model and its firmware version, and
what you expected to happen is a complete report. Redact your Solcast key and any broker credentials
before pasting a log.
