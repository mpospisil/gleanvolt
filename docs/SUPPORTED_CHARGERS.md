# Supported EV chargers

EV chargers Gleanvolt can drive, by manufacturer. The tiers are explained in
[Supported PV systems](https://github.com/mpospisil/gleanvolt/blob/main/docs/SUPPORTED_PV_SYSTEMS.md#at-a-glance).
One charger is supported per installation.

**Every charger is driven through a plugin** from [gleanvolt-chargers](https://github.com/mpospisil/gleanvolt-chargers):
a small program for one charger family, with its own page for the charger's address and settings. Gleanvolt decides;
the plugin reads and commands the charger. Gleanvolt never opens a connection to the charger itself. Since 1.4 there
are no built-in charger drivers (#445). How to run one:
[Your charger](https://github.com/mpospisil/gleanvolt/blob/main/docs/CHARGER_SETUP.md).

### Chargers at a glance

| Manufacturer | Models | Plugin | Tier |
|---|---|---|---|
| [SolaX](https://github.com/mpospisil/gleanvolt-chargers/tree/main/solax) | **X1/X3-HAC**; the other HAC variants; X1/X3-EVC | `solax` | **Verified** (X1/X3-HAC); Expected |
| [Huawei](https://github.com/mpospisil/gleanvolt-chargers/tree/main/huawei) | SCharger-7KS-S0, SCharger-22KT-S0 | `huawei` | Expected |
| [go-e](https://github.com/mpospisil/gleanvolt-chargers/tree/main/goe) | Gemini, Gemini flex, HOMEfix, HOME+ (hardware V3 or later) | `goe` | Expected |
| [Shelly](https://github.com/mpospisil/gleanvolt-chargers/tree/main/shelly) | TopAC Portable EV Charger (EVE01-11R) | `shelly` | Expected |
| Others | — | a plugin someone writes | Not supported yet |

The plugin's name is its compose profile, its name in `GLEANVOLT_PLUGINS` and its image,
`ghcr.io/mpospisil/gleanvolt-plugin-<name>`. Each README in gleanvolt-chargers has the models, the setup and the
hardware's caveats.

### SolaX

[`solax`](https://github.com/mpospisil/gleanvolt-chargers/tree/main/solax), over Modbus TCP.

| Model | Tier | Notes |
|---|---|---|
| [**X1/X3-HAC**](https://www.solaxpower.com/our-products/smart-ev-charger-g2.html) | **Verified** | The reference installation |
| [**X1-HAC, X3-HAC, X1-HAC-S, X3-HAC-S, A1-HAC, J1-HAC, C1-HAC, C3-HAC**](https://www.solaxpower.com/our-products/smart-ev-charger-g2.html) — 4.6, 7.2, 11 and 22 kW | Expected | The same registers as the reference |
| [**X1-EVC 7 kW, X3-EVC 11 kW, X3-EVC 22 kW**](https://www.solaxpower.com/our-products/smart-ev-charger.html) | Expected | Early units were reported with Modbus TCP closed |

- **One Modbus client.** The charger drops a second Modbus TCP connection. A charger that evcc or Home Assistant
  already controls has to be **handed over** to the plugin, not shared.
- **Not through a SolaX Datahub, not in parallel mode, not linked to the inverter's Green/ECO modes.**

### Huawei SCharger

[`huawei`](https://github.com/mpospisil/gleanvolt-chargers/tree/main/huawei), over Modbus TCP. Switching its Modbus
server on, and what to do behind an EMMA, is in the plugin's README.

| Model | Tier | Notes |
|---|---|---|
| [**SCharger-7KS-S0**](https://solar.huawei.com/) (7.4 kW, one phase) | Expected | Behind an EMMA, keep it in dry run |
| [**SCharger-22KT-S0**](https://solar.huawei.com/) (22 kW, three phases) | Expected | As the 7KS |

- **No status.** The protocol has none, so no car and a waiting car both read *unknown*, not *available*.
- Every test recording is synthetic.

### go-e Charger

[`goe`](https://github.com/mpospisil/gleanvolt-chargers/tree/main/goe), over the go-e's local HTTP API v2, which is off
by default.

| Model | Tier | Notes |
|---|---|---|
| [**Gemini, Gemini flex, HOMEfix and HOME+**](https://go-e.com/) (hardware V3 or later), 11 and 22 kW | Expected | Phase switching is the go-e's own; Gleanvolt doesn't command it |

- Every test recording is synthetic.

### Shelly TopAC Portable EV Charger

[`shelly`](https://github.com/mpospisil/gleanvolt-chargers/tree/main/shelly), over the charger's local Gen2 RPC API.

| Model | Tier | Notes |
|---|---|---|
| [**TopAC Portable EV Charger**](https://shelly-api-docs.shelly.cloud/gen2/Devices/ShellyX/XT1/TopACPortableEVCharger) (EVE01-11R), 11 kW, three phases, 6–16 A | Expected | The plugin has the address, and the password when authentication is on |

- **6–16 A.** Gleanvolt refuses the plugin while `ChargeControl:MaxChargingCurrentAmps` is above 16 A.
- **Auto balance** (Shelly EM load management) fights the control loop: turn it off.
- Every test recording is synthetic.

### What Gleanvolt keeps for every charger

- **The safeguards.** The setpoint goes to the plugin only when it moved by
  `ChargeControl:CurrentChangeThresholdAmps`, never above what the charger says it takes, and in
  `ChargeControl:DryRun` nothing goes to the plugin at all.
- **The limits.** A plugin says what its charger can do when it connects, and Gleanvolt refuses it while
  `ChargeControl`'s current range or phases are outside that.
- **Availability.** With no plugin connected, or one that can't read its charger, no mode starts, and a running
  mode is suspended until the charger is back.
- **A recording for testers** is on each plugin's page, in place of Gleanvolt's old register snapshot.

### Other chargers

Not supported yet. A plugin can add one, in any repository:
[the charger kind](https://github.com/mpospisil/gleanvolt/blob/main/docs/PROVIDERS.md#the-charger-kind) and the
[Gleanvolt.Chargers.Hosting README](https://github.com/mpospisil/gleanvolt/blob/main/src/Gleanvolt.Chargers.Hosting/README.md).
