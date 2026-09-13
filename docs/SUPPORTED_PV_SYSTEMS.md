# Supported PV systems

Which inverters and EV chargers Gleanvolt can drive. It talks **Modbus TCP** to two devices on your
LAN — a SolaX hybrid inverter and a SolaX EV charger — using register maps written for the reference
installation's **X3-HYB-G4 PRO** and **X1/X3-HAC**.

Nothing selects a register map by model: `Pv:Inverter:Model` and `Pv:Chargers:0:Model` are
documentation. So *supported* here means one thing — **the device answers the same registers, with the
same meaning, as the reference hardware.**

> **Last reviewed 2026-09-13.** Which SolaX families share those registers comes from the
> [wills106/homeassistant-solax-modbus](https://github.com/wills106/homeassistant-solax-modbus)
> integration, which this project's register maps already cite: it detects each model by serial-number
> prefix and enables every register only for the families that have it. Only the reference hardware
> has been run. Register addresses can differ between firmware revisions, so **every write stays off
> or in dry-run until you have checked the readings against the device's own display.**

## At a glance

| Tier | What it means |
|---|---|
| **Verified** | Run against the reference installation |
| **Expected** | The same registers, per the upstream map. Not tested — read the caveats on its row |
| **Readings and charging only** | Telemetry and charge control work; the battery discharge hold does not exist on this family |
| **Not supported** | A different register map, or registers that mean something else |

## What Gleanvolt needs from the hardware

| Device | Registers | Used for |
|---|---|---|
| Inverter — read, one block `0x00`–`0x74` (input registers) | `0x0A`, `0x0B` — PV power, strings 1 and 2 | Solar, house load |
| | `0x16` battery power, `0x1C` battery SOC | The home battery |
| | `0x46`/`0x47` grid meter | Surplus, grid import/export |
| | `0x6C`, `0x70`, `0x74` — inverter output per phase | Only [`UnmeteredGridPhase`](../README.md#the-pv-system-the-pv-section) |
| Inverter — write (holding) | `0x7C` — the 13-register Modbus Power Control command | Only the battery discharge hold |
| EV charger — read (input) | `0x0B` charge power, `0x1D` run state | Every charging mode |
| EV charger — read and write (holding) | `0x60D` use mode, `0x628` charge current | Every charging mode |

---

## Inverters

### Verified

| Model | Family | Serial prefix | Readings | Battery hold |
|---|---|---|---|---|
| **SolaX X3-HYB-G4 PRO** | GEN6 hybrid | `10K` | Verified | **Verified** 2026-07-27 — see [DECISIONS.md](DECISIONS.md) |

The battery hold on this model is Gleanvolt's own evidence, not upstream's. The upstream integration
classes the G4 PRO as GEN6 and does not enable `0x7C` for it, citing incomplete documentation for that
generation; its newer "mode 8" control was reported to have no effect on a G4 PRO
([discussion #1776](https://github.com/wills106/homeassistant-solax-modbus/discussions/1776)). A
different firmware revision may behave differently.

### Expected — same registers, not tested

| Model | Family | Serial prefix | Battery hold | Caveats |
|---|---|---|---|---|
| **X3-Hybrid G4** | GEN4 hybrid | `H34` | Supported upstream | — |
| **TIGO TSI X3** | GEN4 hybrid | `H31` | Supported upstream | — |
| **Qcells Q.VOLT HYB-G3-3P** | GEN4 hybrid (listed by upstream) | — | Supported upstream | — |
| **X1-Hybrid G4** | GEN4 hybrid | `H43`, `H44`, `H450`, `H460`, `H475` | Supported upstream | [3](#3-x1-models-and-the-block-read) |
| **X1-IES** | GEN5 hybrid | `H53`, `H55`, `H56`, `H58` | Supported upstream | [1](#1-only-pv-strings-1-and-2-are-read) on 5–8 kW |
| **X3-IES** | GEN5 hybrid | `H35…`, `P35…` | Supported upstream | — |
| **X3-Ultra** | GEN5 hybrid | `H3B…` | Supported upstream | [1](#1-only-pv-strings-1-and-2-are-read) on some sizes, [2](#2-only-battery-1-is-read) |
| **X3-Aelio** | GEN5 hybrid (commercial) | `8021` | Supported upstream | [1](#1-only-pv-strings-1-and-2-are-read) |
| **X1-VAST** | GEN6 hybrid | `10M` | **Untested anywhere** | [1](#1-only-pv-strings-1-and-2-are-read), [3](#3-x1-models-and-the-block-read) |

### Readings and charging only

| Model | Family | Serial prefix |
|---|---|---|
| **X1-Hybrid G2** (SK-TL, SK-SU) | GEN2 hybrid | `L30`, `U30`, `L37`, `U37`, `L50`, `U50` |
| **X1-Hybrid G3** | GEN3 hybrid | `H1E`, `H1I`, `HCC`, `HUE`, `XRE` |
| **X3-Hybrid G3** | GEN3 hybrid | `H3DE`, `H3E`, `H3LE`, `H3PE`, `H3UE` |

The Modbus Power Control block at `0x7C` exists from GEN4 on. Leave `BatteryHold:Enabled` off on
these: there is nothing at that address for the hold to write to.

### Not supported

- **AC-coupled, RetroFit and FIT inverters** — X1-AC G3 (`XAC`), X3-RetroFit G3 (`F3D`, `F3E`),
  X1-RetroFit G4 (`F43`, `F450`, `F460`, `F475`, `PRE`), X3-Fit G4 (`F34`), X1-FIT G3 (`PRI`). They have
  no DC PV input, so `0x0A`/`0x0B` read nothing: solar shows 0 and every house-load and surplus figure
  derived from it is wrong.
- **PV-only inverters** — X1-Boost, X1-Mini, X1-Air, X1-SMART-G2, X3-MIC / MIC PRO. A different map, and
  no battery.
- **Other SolaX lines with their own register maps** — X3-MEGA / FORTH G2, A1-Hybrid, J1-Hybrid,
  X1-Hybrid-LV, X1-Lite-LV.
- **Inverters from other manufacturers.**

### Caveats

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
need those registers — they serve only `UnmeteredGridPhase`, which is a three-phase correction.

---

## EV chargers

### Verified

| Model | Family |
|---|---|
| **SolaX X1/X3-HAC** | GEN2 (HAC) — the reference installation |

### Expected — same registers, not tested

| Model | Family | Serial | Notes |
|---|---|---|---|
| **X1-HAC, X3-HAC, X1-HAC-S, X3-HAC-S, A1-HAC, J1-HAC, C1-HAC, C3-HAC** — 4.6, 7.2, 11 and 22 kW | GEN2 (HAC) | `5` + model code + power code | Same four registers as the reference |
| **X1-EVC 7 kW, X3-EVC 11 kW, X3-EVC 22 kW** | GEN1 (EVC) | `C107`, `C311`, `C322` | Upstream enables all four registers. With firmware 7.0 or later upstream treats the unit as a HAC. Early units were reported with Modbus TCP port 502 closed and reachable only over RS485 at address 70 ([discussion #349](https://github.com/wills106/homeassistant-solax-modbus/discussions/349), 2023) |

### Not supported

- **A charger controlled through a SolaX Datahub.** Charge current is then `0x624`, which Gleanvolt does
  not write.
- **Several chargers in parallel mode.** The mode register is then `0x669`. `Pv:Chargers` supports one
  charger anyway.
- **A charger linked to the inverter for SolaX's own Green/ECO modes** was reported to stop answering
  Modbus directly (same discussion). Gleanvolt needs the charger on its own Modbus TCP connection.

### Caveats

- **Gleanvolt writes two charger registers**: `0x628` every time the setpoint changes, and `0x60D` when a
  mode starts or stops. Run `ChargeControl:DryRun` first and compare the logged values with the charger.
- **Not yet confirmed: EEPROM.** SolaX warns that some holding-register writes are stored in EEPROM, which
  has a limited number of write cycles. The inverter's `0x7C` command is documented as not stored; for the
  charger's `0x628` and `0x60D` this project has no answer from SolaX's protocol document yet.
- **6–32 A, no phase switching.** On three phases the charger's 6 A floor is about 4.2 kW.

---

## Connecting

- **Modbus TCP on port 502, unit id 1** for both devices. Both are configurable
  (`Pv:Inverter:Port`, `Pv:Inverter:UnitId`, and the same under `Pv:Chargers:0`).
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

## Sources

- [wills106/homeassistant-solax-modbus](https://github.com/wills106/homeassistant-solax-modbus) —
  `plugin_solax.py` (inverter families and register gating), `plugin_solax_ev_charger.py` (charger
  families), and its [Modbus adaptor setup](https://homeassistant-solax-modbus.readthedocs.io/en/latest/modbus-adaptor-setup/) page
- [Discussion #1776 — X3 Hybrid G4 Pro (GEN6) and remote control](https://github.com/wills106/homeassistant-solax-modbus/discussions/1776)
- [Discussion #349 — SolaX EV Charger](https://github.com/wills106/homeassistant-solax-modbus/discussions/349)
- [SolaX Hybrid X1/X3 G4 Modbus TCP/RTU protocol V3.21](https://www.gbc-solino.cz/files/uploads/FAQ%20SolaX/Hybrid-X1X3-G4-ModbusTCPRTU-V3.21-English_0622-public-version.pdf)
- [SolaX X1/X3-HAC user manual](https://www.solaxpower.com/uploads/file/x1-x3-hac-series-user-manual-en.pdf)
- [evcc discussion #7996 — SolaX Hybrid G4 Modbus](https://github.com/evcc-io/evcc/discussions/7996)
