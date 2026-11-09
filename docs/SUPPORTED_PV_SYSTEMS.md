# Supported PV systems

Which inverters and EV chargers Gleanvolt can drive. It talks to two devices on your LAN:

- a hybrid inverter, **SolaX** or **Huawei SUN2000**, over Modbus TCP;
- an EV charger: a **SolaX** or a **Huawei SCharger** over Modbus TCP, or a **go-e** over its local HTTP API.

The SolaX register maps were written for the reference installation's **X3-HYB-G4 PRO** and
**X1/X3-HAC**. The Huawei and go-e drivers come from the manufacturers' documentation.

Each device's driver is chosen in configuration:

- the inverter's by `Pv:Inverter:Driver`: `solax` (the default, when it is empty) or `huawei`;
- the charger's by `Pv:Chargers:0:Driver`: `solax` (the default), `huawei-7ks`, `huawei-22kt` or `goe`.

A driver is never chosen by model: `Pv:Inverter:Model` and `Pv:Chargers:0:Model` are documentation. So
*supported* here means one thing: **the device answers what its driver reads, with the meaning the driver
expects.**

> **Last reviewed 2026-09-13.** Which SolaX families share those registers comes from the
> [wills106/homeassistant-solax-modbus](https://github.com/wills106/homeassistant-solax-modbus)
> integration, which this project's register maps already cite: it detects each model by serial-number
> prefix and enables every register only for the families that have it. Only the reference hardware
> has been run. Register addresses can differ between firmware revisions, so **every write stays off
> or in dry-run until you have checked the readings against the device's own display.**

The serial-number prefixes in the tables below are also what
[`gleanvolt-discover`](../README.md#finding-your-devices) names a device by when it scans the LAN, and
the tier it prints is the tier here. The tool keeps them in one table in code
(`src/Gleanvolt.Discovery/SolaxModels.cs`), and **the two must agree**: a prefix added to one is added to
the other, and a test fails when the code names a device by a prefix this page does not list.

The device lists below are copied in from [Supported inverters](SUPPORTED_INVERTERS.md) and
[Supported EV chargers](SUPPORTED_CHARGERS.md), which the README and the wiki show too. Edit those two
files, never the copies, and run `scripts/sync-doc-includes.sh`.

## At a glance

| Tier | What it means |
|---|---|
| **Verified** | Run against the reference installation |
| **Expected** | The same registers, per the upstream map. Not tested — read the caveats on its row |
| **Readings and charging only** | Telemetry and charge control work; the battery discharge hold does not exist on this family |
| **Not supported** | A different register map, or registers that mean something else |

## What Gleanvolt needs from the hardware

### SolaX inverter (`solax`)

| Access | Registers | Used for |
|---|---|---|
| Read, one block `0x00`–`0x74` (input registers) | `0x0A`, `0x0B` — PV power, strings 1 and 2 | Solar, house load |
| | `0x16` battery power, `0x1C` battery SOC | The home battery |
| | `0x46`/`0x47` grid meter | Surplus, grid import/export |
| | `0x6C`, `0x70`, `0x74` — inverter output per phase | Only [`UnmeteredGridPhase`](../README.md#the-pv-system-the-pv-section) |
| Write (holding) | `0x7C` — the 13-register Modbus Power Control command | Only the battery discharge hold |

### Huawei SUN2000 (`huawei`)

All holding registers, 32-bit values high word first. Three reads per poll.

| Access | Registers | Used for |
|---|---|---|
| Read | `32064` input power, all MPPTs | Solar, house load |
| | `37760` battery SOC, `37765` battery power | The home battery |
| | `37113` smart meter active power, or the EMMA's `31657` on unit 0 behind an EMMA | Surplus, grid import/export |
| | `30000` model name | "Test connection" and discovery |
| Read and write | `47077` maximum discharging power | Only the battery discharge hold |

### SolaX EV charger

| Access | Registers | Used for |
|---|---|---|
| Read (input) | `0x0B` charge power, `0x1D` run state | Every charging mode |
| Read and write (holding) | `0x60D` use mode, `0x628` charge current | Every charging mode |

### Huawei SCharger (`huawei-7ks`, `huawei-22kt`)

From Huawei's *Northbound Modbus-TCP Interconnection Protocol for AC Chargers* V1.0. All holding registers.

| Access | Registers | Used for |
|---|---|---|
| Read | `0x1000`–`0x100B` phase voltages and currents, `0x100C` total power (kW × 10) | Every charging mode; L1's voltage is "Test connection" and discovery |
| Read and write | `0x2000` maximum charge power (kW × 10) | The setpoint, converted from amps |
| Read and write | `0x2006` charging control: 1 pause, 2 resume | Start and stop |

### go-e Charger (`goe`)

From go-e's [API v2 documentation](https://github.com/goecharger/go-eCharger-API-v2). Port 80.

| Access | Keys | Used for |
|---|---|---|
| Read, `GET /api/status?filter=…` | `car`, `alw`, `nrg` (power at index 11, W), `amp`, `frc`, `ama`, `var` | Every charging mode, "Test connection" and discovery |
| Write, `GET /api/set?…` | `amp` current, `frc` force state (1 off, 2 on) | The setpoint, start, stop and pause |

---

## Inverters

<!-- include: docs/SUPPORTED_INVERTERS.md -->
Hybrid inverters Gleanvolt can drive, by manufacturer. Each model links to its manufacturer's page;
discontinued models go to the manufacturer's home page. The tiers are explained in
[Supported PV systems](https://github.com/mpospisil/gleanvolt/blob/main/docs/SUPPORTED_PV_SYSTEMS.md#at-a-glance),
and [`gleanvolt-discover`](https://github.com/mpospisil/gleanvolt/blob/main/README.md#finding-your-devices)
names a device by the serial prefix or model name given here.

### SolaX

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
[`UnmeteredGridPhase`](https://github.com/mpospisil/gleanvolt/blob/main/README.md#the-pv-system-the-pv-section),
which is a three-phase correction.

### Huawei

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
  [`UnmeteredGridPhase`](https://github.com/mpospisil/gleanvolt/blob/main/README.md#the-pv-system-the-pv-section)
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
[`gleanvolt-discover`](https://github.com/mpospisil/gleanvolt/blob/main/README.md#finding-your-devices)
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

### Other manufacturers

Not supported yet. GoodWe is planned.
<!-- end include -->

## EV chargers

<!-- include: docs/SUPPORTED_CHARGERS.md -->
EV chargers Gleanvolt can drive, by manufacturer. Each model links to its manufacturer's page. The tiers
are explained in
[Supported PV systems](https://github.com/mpospisil/gleanvolt/blob/main/docs/SUPPORTED_PV_SYSTEMS.md#at-a-glance).
One charger is supported per installation. A tester's register snapshots
([issue #354](https://github.com/mpospisil/gleanvolt/issues/354)) move a row up a tier.

### SolaX

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

### Huawei

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

### go-e

Driver `goe`, over the go-e's local HTTP API v2 on port 80. How to switch the API on:
**[Setting up a go-e](https://github.com/mpospisil/gleanvolt/blob/main/docs/CHARGER_SETUP.md#go-e-charger)**.

| Model | Driver | Tier | Notes |
|---|---|---|---|
| [**Gemini, Gemini flex, HOMEfix and HOME+**](https://go-e.com/) (hardware V3 or later), 11 and 22 kW | `goe` | Expected | The API is off by default. Phase switching is the go-e's own; Gleanvolt does not command it |

- **A pause is the force state *off***, because the go-e refuses a current below 6 A.
- **The current and the force state are settings the go-e keeps**; Gleanvolt writes them only when they
  change.
- Start, stop and the setpoint are implemented as documented, and every test recording is synthetic.

### Other manufacturers

Not supported yet.
<!-- end include -->

---

## Connecting

- **Not sure of the address?** [`gleanvolt-discover`](../README.md#finding-your-devices) scans the LAN
  and reports host, port and unit id for each device: from its serial number (SolaX), its model name
  (Huawei SUN2000), its phase voltages (Huawei SCharger) or its API (go-e, on port 80).
- **Huawei:** Modbus TCP is off by default on the SDongle and the EMMA. See
  [Setting up a Huawei SUN2000](HUAWEI_SETUP.md).
- **Huawei SCharger and go-e:** see [Setting up a Huawei SCharger or a go-e](CHARGER_SETUP.md).
- **Modbus TCP on port 502, unit id 1** for both devices, except a go-e: HTTP on port 80, no unit id. All
  are configurable (`Pv:Inverter:Port`, `Pv:Inverter:UnitId`, and the same under `Pv:Chargers:0`).
- **Inverter access.** G2 and G3 hybrids have Ethernet built in. G4 and later need the LAN port or a
  **Pocket WiFi 3.0 or newer** dongle — **PocketLAN does not provide Modbus**. Modbus is off by default on
  some firmware and is enabled in the inverter's own menu.
- **RS485-only devices** work through an RS485-to-Ethernet gateway set up as a Modbus TCP server on port
  502 — the upstream project recommends the Waveshare RS485 to Ethernet (B).
- **Both devices answer on port 502**, so a reachability check passes even with the two addresses swapped.
  The charger serves a web page mentioning `chargeweb`; the inverter reports a battery SOC at `0x1C` that
  matches its display.

## Help the list grow

If you run Gleanvolt on anything other than the reference hardware, an issue with the model, the **first
few characters** of its serial number (never the whole serial), the DSP and ARM firmware versions, which
readings match the device's own display, and whether the battery hold did what its dry-run log said, is
what turns an *Expected* row into a *Verified* one.

**Attach a register snapshot.** On **Settings**, under the inverter, type what is happening at the moment
("night, battery idle, importing") and press **Save register snapshot**. Gleanvolt reads every register its
driver uses, once, and your browser saves a JSON file. The serial number in it is already cut to its model
prefix. Two snapshots are the most useful: one at night with the battery idle and the house importing, and
one with the battery charging. They become the test fixtures every driver is checked against, so the
signs and ranges are proven against your device rather than assumed.

For a charger, the same button is under the charger. Three snapshots are the most useful: no car, a car
plugged in and waiting, and a car charging. It reads over Gleanvolt's own connection, so Gleanvolt keeps
running; a charger that serves one Modbus client at a time would drop a second one. For a go-e, the file is
a recording of its API's answers instead of registers.

## Sources

- [wills106/homeassistant-solax-modbus](https://github.com/wills106/homeassistant-solax-modbus) —
  `plugin_solax.py` (inverter families and register gating), `plugin_solax_ev_charger.py` (charger
  families), and its [Modbus adaptor setup](https://homeassistant-solax-modbus.readthedocs.io/en/latest/modbus-adaptor-setup/) page
- [Discussion #1776 — X3 Hybrid G4 Pro (GEN6) and remote control](https://github.com/wills106/homeassistant-solax-modbus/discussions/1776)
- [Discussion #349 — SolaX EV Charger](https://github.com/wills106/homeassistant-solax-modbus/discussions/349)
- [SolaX Hybrid X1/X3 G4 Modbus TCP/RTU protocol V3.21](https://www.gbc-solino.cz/files/uploads/FAQ%20SolaX/Hybrid-X1X3-G4-ModbusTCPRTU-V3.21-English_0622-public-version.pdf)
- [SolaX X1/X3-HAC user manual](https://www.solaxpower.com/uploads/file/x1-x3-hac-series-user-manual-en.pdf)
- [evcc discussion #7996 — SolaX Hybrid G4 Modbus](https://github.com/evcc-io/evcc/discussions/7996)
- Huawei's *Solar Inverter Modbus Interface Definitions*, and evcc's
  [`huawei-sun2000-hybrid.yaml` and `huawei-emma.yaml`](https://github.com/evcc-io/evcc/tree/master/templates/definition/meter)
  (MIT)
- Huawei's *Northbound Modbus-TCP Interconnection Protocol for AC Chargers* V1.0 (2023-08-04), attached to
  [bolliy/ioBroker.sun2000#171](https://github.com/bolliy/ioBroker.sun2000/issues/171) and
  [evcc discussion #8985](https://github.com/evcc-io/evcc/discussions/8985)
- [goecharger/go-eCharger-API-v2](https://github.com/goecharger/go-eCharger-API-v2) — the HTTP API and its
  key list
- [Dražice inverters](https://www.dzd.cz/fotovoltaika-drazice/stridace), the
  [IN.HYBRID COMPACT PRO datasheet](https://www.dzd.cz/images/sortiment/in-hybrid-compact-pro/PL-stridace-inhybrid-compact-pro-cz.pdf),
  and SolaX's [X3-HYB G4 PRO page](https://www.solaxpower.com/our-products/x3-hyb-g4-pro.html) it was compared with
