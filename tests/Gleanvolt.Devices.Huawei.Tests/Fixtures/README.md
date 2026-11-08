# Huawei register dumps

The recordings `HuaweiConformanceTests` replays (issues #319, #320). Each one is a device in a known state:

| File | State | Used for |
|---|---|---|
| `huawei-night-importing.json` | SUN2000-10KTL-M1, night, battery idle, house importing 850 W | Grid import is positive; no sun, idle battery read as about zero |
| `huawei-battery-charging.json` | SUN2000-10KTL-M1, midday, battery charging 3500 W, exporting 1200 W | Battery charging is positive, export negative |
| `huawei-map0-night-discharging.json` | SUN2000-8K-MAP0, night, battery carrying the house | Discharging reads negative, no sun |
| `solax-inverter.json` | A SolaX inverter at the address (linked from the SolaX tests, not a copy) | The probe refuses another family |

**All three Huawei files are synthetic.** They were generated from Huawei's *Solar Inverter Modbus Interface
Definitions*: the addresses, encodings (32-bit values high word first) and the documented signs, with the
meter counting export as positive. Their `note` says so. The signs are exactly what a real dump has to
confirm, so the family stays **Expected** until a tester's **Settings → Save register snapshot** (#325)
replaces at least the importing and charging files. The tests do not change.

The serial number (holding `30015`–`30024`) is removed whole: the model name at `30000` already says what
the device is. `The_recordings_keep_only_the_serial_prefix` fails on a file that keeps any of it.
