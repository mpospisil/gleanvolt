# Decision records

Append-only. A new record goes here whenever we adopt a library or establish a core pattern.

---

## 2026-07-26 — Battery discharge hold uses computed Power Control, not a device "No Discharge" mode

**Context.** Issue #20 asks for a switch that stops the home battery discharging, so an EV charges
from PV and grid but never from the battery, while the battery is still free to charge from PV
surplus. The issue proposed writing `power_control = Enabled No Discharge` to the Modbus Power
Control block at holding register `0x7C`, treating it as a fire-and-forget command: one write to arm
it for 8 hours, one write to release it, and nothing in between.

**What we found.** Desk verification against the upstream
[`plugin_solax.py`](https://github.com/wills106/homeassistant-solax-modbus/blob/main/custom_components/solax_modbus/plugin_solax.py)
map — the source the issue itself cites — contradicts that design in three places.

1. **`Enabled No Discharge` is not a device-level mode.** The `remotecontrol_power_control` entity is
   declared `WRITE_DATA_LOCAL`, meaning its option values (`11`, `12`, `110`, `120`, `130`) never
   reach the inverter — they are identifiers for client-side strategies. The device enum is only
   `0 = Disabled` and `1 = Enabled Power Control` (upstream lists `2 = Quantity Control` and
   `3 = SOC Target Control`, both commented out). Mode 8/9 at `0xA0` tells the same story: its
   `85: "Enabled No Discharge"` also resolves to a real device value of `8`.

2. **`active_power` is the mechanism, not an ignored field.**
   `autorepeat_function_remotecontrol_recompute` translates `Enabled No Discharge` into
   `power_control = Enabled Power Control` with `active_power = -min(house_load, pv_power)`. Because
   that target is derived from live house load and PV, it must be recomputed and rewritten
   continuously. A single 8-hour arming cannot express it.

3. **The block cannot be read back.** Holding register `0x7C` is overloaded: upstream *writes* the
   power-control command there but *reads* it as the inverter's ARM firmware version
   (`async_read_holding_registers(address=0x7B, count=2)`). No register exposes the active
   remote-control state — upstream tracks it with client-side timers only.

**Decision.** Implement the hold the way the hardware actually supports it: write
`power_control = Enabled Power Control` with `active_power = -min(house load, PV)`, recomputed each
poll from telemetry, and reissued when the target moves past a threshold or the armed command nears
expiry. This preserves both halves of the requirement — the battery is never asked to serve load
(the inverter is only ever told to push out power it is already generating), and PV beyond the house
load has nowhere to go but the battery, so surplus charging still works.

Consequences, deliberately accepted:

- **The "at most one write per 8 hours" acceptance criterion is dropped.** The write rate is instead
  bounded by `BatteryHold:TargetChangeThresholdWatts` (default 100 W) and the renewal interval. The
  command is not EEPROM-backed — upstream states these may be issued as often as desired — so this
  costs Modbus traffic, not hardware wear.
- **`Duration` is 60 s, not 8 hours.** With per-poll reconciliation a short duration is a *better*
  failsafe: if the service stops, the inverter resumes normal operation within a minute instead of
  within eight hours. Renewal happens at half the duration so a slow poll never leaves a gap. The
  8-hour figure survives only as the hardware ceiling (`u16`, 28,800 s) enforced in the encoder.
- **The Home Assistant switch reports our own armed state, not a device read-back.** The acceptance
  criteria around reading the hold back, surviving a restart by reading device state, and correcting
  a manual change made in the SolaX app are not implementable — and the last is moot anyway, since
  this is a command rather than a stored setting the app could show or alter.
- **Upstream's SOC ≥ 98 % branch is not implemented.** There, the target becomes
  `-pv_power - 150`, deliberately trickle-discharging the battery to keep SOC near 98 % and stop
  older inverters curtailing PV. That contradicts this issue's "battery power is never negative"
  requirement, so it is left out pending observation of whether PV curtailment actually occurs on
  this hardware.

**Why not the alternatives.** Unchanged from the issue, and reinforced by the above:

| Approach | Verdict |
|---|---|
| Computed Power Control (`0x7C`, mode 1) | **Chosen.** The only route that both blocks discharge and preserves PV → battery charging. Not EEPROM-backed. |
| Raise discharge cut-off / min SOC | Rejected. Modifies a stored parameter on a ~100,000-cycle EEPROM, 1 % granular, and drifts as SOC moves. |
| Battery Discharge Max Current = 0 | Rejected. Same EEPROM problem; also fights the inverter's own limits. |
| Manual Mode → "Stop charge and discharge" | Rejected. Freezes the battery in *both* directions, so PV surplus exports instead of charging the battery. Remains the manual fallback if the Modbus route fails verification. |

### Verified on hardware, 2026-07-27

First live write to the inverter. Conditions: dusk, PV ~360 W, SOC 87 %, no EV charging, house load
~1.5–2.9 kW.

**The mechanism works.** Arming the hold moved the house from battery to grid within one poll:

| | Battery | Grid | Solar |
|---|---|---|---|
| Before the write | **−2846 W** (discharging) | 0 W | 366 W |
| After the write | **−56 W** | **+1601 W** (importing) | 370 W |

Confirmed by this run:

- **`power_control = 1` with a computed `active_power` is accepted** and takes effect immediately —
  no Modbus exception, no rejected block. The encoded payload
  `[1,1,65170,65535,0,0,60,0,0,0,0,0,0]` at `0x7C` is correct as written.
- **Renewal at half the duration works.** Renewals were issued at ~33 s and ~72 s with the hold
  remaining continuously effective; no lapse or gap was observed between them.
- **PV was not curtailed.** Solar held steady at 358–370 W across the whole run, before, during and
  after arming. Weak evidence at 360 W in the evening — this needs repeating under strong midday sun
  before the curtailment risk can be closed.

**A working hold still leaves a residual 50–65 W trickle out of the battery.** This is inverter
standby draw, not load being served — it persisted regardless of house load swinging between 143 W
and 2877 W. Two consequences:

- Issue #20's acceptance criterion "`BatteryPowerWatts` is never negative" is **not literally
  achievable** on this hardware. The achievable guarantee is that the battery stops serving house
  load, which is what the feature is actually for.
- The "hold armed but battery discharging" warning originally triggered on any negative value, so it
  fired every single poll and drowned out the signal it existed to give. It now uses a 150 W deadband.

**Still to observe:** behaviour under strong PV (does the battery still charge from surplus while
held, and is PV curtailed at full output), behaviour with the EV actually charging, and what the
undocumented `timeout` field does relative to `duration`.

`BatteryHold:Enabled` remains off by default and `DryRun` still defaults to `true`, since none of the
above has been observed on any other firmware.
