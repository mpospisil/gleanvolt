# Gleanvolt

[![CI](https://github.com/mpospisil/gleanvolt/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/mpospisil/gleanvolt/actions/workflows/ci.yml?query=branch%3Amain)
[![Publish image](https://github.com/mpospisil/gleanvolt/actions/workflows/publish-image.yml/badge.svg?branch=main)](https://github.com/mpospisil/gleanvolt/actions/workflows/publish-image.yml?query=branch%3Amain)

A standalone, locally hosted background service for managing and monitoring a hybrid inverter (**SolaX** or **Huawei SUN2000**) and an EV charger (**SolaX**, **Huawei SCharger** or **go-e**). See [Hardware targets](#hardware-targets) for the models.

The controller operates entirely within the local LAN via **Modbus TCP**, bypassing cloud dependencies to ensure continuous operation, instantaneous polling, and strict local data ownership. It polls real-time data (PV generation, battery SOC, grid power flow) and applies automated decision-making logic to optimize EV charging and battery utilization based on household energy surpluses.

## Status

Working, and running against live hardware. Polling, Solcast forecasting, live-solar EV charge
control, the battery discharge hold, and the Home Assistant integration are all implemented.

The two features that **write** to hardware — `ChargeControl` (the EV charger) and `BatteryHold` (the
inverter) — ship disabled by default, and `BatteryHold` additionally defaults to dry-run. Register
addresses vary between SolaX generations and firmware, so verify them against your own device before
enabling either.

## Use at your own risk

Gleanvolt **writes to your hardware**: it changes the inverter's battery settings and starts, stops
and throttles the EV charger over Modbus TCP. A wrong register address, a firmware that behaves
differently, a bug, or a configuration mistake can leave the home battery drained or held at the
wrong charge, exceed a grid-export limit, leave the car uncharged when you need it, or put a device
into a state you have to reset by hand. Changing a device's settings outside the manufacturer's own
tools may also affect its warranty.

You run it at your own risk. The software is provided **as is, without any warranty**, and to the
extent the law allows the author is **not liable for any damage** to inverters, batteries, chargers,
vehicles or other equipment, or for any loss arising from its use — see the *No Liability* section of
the [LICENSE](LICENSE). Start with the write features off or in dry-run, check the register addresses
against your own device, and watch the first runs.

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
- **Solar with grid help** — "take the sun, let the grid make a weak sun usable, and stop when the day's sun is over": the car follows the surplus while it clears a minimum you set, a surplus too small for the charger's 6 A floor is topped up to it from the grid with the home battery held out of it, and once the forecast has no sun left today that clears your minimum the mode switches itself off.
- **Grid tariff** — tell it when your grid is cheap (Czech *HDO*: the low rate, NT, and the high rate,
  VT) by typing your low-tariff periods for weekdays and the weekend on Settings, with a price for each
  rate. It shows on the Overview, in Home Assistant and on the API; a save applies at once, and `Fast`
  and `SolarGrid` can be set to wait for low tariff before buying from the grid. See
  [Grid tariff](docs/CONFIGURATION.md#grid-tariff-the-tariff-section).
- **Solar forecasting** — a cached [Solcast](https://solcast.com/) forecast for the site, logged against actual generation.
- **Home Assistant integration** (optional) over MQTT discovery, with runtime control and telemetry — see
  [Gleanvolt with Home Assistant](docs/HOME_ASSISTANT.md).
- **Self-hosted web UI** (on by default, no configuration — see [Self-hosted web UI](docs/WEB_UI.md) below) — a Blazor dashboard served by the controller itself at `http://<host>:8090`: live telemetry, every control Home Assistant has, charging-session history, the energy history, the solar forecast for today and tomorrow, and the forecast plan, all with no Home Assistant or MQTT broker required. It needs no Home Assistant and no MQTT broker.
- **HTTP API, described by OpenAPI** (off by default) — the same telemetry, history, forecast and
  actions the other two surfaces have, for programs rather than people: read the energies and the
  car, ask what a targeted charge *would* do without starting one, and start it when the answer is
  right. Built so that an **MCP server** can hand the lot to an LLM as generated tools — see
  [HTTP API](docs/HTTP_API.md).
- **Vehicle telemetry** — optionally reads the **car's own** battery SOC and range, either from MQTT
  (normalised, so any vehicle Home Assistant can see becomes a source without new code) or, for a VW
  Group car, from the manufacturer's **EU Data Act portal** on the controller's own schedule. It shapes
  what you *ask* for — a targeted charge can be stated as a battery percentage — and never how it is
  delivered: no control decision depends on it, and a feed that dies changes nothing about how the
  charger is driven.
- **Charging session history** — every controlled session recorded to a local SQLite file: when it ran, which strategy drove it, and how much of the energy came from solar, the grid and the home battery.
- **Energy history at 15-minute resolution** — a monitoring service that does nothing but record, to its own database: for every quarter hour of every day, how much the roof made, how much the forecast said it would, how much crossed the meter each way, how much the car took, and where the home battery sat. Charging or not, plugged in or not — the series analytics is built on, with a
  day-at-a-time viewer in the web UI: the day drawn as a chart, and the same day printed as the rows it is stored as.
- **Background service** — runs unattended as a long-lived process (e.g. systemd service / Windows Service), and can be **stopped gracefully from the web UI or Home Assistant** — the charger released, the open session closed and written — instead of being killed. It stays stopped until you start it again, while a reboot or a power cut still brings it straight back; see [Stopping and starting the controller](deploy/README.md#stopping-and-starting-the-controller).
- **Local data ownership** — no cloud dependency for core operation.

## Hardware targets

| Device | Reference installation | Supported |
|---|---|---|
| Hybrid inverter | SolaX X3-HYB-G4 PRO | [Inverters](#inverters) below |
| EV charger | SolaX X1/X3-HAC | [EV chargers](#ev-chargers) below |
| Home battery | SolaX T-BAT H 2.5 modules + BMS (**10 kWh** nominal) | Whatever the inverter manages — no connection of its own |
| Electric vehicle | VW ID.4 Pro | Any Type 2 AC car — see [Supported electric vehicles](docs/SUPPORTED_EVS.md) |
| Host | Raspberry Pi, 4 GB, Raspberry Pi OS Lite 64-bit | Any 64-bit (x86-64 or arm64) Linux machine running Docker, a [home NAS](deploy/README.md#running-on-a-nas) included; Debian and Ubuntu natively ([.deb](#install-on-linux)); Windows. **Not 32-bit ARM (armv7).** |

### Inverters

<!-- include: docs/SUPPORTED_INVERTERS.md -->
Hybrid inverters Gleanvolt can drive, by manufacturer. Each model links to its manufacturer's page;
discontinued models go to the manufacturer's home page. The tiers are explained in
[Supported PV systems](https://github.com/mpospisil/gleanvolt/blob/main/docs/SUPPORTED_PV_SYSTEMS.md#at-a-glance),
and [`gleanvolt-discover`](https://github.com/mpospisil/gleanvolt/blob/main/docs/DISCOVERY.md)
names a device by the serial prefix or model name given here.

### SolaX inverters

Driver `solax`, over Modbus TCP.

| Model | Family | Serial prefix | Tier | Battery hold | Caveats |
|---|---|---|---|---|---|
| [**X3-HYB-G4 PRO**](https://www.solaxpower.com/our-products/x3-hyb-g4-pro.html) | GEN6 hybrid | `10K` | **Verified** | **Verified** 2026-07-27 — see [DECISIONS.md](https://github.com/mpospisil/gleanvolt/blob/main/docs/DECISIONS.md) | — |
| [**X3-Hybrid G4**](https://www.solaxpower.com/our-products/x3-hybrid-g4.html) | GEN4 hybrid | `H34` | Expected | Supported upstream | — |
| [**X1-Hybrid G4**](https://www.solaxpower.com/our-products/x1-hybrid-g4.html) | GEN4 hybrid | `H43`, `H44`, `H450`, `H460`, `H475` | Expected | Supported upstream | [3](#3-x1-models-and-the-block-read) |
| [**X1-IES**](https://www.solaxpower.com/our-products/x1-ies.html) | GEN5 hybrid | `H53`, `H55`, `H56`, `H58` | Expected | Supported upstream | [1](#1-only-pv-strings-1-and-2-are-read) on 5–8 kW |
| [**X3-IES**](https://www.solaxpower.com/our-products/x3-ies.html) | GEN5 hybrid | `H35…`, `P35…` | Expected | Supported upstream | — |
| [**X3-Ultra**](https://www.solaxpower.com/our-products/x3-ultra.html) | GEN5 hybrid | `H3B…` | Expected | Supported upstream | [1](#1-only-pv-strings-1-and-2-are-read) on some sizes, [2](#2-only-battery-1-is-read) |
| [**X3-Aelio**](https://www.solaxpower.com/our-products/x3-aelio-hybrid-inverter.html) | GEN5 hybrid (commercial) | `8021` | Expected | Supported upstream | [1](#1-only-pv-strings-1-and-2-are-read) |
| [**X1-VAST**](https://www.solaxpower.com/our-products/x1-vast.html) | GEN6 hybrid | `10M` | Expected | **Untested anywhere** | [1](#1-only-pv-strings-1-and-2-are-read), [3](#3-x1-models-and-the-block-read) |
| [**X1-Hybrid G2**](https://www.solaxpower.com/) (SK-TL, SK-SU) | GEN2 hybrid | `L30`, `U30`, `L37`, `U37`, `L50`, `U50` | Readings and charging only | None | — |
| [**X1-Hybrid G3**](https://www.solaxpower.com/) | GEN3 hybrid | `H1E`, `H1I`, `HCC`, `HUE`, `XRE` | Readings and charging only | None | — |
| [**X3-Hybrid G3**](https://www.solaxpower.com/) | GEN3 hybrid | `H3DE`, `H3E`, `H3LE`, `H3PE`, `H3UE` | Readings and charging only | None | — |

The battery hold on the X3-HYB-G4 PRO is Gleanvolt's own evidence, not upstream's. The upstream
integration classes the G4 PRO as GEN6 and does not enable `0x7C` for it, citing incomplete documentation
for that generation; its newer "mode 8" control was reported to have no effect on a G4 PRO
([discussion #1776](https://github.com/wills106/homeassistant-solax-modbus/discussions/1776)). A
different firmware revision may behave differently.

The Modbus Power Control block at `0x7C` exists from GEN4 on. Leave `BatteryHold:Enabled` off on the G2
and G3 hybrids: there is nothing at that address for the hold to write to.

Not supported:

- **AC-coupled, RetroFit and FIT inverters** — X1-AC G3 (`XAC`), X3-RetroFit G3 (`F3D`, `F3E`),
  X1-RetroFit G4 (`F43`, `F450`, `F460`, `F475`, `PRE`), X3-Fit G4 (`F34`), X1-FIT G3 (`PRI`). They have
  no DC PV input, so `0x0A`/`0x0B` read nothing: solar shows 0 and every house-load and surplus figure
  derived from it is wrong.
- **PV-only inverters** — X1-Boost, X1-Mini, X1-Air, X1-SMART-G2, X3-MIC / MIC PRO. A different map, and
  no battery.
- **Other SolaX lines with their own register maps** — X3-MEGA / FORTH G2, A1-Hybrid, J1-Hybrid,
  X1-Hybrid-LV, X1-Lite-LV.

Caveats:

#### 1. Only PV strings 1 and 2 are read

Gleanvolt reads PV power from `0x0A` and `0x0B`. On a model with three or more MPPT inputs, power on
input 3 and above is **missing from solar and from house load**. The larger X1-IES, some X3-Ultra sizes,
the X1-VAST and the X3-Aelio (5–6 inputs) all have them. Strings wired only to inputs 1 and 2 are
unaffected.

#### 2. Only battery 1 is read

On GEN5 and GEN6, `0x16` and `0x1C` belong to the first battery. A second battery — possible on the
X3-Ultra — is not counted.

#### 3. X1 models and the block read

The inverter is read in one request from `0x00` to `0x74`, because SolaX asks for at least a second
between Modbus instructions. The per-phase registers at `0x6C`–`0x74` exist only on three-phase (X3)
models, so a single-phase inverter might refuse the whole block. Untested. A single-phase site does not
need those registers — they serve only
[`UnmeteredGridPhase`](https://github.com/mpospisil/gleanvolt/blob/main/docs/CONFIGURATION.md#the-pv-system-the-pv-section),
which is a three-phase correction.

### Huawei SUN2000

Driver `huawei` ([#316](https://github.com/mpospisil/gleanvolt/issues/316)), over Modbus TCP. How to
connect it, enable Modbus TCP and set up an EMMA:
**[Setting up a Huawei SUN2000](https://github.com/mpospisil/gleanvolt/blob/main/docs/HUAWEI_SETUP.md)**.
Huawei's product pages move by region, so the models link to Huawei's solar home page. There is no
serial prefix: discovery names these by the model name at register `30000`, which starts with `SUN2000`.

| Model | Family | Meter | Tier | Battery hold |
|---|---|---|---|---|
| [**SUN2000-…KTL-M1**](https://solar.huawei.com/) (3–10 kW) | Three-phase hybrid | DTSU666-H | Expected | **Untested** |
| [**SUN2000-…K-MAP0**](https://solar.huawei.com/) | Three-phase hybrid | DTSU666-H | Expected | **Untested** |
| [**SUN2000-…KTL-L1**](https://solar.huawei.com/) | Single-phase hybrid | DDSU666-H | Expected | **Untested** |
| [**SUN2000-…K-LC0**](https://solar.huawei.com/) | Single-phase hybrid | DDSU666-H | Expected | **Untested** |

The same models **behind an EMMA** energy manager are in the same tiers: set *Connected through* to EMMA
([#322](https://github.com/mpospisil/gleanvolt/issues/322)). Not supported: the larger SUN2000 models (M2
and up), the SmartLogger, the FusionSolar cloud API and direct RS485.

- **Nothing has run on a real device yet.** The readings come from Huawei's *Solar Inverter Modbus
  Interface Definitions* and evcc's `huawei-sun2000-hybrid.yaml` and `huawei-emma.yaml` (MIT). Which way
  the meter and the battery count is the documented sign, not one observed. The EMMA's grid sign is
  assumed to be the meter's. A tester's register snapshot settles all three
  ([#325](https://github.com/mpospisil/gleanvolt/issues/325)).
- **No output per phase**, so
  [`UnmeteredGridPhase`](https://github.com/mpospisil/gleanvolt/blob/main/docs/CONFIGURATION.md#the-pv-system-the-pv-section)
  is not available.
- **One Modbus client.** The SDongle serves very few clients, and the EMMA exactly one. Home Assistant's
  `huawei_solar` integration competes for it. See
  [the setup page](https://github.com/mpospisil/gleanvolt/blob/main/docs/HUAWEI_SETUP.md#2-only-one-client-at-a-time).
- **The battery hold is a lasting setting.** It writes the maximum discharging power (`47077`) as 0, and
  the original back to release ([#323](https://github.com/mpospisil/gleanvolt/issues/323)). Unlike
  SolaX's command it does not lapse, so:
  - the original is stored in `data/inverter-state.json` before 0 is written, and written back on
    release and when Gleanvolt stops normally;
  - after a crash or a power cut, the first poll finds the register at 0 with an original stored, and
    puts it back (or keeps the hold, if one is due);
  - a register that already reads 0 with nothing stored was set by someone else, and is left alone;
  - arming is spaced at least a minute after the last write, until it is known whether the setting wears
    out flash memory.

  If Gleanvolt is gone for good while a hold is on, set the maximum discharging power back in the
  FusionSolar app. Behind an EMMA, the EMMA's own AI energy optimisation must be off, with the EMMA in
  maximum self-consumption mode.

### Tigo

A SolaX inverter under Tigo's name: driver `solax`, over Modbus TCP.

| Model | Family | Serial prefix | Tier | Battery hold |
|---|---|---|---|---|
| [**TSI X3**](https://www.tigoenergy.com/ei-residential-solution-eu) | GEN4 hybrid | `H31` | Expected | Supported upstream |

### Qcells

A SolaX inverter under Qcells' name: driver `solax`, over Modbus TCP.

| Model | Family | Serial prefix | Tier | Battery hold |
|---|---|---|---|---|
| [**Q.VOLT HYB-G3-3P**](https://www.qcells.com/) | GEN4 hybrid (listed by upstream) | — | Expected | Supported upstream |

### Dražice (DZ Dražice)

Reviewed 2026-11-07 from [dzd.cz](https://www.dzd.cz/fotovoltaika-drazice/stridace). These are sold as
Dražice's own, but match SolaX models by their datasheets. Their tier is the SolaX model's **if** the unit
is the SolaX hardware with SolaX firmware; no statement from Dražice confirms it, and no unit has been run.
Before relying on a row, check that
[`gleanvolt-discover`](https://github.com/mpospisil/gleanvolt/blob/main/docs/DISCOVERY.md)
names the device by a SolaX serial prefix. A unit it cannot name runs firmware this page knows nothing
about.

| Dražice model | Matches | Tier | Why we think so |
|---|---|---|---|
| [**IN.HYBRID COMPACT PRO**](https://www.dzd.cz/fotovoltaika-drazice/stridace/fotovoltaicky-stridac-in-hybrid-compact-pro) 5–15 kW | SolaX **X3-HYB-G4 PRO** | Verified on the SolaX unit, not on this one | Identical datasheet: 2 or 3 MPPT at 20 A, 110–950 V, 200 % PV oversizing, 110 % AC, 2× EPS for 10 s, 560×503×210 mm, 38 kg, two battery ports with a 2-in-1 cable, the same VPP list (OpenADR, IEEE 2030.5, FCAS, API) |
| [**IN.HYBRID COMPACT S**](https://www.dzd.cz/fotovoltaika-drazice/stridace/fotovoltaicky-stridac-in-hybrid-compact-s) 15–30 kW | SolaX **X3-Ultra** | Expected — caveats [1](#1-only-pv-strings-1-and-2-are-read), [2](#2-only-battery-1-is-read) | 15–30 kW including a 19.9K size, 36 A per MPPT, 696×526×240 mm, 47 kg, two battery ports |
| [**IN.HYBRID COMPACT M**](https://www.dzd.cz/fotovoltaika-drazice/stridace/fotovoltaicky-stridac-in-hybrid-compact-m) 50/60 kW | SolaX **X3-Aelio** | Expected — caveat [1](#1-only-pv-strings-1-and-2-are-read) | 50 and 60 kW, 6 MPPT at 40 A, two battery ports |
| **IN.HYBRID COMPACT** (earlier, `-D`/`-M` sizes) | SolaX **X3-Hybrid G4** | Expected | 5–15 kW three-phase hybrid with the same `-D`/`-M` variants as the G4 |
| **IN.ENTITY S/M/L, IN.COMPACT, IN.One** | — | Not supported | Grid-tied, no battery |

Dražice's datasheets list RS485 and a "Datapoint" interface but say nothing about Modbus TCP on the LAN
port. The 10-year warranty requires the inverter to be connected to the DZD cloud, which suggests
Dražice's own firmware or dongle. Whether Modbus TCP is open on these units is the open question.

### Other inverters

Not supported yet. GoodWe is planned.
<!-- end include -->

### EV chargers

<!-- include: docs/SUPPORTED_CHARGERS.md -->
EV chargers Gleanvolt can drive, by manufacturer. Each model links to its manufacturer's page. The tiers
are explained in
[Supported PV systems](https://github.com/mpospisil/gleanvolt/blob/main/docs/SUPPORTED_PV_SYSTEMS.md#at-a-glance).
One charger is supported per installation. A tester's register snapshots
([issue #354](https://github.com/mpospisil/gleanvolt/issues/354)) move a row up a tier.

### SolaX chargers

Driver `solax`, over Modbus TCP. The HAC models link to SolaX's Smart EV Charger G2 page, the EVC to its
Smart EV Charger page.

| Model | Family | Serial prefix | Tier | Notes |
|---|---|---|---|---|
| [**X1/X3-HAC**](https://www.solaxpower.com/our-products/smart-ev-charger-g2.html) | GEN2 (HAC) | `5` + model code + power code | **Verified** | The reference installation |
| [**X1-HAC, X3-HAC, X1-HAC-S, X3-HAC-S, A1-HAC, J1-HAC, C1-HAC, C3-HAC**](https://www.solaxpower.com/our-products/smart-ev-charger-g2.html) — 4.6, 7.2, 11 and 22 kW | GEN2 (HAC) | `5` + model code + power code | Expected | Same four registers as the reference |
| [**X1-EVC 7 kW, X3-EVC 11 kW, X3-EVC 22 kW**](https://www.solaxpower.com/our-products/smart-ev-charger.html) | GEN1 (EVC) | `C107`, `C311`, `C322` | Expected | Upstream enables all four registers. With firmware 7.0 or later upstream treats the unit as a HAC. Early units were reported with Modbus TCP port 502 closed and reachable only over RS485 at address 70 ([discussion #349](https://github.com/wills106/homeassistant-solax-modbus/discussions/349), 2023) |

Not supported:

- **A charger controlled through a SolaX Datahub.** Charge current is then `0x624`, which Gleanvolt does
  not write.
- **Several chargers in parallel mode.** The mode register is then `0x669`.
- **A charger linked to the inverter for SolaX's own Green/ECO modes** was reported to stop answering
  Modbus directly (same discussion). Gleanvolt needs the charger on its own Modbus TCP connection.

Caveats:

- **Gleanvolt writes two charger registers**: `0x628` every time the setpoint changes, and `0x60D` when a
  mode starts or stops. Run `ChargeControl:DryRun` first and compare the logged values with the charger.
- **Not yet confirmed: EEPROM.** SolaX warns that some holding-register writes are stored in EEPROM, which
  has a limited number of write cycles. The inverter's `0x7C` command is documented as not stored; for the
  charger's `0x628` and `0x60D` this project has no answer from SolaX's protocol document yet.
- **6–32 A, no phase switching.** On three phases the charger's 6 A floor is about 4.2 kW.
- **One Modbus client.** The charger drops a second Modbus TCP connection on its first request. A charger
  that evcc or Home Assistant already controls has to be **handed over** to Gleanvolt, not shared. Take
  snapshots with the button on Settings: it uses Gleanvolt's own connection.

### Huawei SCharger

Over Modbus TCP, port 502. How to switch its Modbus server on, and what to do behind an EMMA:
**[Setting up a Huawei SCharger](https://github.com/mpospisil/gleanvolt/blob/main/docs/CHARGER_SETUP.md#huawei-scharger)**.
Huawei's product pages move by region, so the models link to Huawei's solar home page.

| Model | Driver | Tier | Notes |
|---|---|---|---|
| [**SCharger-7KS-S0**](https://solar.huawei.com/) (7.4 kW, one phase) | `huawei-7ks` | Expected | Behind an EMMA, keep it in dry-run |
| [**SCharger-22KT-S0**](https://solar.huawei.com/) (22 kW, three phases) | `huawei-22kt` | Expected | As the 7KS |

- **No status.** The protocol has none, so no car and a waiting car both read *unknown*, not *available*.
- **The setpoint is power**, written in 100 W steps and converted from amps.
- **Port 502 refused** on 2023/2024 firmware is reported by owners in evcc's tracker.
- **One Modbus client.** The SCharger may drop a second connection, as the SolaX does. A charger an EMMA
  already manages has to be handed over, not shared.
- Start, stop and the setpoint are implemented as documented, and every test recording is synthetic.

### go-e Charger

Driver `goe`, over the go-e's local HTTP API v2 on port 80. How to switch the API on:
**[Setting up a go-e](https://github.com/mpospisil/gleanvolt/blob/main/docs/CHARGER_SETUP.md#go-e-charger)**.

| Model | Driver | Tier | Notes |
|---|---|---|---|
| [**Gemini, Gemini flex, HOMEfix and HOME+**](https://go-e.com/) (hardware V3 or later), 11 and 22 kW | `goe` | Expected | The API is off by default. Phase switching is the go-e's own; Gleanvolt does not command it |

- **A pause is the force state *off***, because the go-e refuses a current below 6 A.
- **The current and the force state are settings the go-e keeps**; Gleanvolt writes them only when they
  change.
- Start, stop and the setpoint are implemented as documented, and every test recording is synthetic.

### Other chargers

Not supported yet.
<!-- end include -->

The battery has no interface of its own: everything about it reaches us through the inverter's
registers (SOC from `BatteryCapacity 0x1C`, power from `BatteryPowerCharge1 0x16`) and every command
that affects it goes through the inverter's power-control block. Its **usable** capacity is the one
site-specific number the forecast-driven mode cannot work without — see
[`BatteryCapacityKWh`](docs/MODE_FORECASTED.md).

## Getting started

Once Gleanvolt is installed — [on Linux](#install-on-linux) or [in Docker](#deployment) — open
`http://<host>:8090`. A fresh install starts in [setup mode](docs/WEB_UI.md#setup-mode), which asks for
a password and runs the [setup wizard](docs/WEB_UI.md#the-setup-wizard) for the device addresses and the site (or use
[the command line](docs/WEB_UI.md#setting-up-from-the-command-line)). Nothing is polled until that is done. After
that, nothing is written to either device either: the service always boots with the charge mode **Off** and the battery hold
**off**, and `BatteryHold:Enabled` is `false` as well, so it only polls and logs. That is the
recommended way to confirm the telemetry looks right before enabling anything that writes.

To build and run it from source instead, see
[Building and running from source](CONTRIBUTING.md#building-and-running-from-source).

Don't know the inverter's or the charger's address? The setup wizard's **Search** finds them;
see [Finding your devices](docs/DISCOVERY.md).

## Install on Linux

On **Debian, Ubuntu or Raspberry Pi OS (64-bit)**, amd64 or arm64, one command installs the controller
and its web UI as a systemd service, with no Docker, no .NET installation and no Home Assistant or
MQTT broker:

```bash
curl -fsSL https://github.com/mpospisil/gleanvolt/releases/latest/download/install.sh | sudo sh
```

It starts straight away in setup mode: open `http://<device>:8090`, choose a password and finish the
setup wizard, which finds the inverter and the charger on your network. Charge mode starts `Off` and the
battery hold disabled. Running the command again upgrades; settings and data are kept.

Settings, where the data and logs live, pinning a version and uninstalling are in
**[packaging/linux/README.md](packaging/linux/README.md)**. For Home Assistant alongside it, use the
Docker deployment below.

## Deployment

For unattended operation, Gleanvolt runs on a Raspberry Pi (Raspberry Pi OS Lite, 64-bit) as one
Docker container: the controller, with its web UI on `:8090`. From a developer machine:

```bash
./deploy/deploy-controller-only.sh                         # newest build of main
IMAGE_TAG=1.0.0 ./deploy/deploy-controller-only.sh         # a released version -- note: no "v"
IMAGE_TAG=sha-abc1234 ./deploy/deploy-controller-only.sh   # one specific build; also how you roll back
```

**Updating is the same command.** It copies the compose files, pulls, and recreates the container if
the image changed. All state — `.env`, the databases under `data/`, the logs — lives on bind mounts
under `/opt/gleanvolt`, so nothing is lost. The controller restarts into charge mode `Off`, so
charging has to be started again afterwards.

**Don't run the Home Assistant container on a low-powered Pi** (less than 2 GB of RAM). To connect
Gleanvolt to Home Assistant — one you already run, or Home Assistant and an MQTT broker beside it on a
bigger Pi — see [Gleanvolt with Home Assistant](docs/HOME_ASSISTANT.md).

**On a home NAS** (Synology, QNAP, Unraid, TrueNAS SCALE, OpenMediaVault), use
[`deploy/docker-compose.nas.yml`](deploy/docker-compose.nas.yml) in the NAS's own container UI instead
of the scripts; [Running on a NAS](deploy/README.md#running-on-a-nas) covers each of them. The NAS must
be 64-bit: there is no 32-bit ARM (armv7) image.

The image, `ghcr.io/mpospisil/gleanvolt`, covers **`linux/arm64`** (the Pi), **`linux/amd64`** (an x64
Linux host) and **Windows Nano Server ltsc2022**, so the same tag pulls the right image everywhere;
Windows needs `Controller:TimeZone` set, for the reason given under [Configuration](docs/CONFIGURATION.md).

Deploying writes **nothing** to your hardware: charge control boots in mode `Off` and takes control
only when a charging button is pressed, and the battery hold stays disabled and dry-run until you
turn it on deliberately.

Preparing the Pi, storage, backup and restore, and troubleshooting are in
**[deploy/README.md](deploy/README.md)**.

## Documentation

| Page | What it covers |
|---|---|
| [Configuration](docs/CONFIGURATION.md) | Where settings live, secrets, the PV system, the solar forecast, weather, the grid tariff |
| [Finding your devices](docs/DISCOVERY.md) | The setup wizard's search and `gleanvolt-discover` |
| [EV charge control](docs/CHARGE_CONTROL.md) | How the charger is driven: surplus, smoothing, the 6 A floor, how modes end |
| [Battery discharge hold](docs/BATTERY_HOLD.md) | Keeping the home battery out of the car's charge |
| [`Forecasted` mode](docs/MODE_FORECASTED.md) | Charging on the day's solar forecast |
| [`FastNoBattery` mode](docs/MODE_FAST_NO_BATTERY.md) | Fast charge from PV and grid, with the battery held |
| [`Targeted` mode](docs/MODE_TARGETED.md) | An amount of energy by a departure time |
| [`SolarGrid` mode](docs/MODE_SOLAR_GRID.md) | The sun, with the grid making a weak sun usable |
| [The car and its telemetry](docs/VEHICLE.md) | The `Ev` and `Vehicle` sections: the car's own state of charge |
| [Web UI](docs/WEB_UI.md) | Every page of the web UI, the password, setup mode and the setup wizard |
| [HTTP API](docs/HTTP_API.md) | The OpenAPI-described API, for programs and MCP servers |
| [Charging session and energy history](docs/DATA.md) | What is recorded, and how to get it out |
| [Gleanvolt with Home Assistant](docs/HOME_ASSISTANT.md) | MQTT discovery, the entities, and running Home Assistant beside it |
| [Deploying to a Raspberry Pi](deploy/README.md) | Preparing the Pi, updating, NAS, logs, backup, troubleshooting |
| [Supported hardware](docs/SUPPORTED_PV_SYSTEMS.md) | Inverters, chargers and cars, and how well each is supported |

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
