# Huawei register dumps

The recordings `HuaweiConformanceTests` replays (issues #319, #320). Each one is a device in a known state:

| File | State | Used for |
|---|---|---|
| `huawei-night-importing.json` | SUN2000-10KTL-M1, night, battery idle, house importing 850 W | Grid import is positive; no sun, idle battery read as about zero |
| `huawei-battery-charging.json` | SUN2000-10KTL-M1, midday, battery charging 3500 W, exporting 1200 W | Battery charging is positive, export negative |
| `huawei-map0-night-discharging.json` | SUN2000-8K-MAP0, night, battery carrying the house | Discharging reads negative, no sun |
| `huawei-l1-night-importing.json` | SUN2000-5KTL-L1, single-phase, DDSU666-H meter, night, importing 620 W | Single-phase import is positive; the meter's one phase is the total |
| `huawei-lc0-battery-charging.json` | SUN2000-6K-LC0, single-phase, DDSU666-H meter, battery charging 2500 W, exporting 900 W | Single-phase charging positive, export negative |
| `huawei-emma-night-importing.json` | SUN2000-10K-MAP0 on unit 1 behind an EMMA (unit 0), night, importing 900 W | The EMMA suite: import positive, read from the EMMA |
| `huawei-emma-battery-charging.json` | The same, battery charging 4000 W, exporting 1500 W | The EMMA suite: charging positive, export negative |
| `solax-inverter.json` | A SolaX inverter at the address (linked from the SolaX tests, not a copy) | The probe refuses another family |

**Every Huawei file is synthetic.** They were generated from Huawei's *Solar Inverter Modbus Interface
Definitions*: the addresses, encodings (32-bit values high word first) and the documented signs, with the
meter counting export as positive. Their `note` says so. The signs are exactly what a real dump has to
confirm, so the family stays **Expected** until a tester's **Settings → Save register snapshot** (#325)
replaces at least the importing and charging files, three-phase and single-phase. A single-phase dump
also has to show what `A_single_phase_meter_reports_its_one_phase_as_the_total` assumes: meter type
`0` at `37125`, and the total at `37113` equal to phase A at `37132`. The tests do not change.

The serial number (holding `30015`–`30024`) is removed whole: the model name at `30000` already says what
the device is. `The_recordings_keep_only_the_serial_prefix` fails on a file that keeps any of it.
