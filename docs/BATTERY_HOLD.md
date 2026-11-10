# Battery discharge hold (writes to the inverter)

A switch that stops the home battery discharging, so charging the EV never drains it. PV covers what
it can, the grid covers the rest, and the battery is left alone — but it can **still charge** from PV
surplus, which is the whole point of using this rather than simply freezing the battery.

It is deliberately orthogonal to charge control, not a third charge mode:

- **Hold on + charge mode `Off`** — the charger runs at whatever current you set in Fast mode. PV
  covers what it can, the grid tops up, the battery is untouched.
- **Hold on + charge mode `Solar`** — the surplus loop runs unchanged, with a safety net underneath it
  for the moments its estimate is briefly wrong.
- **Hold on with no EV charging** — a general "preserve the battery" switch, e.g. ahead of an
  expensive tariff period or a known outage.

## How it works

The inverter decides where the EV's power comes from, not this controller — in Self Use mode it sees
the EV as household load and discharges the battery to cover it, whatever charging current we set. So
the hold doesn't touch the charger at all. It uses the inverter's **Modbus Power Control** command
(holding register `0x7C`) to drive the inverter's grid-connection point to a commanded power target of
`-min(house load, PV)`:

- **PV covers the house** — push out the whole load. The house runs on sun, and the PV it doesn't need
  has nowhere to go but the battery, so surplus charging is preserved.
- **PV falls short** — push out all the PV there is. The inverter is already at its maximum, so the
  shortfall can only come from the grid. The battery is never asked to contribute.
- **PV falls short and the pack is full** — push out up to `FullPackHeadroomWatts` (1 kW) more than PV
  reads, but never more than the forecast expects the roof to make right now, and never more than the
  load. "Already at its maximum" is only true while the pack can take the PV the target leaves over. A
  full pack can't, so a target of exactly the PV reading caps PV there. The pack's standby trickle then
  lets the next reading come in lower, and the target follows it down. On 2026-09-16 that took PV from
  6.1 kW to 115 W in three minutes under a clear sky, with the car on the grid. The headroom lets PV the
  target capped climb back. If the sun really is that weak, a full pack covers up to the headroom. It
  applies at `FullPackSocPercent` (95%) or above, while the pack takes no more than 100 W and a forecast
  covers the moment, so a night charge never borrows from the pack.

> **This is not the SolaX "No Discharge" mode.** That option exists in the upstream Home Assistant
> integration but never reaches the inverter — it is a client-side strategy, and the formula above is
> what it actually sends. See [docs/DECISIONS.md](DECISIONS.md).

Because the target follows live house load and PV, it is recomputed every poll and reissued whenever
it moves past `TargetChangeThresholdWatts`, plus a renewal at half of `Duration`. The command is *not*
a stored setting — nothing is written to EEPROM, and it lapses on its own.

**That expiry is the failsafe.** If the service stops, nothing renews the command and the inverter
returns to normal operation within `Duration` (60 s by default). There is no shutdown hook and no
cleanup path — the inverter provides the guarantee.

Turning the switch off writes a release immediately; it never waits for the duration to run out.

## Persistence and reported state

The hold does **not** survive a restart, and cannot be armed by configuration: the service always
comes back with the switch **off**, so the battery charges and discharges normally until somebody asks
otherwise. The inverter will already have resumed normal operation by then anyway, since the armed
command expires after `Duration`. That is deliberate — the hold is a command with a lifetime, not a
stored setting, so an unattended restart that re-armed it would silently keep the pack idle.

The Home Assistant switch reports **what the controller last successfully wrote**, not a reading from
the inverter — register `0x7C` reports the firmware version when read, so the command state cannot be
read back. A failed write therefore shows up as the switch springing back to OFF rather than as an
assumed success. As a cross-check, the controller logs a warning if the battery discharges by more
than 150 W while it believes the hold is armed.

**A working hold still leaves a 50–65 W trickle out of the battery** — inverter standby draw, not
load being served; measured here across house loads from 143 W to 2877 W. So the guarantee is that
the battery stops *serving the house*, not that battery power reaches exactly zero. The 150 W
deadband above exists for this reason: warning on any negative value fires every poll and drowns out
the signal it is there to give.

## Configuring the battery hold

```jsonc
"BatteryHold": {
  "Enabled": false,                 // master switch — while off, inverter writes are impossible
  "DryRun": true,                   // decide and log, but write nothing
  "Duration": "00:01:00",           // how long each command stays armed; also the failsafe window
  "StatePath": "data/inverter-state.json", // Huawei: the setting the hold changed, so a crash can undo it
  "TargetChangeThresholdWatts": 100, // how far the target must move before reissuing
  "BridgeShortfallWatts": 300,      // SolarGrid/Targeted: arm when the car draws this much more than the sun gives
  "BridgeReleaseDwell": "00:03:00", // SolarGrid/Targeted: keep it this long after the last need, while charging
  "FullPackSocPercent": 95,         // at or above this, a pack that isn't charging counts as full
  "FullPackHeadroomWatts": 1000     // full pack: how far past PV the target may reach, so it can't cap PV; 0 = off
}
```

`Enabled` is a true master switch: while it is off no Home Assistant
switch is published, the poll loop skips the feature, and the inverter's Modbus client is wrapped
read-only so a write is structurally impossible rather than merely skipped.

> ⚠️ **This is the only feature that writes to your inverter.** The register address, field layout and
> mode values come from the wills106 homeassistant-solax-modbus map, not from a SolaX document, and
> upstream reports behaviour differing across firmware versions.
>
> It **has been verified on the reference hardware** (X3-HYB-G4 PRO, 2026-07-27): arming the hold
> moved the house from 2846 W of battery discharge to 1601 W of grid import within one poll, renewal
> held it continuously, and PV was not curtailed. Full measurements are in
> [docs/DECISIONS.md](DECISIONS.md). Two things remain unobserved: behaviour under strong midday
> PV (does the battery still charge from surplus, and is PV curtailed at full output), and behaviour
> with the EV actually charging.
>
> None of that transfers to a different inverter or firmware, so `Enabled` stays `false` and `DryRun`
> stays `true` by default. **Validate with `DryRun: true` first** — it logs the exact block it would
> write (`[DRY RUN] would hold battery discharge: active power target -2000W for 60s (registers [...]
> at 0x7C)`) without touching the inverter.
