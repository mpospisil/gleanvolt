# SolaX register dumps

The recordings `SolaxConformanceTests` replays (issue #319). Each one is a device in a known state:

| File | State | Used for |
|---|---|---|
| `solax-night-importing.json` | Night, battery idle, house importing | Grid import is positive; no sun, idle battery read as about zero |
| `solax-battery-charging.json` | Battery charging | Battery charging is positive |
| `solax-ev-charger.json` | The SolaX EV charger answering at the inverter's address | The probe refuses another family |
| `solax-night-discharging.json` | Night, battery carrying the house (**real**, 2026-11-08 16:37 UTC) | Discharging reads negative, no sun; range, probe and serial checks |

**The first three are synthetic for now.** They were written by hand from the reference installation's figures,
and their `note` says so. Replace each with **Settings → Save register snapshot** taken on the reference
installation in the state the file's name describes, with the same note text minus "SYNTHETIC". The tests
do not change. For the charger, point a dev build's `Pv:Inverter:Host` at the charger's address and take the
snapshot there.

Snapshots keep only the serial number's four-character model prefix; `The_recordings_keep_only_the_serial_prefix`
fails on a file that keeps more, because these are committed to a public repository.
