# Supported inverters

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
