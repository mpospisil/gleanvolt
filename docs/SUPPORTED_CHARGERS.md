# Supported EV chargers

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
