# Gleanvolt

[![CI](https://github.com/mpospisil/gleanvolt/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/mpospisil/gleanvolt/actions/workflows/ci.yml?query=branch%3Amain)
[![Publish image](https://github.com/mpospisil/gleanvolt/actions/workflows/publish-image.yml/badge.svg?branch=main)](https://github.com/mpospisil/gleanvolt/actions/workflows/publish-image.yml?query=branch%3Amain)

**Gleanvolt charges your electric car from your own solar power.** It talks directly to your hybrid
inverter and your EV charger on your home network, decides how much current the car should take from
moment to moment, and keeps the home battery out of it when you want it to. No cloud account, no
vendor app, nothing leaving your network.

It runs on a Raspberry Pi, a NAS, any 64-bit Linux machine or Windows, and you use it from its own
web UI. An HTTP API and Home Assistant integration are there if you want them.

**[The user manual](https://mpospisil.github.io/gleanvolt/latest/en/)** takes you through installing, setting up and using it, step by step,
with screenshots. It is in English, and a Czech translation is under way.

## What it does

**Charge modes** — pick one from the web UI, Home Assistant or the API:

| Mode | What you get |
|---|---|
| **Solar** | The car takes whatever the house doesn't use, once the home battery is full. |
| [**Forecasted**](docs/MODE_FORECASTED.md) | The car starts earlier, on the day's solar forecast, while the home battery is still planned to be full by evening. |
| [**Solar + grid**](docs/MODE_SOLAR_GRID.md) | The sun, with a little grid to make a weak sun usable; it stops by itself when the day's sun is over. |
| [**Fast (no battery)**](docs/MODE_FAST_NO_BATTERY.md) | "I leave in an hour": maximum current from sun and grid, the home battery left alone. Optionally an amount, and a departure time to finish just in time. |
| [**Targeted**](docs/MODE_TARGETED.md) | "22 kWh — or 80% — by 07:00, with as little grid as possible": paced to use the most sun. You see the plan before it starts. |

Every mode stops by itself when the car is full or unplugged. The service always starts with charging
**off** and writes nothing until you press a button.

**And around them:**

- **[Battery discharge hold](docs/BATTERY_HOLD.md)** — the home battery stops serving the house, so the
  car charges from sun and grid, while the battery can still charge from surplus.
- **[Grid tariff](docs/CONFIGURATION.md#grid-tariff-the-tariff-section)** — enter your low-tariff
  hours (Czech HDO) and let `Fast` and `Solar + grid` wait for them.
- **[Web UI](docs/WEB_UI.md)** — live figures, the charging plan, the forecast, every control, and the
  history of every charge and every 15 minutes of your site. On by default, behind a password.
- **[The car's own battery level](docs/VEHICLE.md)** — from volkswagen.de, MyŠkoda, the VW Group EU
  Data Act portal, or any car Home Assistant can see, so a target can be a percentage.
- **[HTTP API](docs/HTTP_API.md)** — everything the web UI can do, described by OpenAPI, for scripts
  and for an MCP server that lets an AI assistant read and drive it.
- **[Home Assistant](docs/HOME_ASSISTANT.md)** (optional) — a device with a button per mode and a
  sensor for every figure, over MQTT discovery.
- **Local** — it keeps working through an internet outage. The only outside services are the
  optional solar forecast (Solcast), weather and the car's manufacturer.

## Supported hardware

**Inverters** — over Modbus TCP:

<!-- include: docs/SUPPORTED_INVERTERS.md#inverters-at-a-glance -->
| Manufacturer | Models | Driver | Tier |
|---|---|---|---|
| [SolaX](https://github.com/mpospisil/gleanvolt/blob/main/docs/SUPPORTED_INVERTERS.md#solax-inverters) | **X3-HYB-G4 PRO**; X3/X1-Hybrid G4, X1/X3-IES, X3-Ultra, X3-Aelio, X1-VAST; G2/G3 hybrids | `solax` | **Verified** (G4 PRO); Expected; readings and charging only (G2/G3) |
| [Huawei](https://github.com/mpospisil/gleanvolt/blob/main/docs/SUPPORTED_INVERTERS.md#huawei-sun2000) | SUN2000 KTL-M1, K-MAP0, KTL-L1, K-LC0, also behind an EMMA | `huawei` | Expected — not yet run on a device |
| [Tigo](https://github.com/mpospisil/gleanvolt/blob/main/docs/SUPPORTED_INVERTERS.md#tigo), [Qcells](https://github.com/mpospisil/gleanvolt/blob/main/docs/SUPPORTED_INVERTERS.md#qcells) | TSI X3, Q.VOLT HYB-G3-3P — SolaX inside | `solax` | Expected |
| [Dražice](https://github.com/mpospisil/gleanvolt/blob/main/docs/SUPPORTED_INVERTERS.md#dražice-dz-dražice) | IN.HYBRID COMPACT, COMPACT PRO, COMPACT S, COMPACT M — SolaX models by datasheet | `solax` | Expected, if the unit is SolaX hardware |
| Others | — | — | Not supported yet (GoodWe is planned) |
<!-- end include -->

**EV chargers** — over Modbus TCP, or a go-e over its local HTTP API:

<!-- include: docs/SUPPORTED_CHARGERS.md#chargers-at-a-glance -->
| Manufacturer | Models | Driver | Tier |
|---|---|---|---|
| [SolaX](https://github.com/mpospisil/gleanvolt/blob/main/docs/SUPPORTED_CHARGERS.md#solax-chargers) | **X1/X3-HAC**; the other HAC variants; X1/X3-EVC | `solax` | **Verified** (X1/X3-HAC); Expected |
| [Huawei](https://github.com/mpospisil/gleanvolt/blob/main/docs/SUPPORTED_CHARGERS.md#huawei-scharger) | SCharger-7KS-S0, SCharger-22KT-S0 | `huawei-7ks`, `huawei-22kt` | Expected |
| [go-e](https://github.com/mpospisil/gleanvolt/blob/main/docs/SUPPORTED_CHARGERS.md#go-e-charger) | Gemini, Gemini flex, HOMEfix, HOME+ (hardware V3 or later) | `goe` | Expected |
| Others | — | — | Not supported yet |
<!-- end include -->

**Verified** means run on the reference installation (SolaX X3-HYB-G4 PRO, SolaX X1/X3-HAC, VW ID.4);
**Expected** means the same registers per the manufacturer's or the community's documentation, not yet
run. Every model, its caveats and the tiers are in
**[Supported PV systems](docs/SUPPORTED_PV_SYSTEMS.md)**.

- **Home battery** — whatever your inverter manages; Gleanvolt reaches it through the inverter.
- **Car** — any car with a Type 2 AC inlet. See [Supported electric vehicles](docs/SUPPORTED_EVS.md).
- **Host** — any 64-bit Linux (x86-64 or arm64), a Raspberry Pi with Raspberry Pi OS 64-bit, a
  64-bit NAS, or Windows. **Not 32-bit ARM.**

## Get started

### 1. Install

Install Gleanvolt **on the machine that will run it**, then finish in the browser (step 2). The manual's
[Installation](https://mpospisil.github.io/gleanvolt/latest/en/installation/choosing/) chapter has each of these step by step, including how to
[prepare your inverter and charger](https://mpospisil.github.io/gleanvolt/latest/en/installation/preparing-devices/).

**Raspberry Pi, Debian or Ubuntu (64-bit) — recommended.** On the device itself, one command installs
Gleanvolt as a system service, with no Docker and no .NET to install:

```bash
curl -fsSL https://github.com/mpospisil/gleanvolt/releases/latest/download/install.sh | sudo sh
```

Running it again upgrades; settings and data are kept. More in
[Install on Linux](packaging/linux/README.md).

**Docker** on any 64-bit Linux host — one `docker run`; see [Running in Docker](docs/DOCKER.md).

**A NAS** (Synology, QNAP, Unraid, TrueNAS SCALE) — add
[`deploy/docker-compose.nas.yml`](deploy/docker-compose.nas.yml) in the NAS's container UI. See
[Running on a NAS](docs/NAS.md).

**Windows** — every release is published to [winget](https://learn.microsoft.com/windows/package-manager/),
the Windows Package Manager:

```powershell
winget install MartinPospisil.Gleanvolt
gleanvolt
```

Then open `http://localhost:8090`. Gleanvolt runs in that terminal window for as long as it is open; it
is not installed as a Windows service. Its settings, history and logs are kept in `data\` and `logs\`
beside the program, in winget's package folder under `%LOCALAPPDATA%\Microsoft\WinGet\Packages\`.
Upgrade with `winget upgrade MartinPospisil.Gleanvolt`, after copying `data\` somewhere safe. Without
winget, download `gleanvolt-<version>-win-x64.zip` from
[Releases](https://github.com/mpospisil/gleanvolt/releases), unzip it and run `Gleanvolt.Worker.exe`.

**Advanced: Docker on a Pi, managed from another computer.** The scripts in `deploy/` deploy, update
and roll back a Docker container on a Raspberry Pi over ssh, from a checkout of this repository — and
can run Home Assistant and an MQTT broker beside it. See
[Deploying to a Raspberry Pi from your computer](deploy/README.md).

> **Home Assistant on the same device?** Don't run the Home Assistant container on a low-powered Pi
> (less than 2 GB of RAM). See [Gleanvolt with Home Assistant](docs/HOME_ASSISTANT.md).

### 2. Set it up

Open **`http://gleanvolt.local:8090`** from any device on the same network, or
`http://<the machine's address>:8090` where `.local` names don't work. Choose a password, then the
**setup wizard** searches your network for the inverter and the charger and asks for the site and,
optionally, a Solcast solar forecast, the car and the grid tariff. See
[The setup wizard](https://mpospisil.github.io/gleanvolt/latest/en/setting-up/setup-wizard/) in the manual. Prefer a terminal? The `setup` command
does the same ([command line](docs/WEB_UI.md#setting-up-from-the-command-line)).

### 3. Check, then charge

Gleanvolt now reads the devices and shows them on the **Overview**, but writes nothing: charging is
**off** and the battery hold is disabled. Check the figures against your inverter's and charger's own
displays first. Then pick a mode on **Charging plan**: see
[Which mode should I choose?](https://mpospisil.github.io/gleanvolt/latest/en/using/modes/choosing/).

## Use at your own risk

Gleanvolt **writes to your hardware**: it starts, stops and throttles the EV charger, and the battery
hold changes the inverter's battery behaviour. A wrong register address, a firmware that behaves
differently, a bug or a configuration mistake can leave the home battery drained or held at the wrong
charge, exceed a grid-export limit, leave the car uncharged when you need it, or put a device into a
state you have to reset by hand. Changing a device's settings outside the manufacturer's own tools may
also affect its warranty.

You run it at your own risk. The software is provided **as is, without any warranty**, and to the
extent the law allows the author is **not liable for any damage** to inverters, batteries, chargers,
vehicles or other equipment, or for any loss arising from its use — see the *No Liability* section of
the [LICENSE](LICENSE). Start with the write features off or in dry-run, check the readings against
your own devices, and watch the first runs.

## Documentation

**For using Gleanvolt, read [the user manual](https://mpospisil.github.io/gleanvolt/latest/en/).** It is versioned with the releases, and the web
UI's **Manual** link opens the one for the version you run.

The pages below are the **reference**: every setting and configuration key, the reasoning behind the
modes, and the integrations the manual leaves out.

| Page | What it covers |
|---|---|
| [Web UI](docs/WEB_UI.md) | Every page, the password, setup mode and the setup wizard |
| [Configuration](docs/CONFIGURATION.md) | Where settings live, secrets, the PV system, the solar forecast, weather, the grid tariff |
| [Finding your devices](docs/DISCOVERY.md) | The wizard's search and `gleanvolt-discover` |
| [EV charge control](docs/CHARGE_CONTROL.md) | How the charger is driven: surplus, smoothing, the 6 A floor, how modes end |
| [`Forecasted`](docs/MODE_FORECASTED.md), [`Solar + grid`](docs/MODE_SOLAR_GRID.md), [`Fast`](docs/MODE_FAST_NO_BATTERY.md), [`Targeted`](docs/MODE_TARGETED.md) | One page per charge mode |
| [Battery discharge hold](docs/BATTERY_HOLD.md) | Keeping the home battery out of the car's charge |
| [The car and its telemetry](docs/VEHICLE.md) | The car's limits, and the feeds that report its battery |
| [Charging session and energy history](docs/DATA.md) | What is recorded, and how to query it |
| [HTTP API](docs/HTTP_API.md) | The OpenAPI-described API, for programs and MCP servers |
| [Gleanvolt with Home Assistant](docs/HOME_ASSISTANT.md) | Connecting it, running Home Assistant beside it, and every entity |
| [Install on Linux](packaging/linux/README.md) | The one-line install: settings, files, upgrade and uninstall |
| [Running in Docker](docs/DOCKER.md) | One container on any Docker host |
| [Running on a NAS](docs/NAS.md) | Synology, QNAP, Unraid, TrueNAS SCALE and OpenMediaVault |
| [Deploying to a Raspberry Pi from your computer](deploy/README.md) | Advanced: Docker on a Pi, managed over ssh; updates, logs, backup, troubleshooting |
| [Supported PV systems](docs/SUPPORTED_PV_SYSTEMS.md), [electric vehicles](docs/SUPPORTED_EVS.md) | Every model, how well it is supported, and what Gleanvolt needs from it |
| [Troubleshooting](docs/TROUBLESHOOTING.md) | Symptoms, their causes and the fixes |
| [Privacy](docs/PRIVACY.md) | What leaves your network, and when |

**Building from source or contributing?** See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

Licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE). It comes without any warranty
and without liability for damage — see [Use at your own risk](#use-at-your-own-risk).

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
