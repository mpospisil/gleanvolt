# SolaX register dumps

The recordings `SolaxConformanceTests` replays (issue #319). Each one is a device in a known state:

| File | State | Used for |
|---|---|---|
| `solax-night-importing.json` | Night, battery idle at its floor, house importing (**real**, 2026-11-08 19:56 UTC) | Grid import is positive; no sun, idle battery read as about zero |
| `solax-battery-charging.json` | Cloudy day, battery charging from the sun (**real**, 2026-11-09 11:22 UTC) | Battery charging is positive |
| `solax-ev-charger.json` | The SolaX EV charger answering at the inverter's address | The probe refuses another family |
| `solax-night-discharging.json` | Night, battery carrying the house (**real**, 2026-11-08 16:37 UTC) | Discharging reads negative, no sun; range, probe and serial checks |
| `solax-night-ev-charging.json` | Night, battery at its floor, car charging at 16 A (**real**, 2026-11-08 20:46 UTC) | A heavy import reads as the meter's own figure, unscaled; range, probe and serial checks |

**`solax-ev-charger.json` is the one synthetic file left.** It was written by hand from the reference
installation's figures, and its `note` says so. Replace it with **Settings → Save register snapshot** of the
charger, with the same note text minus "SYNTHETIC"; the tests do not change. For that, point a dev build's `Pv:Inverter:Host` at the charger's address and take the
snapshot there.

Snapshots keep only the serial number's four-character model prefix; `The_recordings_keep_only_the_serial_prefix`
fails on a file that keeps more, because these are committed to a public repository.
