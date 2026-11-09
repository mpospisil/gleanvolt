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
| `huawei-scharger-22kt-idle.json` | SCharger-22KT-S0, no car, maximum power at its 22 kW default, control on standby | `HuaweiSCharger22KtConformanceTests`: idle reads Unknown and 0 W |
| `huawei-scharger-22kt-plugged-in.json` | The same with a car waiting: nothing flowing, so the registers read as idle | Plugged in reads Unknown, never Available |
| `huawei-scharger-22kt-charging.json` | Charging at 16 A on three phases, 11.0 kW, control resumed | Charging, 11 000 W, 16 A, allowed |
| `huawei-scharger-7ks-idle.json` | SCharger-7KS-S0, one phase (L2 and L3 read 0), no car, 7.4 kW maximum | `HuaweiSCharger7KsConformanceTests`: idle |
| `huawei-scharger-7ks-plugged-in.json` | The same with a car waiting | Plugged in |
| `huawei-scharger-7ks-charging.json` | Charging at 16 A on one phase, 3.7 kW | Charging, 3 700 W, 16 A |
| `solax-charger-idle.json` | The SolaX charger (linked from the SolaX tests) | The SCharger probe refuses another charger |
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

**The SCharger files are synthetic too** (#351). They are written from Huawei's *Northbound Modbus-TCP
Interconnection Protocol for AC Chargers* V1.0: holding registers `0x1000`–`0x100D` (voltages and currents
×10, total power in kW ×10), the maximum charge power at `0x2000` (kW ×10) and the charging control at `0x2006`
(0 standby, 1 paused, 2 resumed). The protocol has no status register and no serial number, so there is
nothing to redact. A tester's snapshot (#354) has to confirm that the registers answer at all on current
firmware, what `0x2006` reads after the charger starts a car on its own, and whether the maximum power
written is what the car then draws.
