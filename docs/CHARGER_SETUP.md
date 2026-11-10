# Setting up a Huawei SCharger or a go-e

How to connect Gleanvolt to an EV charger that is not a SolaX: Huawei's **SCharger-7KS-S0** or
**-22KT-S0**, or a **go-e Charger**. What works, and how well it has been tested, is in
[Supported EV chargers](SUPPORTED_CHARGERS.md). In short: both are **Expected**. Their drivers
were written from the manufacturers' documentation, and nobody has run them against a real charger yet.

**Run in dry-run first.** `ChargeControl:DryRun` is on by default: Gleanvolt then only reads, and logs the
writes it would make. Leave it on until the charger's status and power on the dashboard match the
charger's own app.

## Huawei SCharger

### 1. Enable Modbus TCP

The SCharger has its own Modbus TCP server, and it is **off by default**. In the FusionSolar app, open the
charger's commissioning page and enable Modbus TCP under *Settings → Communication configuration*. This
path comes from public guides, not from a device: a tester should confirm it.

Some owners report port 502 refused on 2023 and 2024 firmware (V100R023C10SPC020), with Huawei support
saying third-party access was "not yet available". If the port stays closed, check for a firmware update.

### 2. Behind an EMMA, the EMMA manages the charger

An EMMA wants to manage a Huawei charger itself. One owner reports that configuring another controller on
an SCharger broke the EMMA's surplus charging until a factory reset. Behind an EMMA, **keep Gleanvolt in
dry-run** for the charger: it reads, and the EMMA keeps control. The EMMA's own Modbus interface has the
charger's model and energy, but no power, no status and no control, so Gleanvolt talks to the charger
directly, not through the EMMA.

### 3. Find the address, and pick the model

Run the setup wizard's device search, or [`gleanvolt-discover`](DISCOVERY.md). The
SCharger keeps no model name or serial number in its registers, so the scan recognises it by its phase
voltages: a mains voltage on L1 at holding register `0x1000`. It picks the model from the other phases:

- L2 and L3 live: the three-phase **22KT** (`huawei-22kt`).
- L1 only: the single-phase **7KS** (`huawei-7ks`).

A 22KT wired to one phase reads like a 7KS, so check the label. In the wizard, **Use as charger** fills in
the brand as well as the address.

By hand, on **Settings → Installation → Charger**:

| Field | Value |
|---|---|
| Charger brand | Huawei SCharger-7KS or Huawei SCharger-22KT |
| Charger address | the charger's address |
| Charger port | 502 |
| Charger unit id | 1. The protocol also allows 0 or 255 on a direct connection |

Optionally, in `.env` instead: `PV_CHARGER_DRIVER=huawei-22kt` (or `huawei-7ks`), and `EV_CHARGER_HOST`.

### 4. What to expect from the SCharger

- **No status.** The protocol reports power, voltages and currents, nothing else. A charging car shows as
  *charging*. No car and a car plugged in and waiting both show as *unknown*, never as *available*.
  Gleanvolt never ends a session on *unknown*.
- **The setpoint is power.** Gleanvolt converts its amps at `ChargeControl:NominalVoltage` on
  `ChargeControl:Phases`: 16 A on three phases is written as 11.0 kW. The charger reports power in 100 W
  steps.
- **Start and stop** write the charging control: resume and pause.

## go-e Charger

### 1. Enable the local HTTP API v2

Gleanvolt reaches a go-e over its **local HTTP API v2**, on port 80. It is **off by default**. In the go-e
app, open the charger, then *Internet → Advanced settings*, and switch on **Local HTTP API v2**.

It needs hardware **V3 or later**: the Gemini, Gemini flex, and HOMEfix or HOME+ V3/V4. Older units speak
only API v1, which Gleanvolt does not.

### 2. One controller at a time

Gleanvolt switches charging on and off with the go-e's force state, and sets its current. Anything else
that does the same fights it:

- **evcc** driving the go-e: hand control over. Do not run both.
- **The go-e app's own PV surplus charging** ("Eco" mode, with grid data from the app or an inverter
  link): turn it off. Gleanvolt does the surplus charging.
- A schedule or "next trip" in the app keeps working only while Gleanvolt is **Off**. When a mode starts,
  Gleanvolt forces charging on or off.

Gleanvolt never writes the go-e's phase switching, its maximum current or its logic mode.

### 3. Find the address

The wizard's search and `gleanvolt-discover` try port 80 on every host. A host that answers HTTP and is
not a SolaX dongle is asked for go-e's `/api/status`. If it answers, it is named a go-e, with the `goe`
driver. A go-e with the API switched off answers like any other web server, and is not reported.

By hand, on **Settings → Installation → Charger**:

| Field | Value |
|---|---|
| Charger brand | go-e |
| Charger address | the go-e's address on your LAN |
| Charger port | 80 |
| Charger unit id | unused: leave it as it is |

Optionally, in `.env` instead: `PV_CHARGER_DRIVER=goe`, and `EV_CHARGER_HOST`.

"Test connection" asks the API for the car state. If it says *not with the selected charger's API*, the
local API is still off.

### 4. What to expect from the go-e

- **Status** comes from the go-e's car state and whether it allows charging. A car that waits or has
  finished while the go-e blocks charging is the charger's doing, never "the car is full".
- **Start and stop** set the force state to on and off. The go-e's own *neutral* does not count as started.
- **A pause is the force state off.** The go-e refuses a current below 6 A, and Gleanvolt pauses by asking
  for 0 A. So a pause switches charging off, and the next current of 6 A or more switches it back on.
- **Two settings are written, only when they change:** the current (`amp`) and the force state (`frc`).
  The go-e keeps both across a restart. Its API documents no current that is not stored, so whether its
  memory minds the control loop's rate is one of the things a tester has to confirm. evcc writes the same
  setting.
- An **11 kW unit** is never asked for more than 16 A.

## Help confirm it

Nobody on the project has either charger yet. On **Settings**, under the charger, take a **register
snapshot** in three moments: no car, a car plugged in and waiting, and a car charging. For a go-e it is a
recording of the API's answers, without the serial number, Wi-Fi or cloud keys. Attach the files to
[issue #354](https://github.com/mpospisil/gleanvolt/issues/354). Say:

- whether the status and power on the dashboard match the charger's own app at the same moment;
- your charger model and firmware, and your inverter;
- whether an EMMA, evcc or anything else controls the charger.
