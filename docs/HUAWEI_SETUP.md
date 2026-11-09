# Setting up a Huawei SUN2000

How to connect Gleanvolt to a Huawei SUN2000 hybrid inverter. What works, and how well it has been tested,
is in [Supported inverters](SUPPORTED_INVERTERS.md#huawei). In short: readings are **Expected**
(written from Huawei's interface definitions, no real device yet), and the battery hold is **Untested**.

## What you need

- A **SUN2000 hybrid**: three-phase -KTL-M1 or -MAP0, or single-phase -KTL-L1 or -LC0.
- A **LUNA2000 battery**. Several towers report as one battery.
- A **Huawei smart meter at the grid connection**: DTSU666-H (three-phase) or DDSU666-H (single-phase),
  wired to the inverter. Behind an EMMA, the EMMA's built-in sensor does this job. Without a meter
  there is no grid figure. Every surplus Gleanvolt computes would then be wrong.
- Either the inverter's **SDongle** (SDongleA-05) on your LAN, or an **EMMA** energy manager.
- The **installer account** for FusionSolar. Modbus TCP can only be enabled with it.

## 1. Enable Modbus TCP

Modbus TCP is **off by default** on both the SDongle and the EMMA. Until it is on, the port answers but
refuses Modbus. [`gleanvolt-discover`](../README.md#finding-your-devices) and the setup wizard report such
a port as *port open, no Modbus reply*.

**SDongle.** In the FusionSolar app, open *Device commissioning* for the inverter and log in as the
installer. Under *Settings → Communication configuration → Dongle parameter settings → Modbus TCP*,
choose **Enable (Unrestricted)**. The restricted setting only answers the addresses on its list.

**EMMA.** In the FusionSolar app, open the EMMA's commissioning page as the installer. Under
*Commission device → Communication settings → Modbus TCP*, enable it.

## 2. Only one client at a time

The SDongle and the EMMA serve very few Modbus TCP clients. The EMMA serves exactly one: it resets any
further connection. Gleanvolt keeps one connection open all the time, so:

- **Home Assistant's `huawei_solar` integration competes for the same connection.** Run one or the
  other. To run both, put a Modbus proxy in front of the dongle or EMMA, and point both at the proxy.
- Stop Gleanvolt while you run `gleanvolt-discover`. Otherwise the scan finds the port taken.
- Behind an EMMA, Gleanvolt reads the EMMA and the inverter behind it over its one connection.

## 3. Find the address

Run the setup wizard's device search, or [`gleanvolt-discover`](../README.md#finding-your-devices). It reads
the model name at register `30000` and names a SUN2000 by it, with the `huawei` driver. Behind an EMMA it
also notices the EMMA and suggests *Connected through: EMMA*. In the wizard, **Use as inverter** fills in
the brand and the connection as well as the address.

By hand, on **Settings → PV system → Inverter**:

| Field | Through the SDongle | Behind an EMMA |
|---|---|---|
| Inverter brand | Huawei | Huawei |
| Connected through | SDongle | EMMA |
| Inverter address | the SDongle's address | the EMMA's address |
| Inverter port | 502 | 502 |
| Inverter unit id | 1 | the unit id the EMMA gave the inverter, usually 1 |

In `.env`: `Pv__Inverter__Driver=huawei`, and `Pv__Inverter__Topology=emma` behind an EMMA.

"Test connection" reads the model name and the battery's state of charge. Behind an EMMA it reads the
EMMA's grid sensor first.

## 4. Behind an EMMA: who schedules the battery

The EMMA's own **AI energy optimisation** schedules the battery too. If you turn on Gleanvolt's battery
hold, switch the EMMA's optimisation off and set the EMMA to **maximum self-consumption**. Only one system
may schedule the battery. With the hold off, Gleanvolt only reads, and the EMMA's settings do not matter.

## 5. The battery hold

Leave `BatteryHold:Enabled` off, or on with `BatteryHold:DryRun` true, until it has been confirmed on a
real inverter. The hold sets the battery's maximum discharging power to 0, and puts the original back
afterwards. That is a setting the inverter keeps, so Gleanvolt keeps the original on disk until it has put
it back. [Supported inverters](SUPPORTED_INVERTERS.md#huawei) explains how it recovers after a
crash. If Gleanvolt is removed while a hold is on, set the maximum discharging power back in the
FusionSolar app.

## 6. Help confirm it

Nobody on the project has a Huawei yet, so the first real installation decides what moves above
"Expected". On **Settings**, under the inverter, take a **register snapshot** in two moments: at night with
the battery idle and the house importing, and with the battery charging. Attach both to
[issue #325](https://github.com/mpospisil/gleanvolt/issues/325). Say whether the grid and battery figures
on the dashboard match the FusionSolar app, sign included. The snapshot removes the serial number.
