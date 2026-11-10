# Battery discharge hold (writes to the inverter)

A switch that stops the home battery serving the house, so charging the car never drains it. The sun
covers what it can, the grid covers the rest, and the battery is left alone — but it can **still
charge** from surplus.

It combines with any charge mode:

- **With charge mode `Off`** — the charger runs at whatever current you set at the wallbox; sun and
  grid cover it, the battery is untouched.
- **With `Solar`** — the surplus loop runs unchanged, with a safety net for the moments its estimate
  is briefly wrong.
- **With no car charging** — a "preserve the battery" switch, for example ahead of an expensive tariff
  or a known outage.

`FastNoBattery` switches it on for the length of its charge, and `Targeted` and `SolarGrid` arm it
while the car draws more than the sun gives; see their pages.

## How it works

The inverter, not Gleanvolt, decides where the car's power comes from: it sees the car as house load
and covers it from the battery. So the hold doesn't touch the charger. It tells the inverter how much
power to deliver to the house, every poll:

- **The sun covers the house** — deliver the whole house load; the sun left over goes to the battery.
- **The sun falls short** — deliver all the sun there is; the rest comes from the grid, never the
  battery.
- **The sun falls short and the battery is full** — deliver up to `FullPackHeadroomWatts` (1 kW) more
  than the sun reads, within the forecast and the house load, so a full battery can't hold the sun
  back. Only at `FullPackSocPercent` (95%) or above and while a forecast covers the moment, so it never
  applies at night.

**How it ends depends on the inverter:**

- **SolaX** — the hold is a command that lapses by itself after `Duration` (60 s) unless renewed, so if
  Gleanvolt stops, the inverter returns to normal within a minute. Nothing is stored in the inverter.
- **Huawei** — the hold sets the maximum discharging power to 0, which the inverter keeps. Gleanvolt
  stores the original value in `StatePath` (`data/inverter-state.json`) and writes it back on release,
  when it stops normally, and on the first poll after a crash. If Gleanvolt is gone for good while a
  hold is on, set the maximum discharging power back in the FusionSolar app. See
  [Huawei SUN2000](SUPPORTED_INVERTERS.md#huawei-sun2000).

Turning the switch off releases the hold at once.

## What the switch shows

The hold does **not** survive a restart and can't be switched on by configuration: the service always
starts with it **off**.

The switch shows **the last command Gleanvolt wrote successfully**, not a reading from the inverter,
which can't report it. A failed write shows as the switch springing back to off. A warning is logged if
the battery discharges more than 150 W while the hold is on.

**A working hold still leaves a 50–65 W trickle** out of the battery — the inverter's own standby draw.
The battery stops serving the house; its power does not reach exactly zero.

## Settings

```jsonc
"BatteryHold": {
  "Enabled": false,                 // master switch -- while off, nothing can write to the inverter
  "DryRun": true,                   // decide and log, but write nothing
  "Duration": "00:01:00",           // SolaX: how long each command lasts; also how soon it lapses
  "StatePath": "data/inverter-state.json", // Huawei: the setting the hold changed, so it can be put back
  "TargetChangeThresholdWatts": 100, // how far the target must move before it is sent again
  "BridgeShortfallWatts": 300,      // SolarGrid/Targeted: arm when the car draws this much more than the sun gives
  "BridgeReleaseDwell": "00:03:00", // SolarGrid/Targeted: keep it this long after the last need, while charging
  "FullPackSocPercent": 95,         // at or above this, a battery that isn't charging counts as full
  "FullPackHeadroomWatts": 1000     // full battery: how far past the sun's reading the target may reach; 0 = off
}
```

While `Enabled` is off, the switch doesn't appear in the web UI or Home Assistant, and the inverter
connection is read-only.

> ⚠️ **This is the only feature that writes to your inverter**, and it ships off and in dry-run.
> It has been verified only on the reference SolaX X3-HYB-G4 PRO; the Huawei hold has not been run on
> a device yet. Before turning `DryRun` off, run it in dry-run and read the log. After, **judge whether
> a hold held by the battery's power, every time** — on the session chart or **Battery power** — not by
> the switch. Which inverters support it is in [Supported inverters](SUPPORTED_INVERTERS.md).
