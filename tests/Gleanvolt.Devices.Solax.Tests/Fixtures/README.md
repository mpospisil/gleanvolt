# SolaX register dumps

The recordings `SolaxConformanceTests` (the inverter, issue #319) and `SolaxChargerConformanceTests` (the
X3-HAC charger, issue #350) replay. Each one is a device in a known state.

## Inverter

| File | State | Used for |
|---|---|---|
| `solax-night-importing.json` | Night, battery idle at its floor, house importing (**real**, 2026-11-08 19:56 UTC) | Grid import is positive; no sun, idle battery read as about zero |
| `solax-battery-charging.json` | Cloudy day, battery charging from the sun (**real**, 2026-11-09 11:22 UTC) | Battery charging is positive |
| `solax-ev-charger.json` | The SolaX EV charger answering at the inverter's address (**real**, 2026-11-09 11:30 UTC) | The probe refuses another family |
| `solax-night-discharging.json` | Night, battery carrying the house (**real**, 2026-11-08 16:37 UTC) | Discharging reads negative, no sun; range, probe and serial checks |
| `solax-night-ev-charging.json` | Night, battery at its floor, car charging at 16 A (**real**, 2026-11-08 20:46 UTC) | A heavy import reads as the meter's own figure, unscaled; range, probe and serial checks |

**All five are real.** A new inverter recording is a **Settings → Save register snapshot** in the state its
file name describes. The charger serves one Modbus client at a time, so its recording needs the controller
stopped for the minute it takes; it was read with the SolaX inverter driver's snapshot ranges, which is
what the inverter's probe sees when the two addresses are swapped. Its `0x001C` reads 24, a plausible state
of charge, which is why the probe also asks for the serial number's model prefix at holding `0x0000`; the
charger refuses that address, and the synthetic file this one replaced had hidden it.

Snapshots keep only the serial number's four-character model prefix; `The_recordings_keep_only_the_serial_prefix`
fails on a file that keeps more, because these are committed to a public repository.

## Charger

| File | State | Used for |
|---|---|---|
| `solax-charger-idle.json` | No car, use-mode Stop, setpoint 16 A (**real**, 2026-11-09 13:51 UTC) | Idle reads `Available` and about 0 W |
| `solax-charger-plugged-in.json` | A car plugged in and waiting, use-mode Stop (**synthetic**) | A plugged-in state with no power |
| `solax-charger-charging.json` | Charging at 16 A on three phases, use-mode Fast (**synthetic**) | `Charging` and positive power; setpoint, start/stop and snapshot checks |
| `solax-night-importing.json` | The SolaX inverter (above) | The probe refuses the inverter at the charger's address |

The idle file is a **Settings -> charger -> Save register snapshot** of the reference installation, taken with
the controller running. **The other two are synthetic until real ones replace them:** they are the idle
snapshot with RunMode (`0x001D`) set to the state the file names and, for the charging one, the total power
(`0x000B`) set to 11040 W and the use-mode (`0x060D`) to Fast. A real one is the same button, in the state the
file name describes.
