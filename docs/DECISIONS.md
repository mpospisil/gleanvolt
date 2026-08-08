# Decision records

Append-only. A new record goes here whenever we adopt a library or establish a core pattern.

---

## 2026-08-08 — A mode may end itself, and "the car is finished" is decided on power

**Context.** Issue #28, the `FastNoBattery` mode. It creates the most expensive state this controller
can ask for — maximum current, grid import, the home battery locked out of the house — for a goal that
completes: the car reaches its own charge limit. Leaving that armed until somebody looks at Home
Assistant is the obvious failure mode, so the mode has to be able to end itself.

**Decision 1: a controller can say "this is over".** `ChargingControlDecision` gains
`SessionComplete`, carried up through `ChargeControlCycleResult`, and the poll loop answers by writing
the pause current and calling `IChargeControlModeSelector.Set(Off, …)`. The mode change is applied
*before* the battery hold is reconciled in the same cycle, so the inverter release goes out on the
same poll rather than the next one.

This is the first time control flows from a strategy back into the mode selector. The alternative —
a scheduler or timer outside the strategies — would have needed its own copy of "is the car still
drawing", which is exactly what the strategy already sees. The selector's existing contract does the
rest: `Set` logs and raises `Changed`, and the HA worker republishes the retained select state from
`_mode.Mode` on its next status tick, so the mode flipping under the owner needs no new plumbing.

**Decision 2: completion is a power judgement, corroborated by status.** The X1/X3-HAC's end-of-charge
status is firmware-specific and **has not been observed here yet** — the mode ships before a full
session has been logged through it. So the rule is built on the reading that cannot be misinterpreted:

- idle = draw at or below `CompletionPowerThresholdWatts` (200 W), *or* status `SuspendedEv` /
  `Finishing`, which is the car declaring itself done even while trickling for conditioning;
- finished = idle continuously for `CompletionDwell` (2 min);
- and only once the car has drawn power at least once this session, which is what separates "finished"
  from "hasn't started".

`ChargePaused` and `SuspendedEvse` are excluded on purpose: those are the *charger's* state, and our
own pause write produces them. Including them would let the controller read its own pause back as a
finished charge.

**Consequences.**

- The 200 W threshold sits in a wide gap — a charger's standby draw is tens of watts, its 6 A floor is
  1.4 kW single-phase and 4.1 kW on three — so no realistic reading is ambiguous.
- A car that pauses mid-session for longer than the dwell (thermal management, a utility signal) will
  be read as finished and the mode will end. Acceptable: ending returns control to the owner rather
  than doing anything to the car, and the owner reselects the mode.
- **Still to verify on hardware:** what the charger actually reports as the car finishes. Log a full
  completed session and, if the observed transitions contradict the rule above, amend it here.

---

## 2026-07-29 — A failed Modbus exchange invalidates the connection

**Context.** Issue #24: after roughly fifteen minutes of normal operation the service began failing
every single poll with `Response was not of expected transaction ID. Expected 2426, received 2424`,
and never recovered — 45 further minutes produced zero successful polls. Earlier logs show the same
failure in smaller doses going back a week.

**What was found.** A Modbus TCP response that arrives after its request has given up stays in the
socket's receive buffer. The next request reads *that* reply, and every request after it is
permanently one or more responses behind. NModbus retries a mismatch by re-sending, which heals a
one-off glitch — but not this, because every subsequent response is offset too, so the retries are
exhausted and the read throws.

The connection is not the problem. Throughout all of it the TCP socket is open and healthy, so
`ModbusTcpClient.IsConnected` returns true and the callers' `if (!IsConnected) ConnectAsync()` guard
never fires. The poll loop dutifully catches the exception, logs it, and retries on the same poisoned
stream, forever. Only restarting the process cleared it.

**Decision.** Any failed exchange invalidates the connection: the master and the `TcpClient` are
disposed and nulled, so the next call reconnects with a fresh stream and a fresh transaction counter.
This is done in an exception filter, so the original NModbus exception still reaches the caller and
the logs unchanged.

Three supporting changes:

- **Connect on demand.** Operations no longer throw "not connected"; they connect if needed. The
  callers' explicit guards still work but are no longer load-bearing.
- **One request at a time,** via a `SemaphoreSlim`. Requests are sequential today, but a reconnect can
  now happen mid-call, and two requests sharing a stream is another route to the same desync.
- **A minimum gap between requests** (`DeviceConfig.MinRequestInterval`). The SolaX protocol documents
  a second between instructions — a constraint noted in `InverterRegisterMap`'s own comment and then
  honoured nowhere. The poll loop issues about five requests per five-second cycle, four of them to
  the charger, which is where the failure was observed.

**Why 250 ms and not the documented second.** A full second across four charger requests would consume
most of a five-second poll. Recovery no longer depends on the spacing, so this value only affects how
often the device is pushed into a state that needs recovering; it is per-device configuration, and
raising it to `00:00:01` is the first thing to try if desyncs persist on other hardware.

**Consequences.**

- A transient glitch now costs one request instead of the process. The poll loop's existing
  catch-log-retry becomes sufficient rather than futile.
- Reconnection is invisible to callers, so a burst of failures shows up as a few warnings rather than
  an outage — worth remembering when reading logs: absence of errors no longer proves the link was
  never disturbed.
- Testing this needed a real socket. `FakeModbusTcpServer` speaks just enough MBAP to answer reads and
  writes and, on demand, to answer them with the wrong transaction id. Verified as a genuine
  regression test: with the invalidation disabled, three of the nine tests fail.

---

## 2026-07-27 — The forecast plans by power band, not by daily energy; the car absorbs any shortfall

**Context.** Issue #22 adds a third charge mode, `Forecasted`, driven by the Solcast forecast, with
one hard requirement: the home battery must be at 100 % by evening, while the car takes as much solar
as it can and neither pack is degraded unnecessarily.

**Decision 1 — plan in power bands, not in kilowatt-hours.** The obvious formulation,
`EvBudget = forecast − house − battery`, is wrong on this hardware: it treats energy as fungible when
the two consumers cannot accept the same power. The EV charger's floor is 6 A, which on three phases
(and the X1/X3-HAC has no phase switching) is **~4.2 kW**; the home battery accepts anything down to a
few hundred watts. So the day is split into *shoulder* production (below the floor — battery only) and
*plateau* production (at or above it — the only time the car can charge), and the battery's need is
booked against the shoulders first. Only what the shoulders cannot cover is claimed from the plateau.
A budget expressed purely in energy would happily promise the car 3 kWh on a day whose surplus never
once clears 4.2 kW.

**Decision 2 — book the battery backwards from the deadline.** The booking walks the remaining
forecast from `FullByTime` backwards, reserving the latest production first. That is what "100 % by
evening, not by lunchtime" means, and it hands the car the *earliest* feasible plateau — when it is
most likely to be plugged in, and when a forecast error still has the rest of the day to correct
itself. Recomputed every poll, so a collapsing afternoon simply grows the reservation next cycle.

**Decision 3 — the SOC floor counts all remaining surplus, not the booking.** An earlier draft derived
the floor from the energy booked for the battery. That is degenerate: the booking is sized to the need
*at the current SOC*, so the floor came out equal to the current SOC — "you may never discharge". The
floor answers a different question ("how far may SOC fall and still recover by the deadline?"), and a
deeper discharge simply grows a need the battery outranks the car to satisfy. It therefore counts
every remaining watt of surplus, clamped by `MinBatterySocFloorPercent`.

**Decision 4 — plan on p10, and measure the forecast against reality.** Planning a guarantee against
the median means missing it about half the time, so the plan uses Solcast's `pv_estimate10`
(`pv_estimate` was the only band parsed before). On top of that a realised bias (`actual ÷ forecast`
for elapsed daylight) scales the remaining forecast, clamped asymmetrically to `[0.5, 1.2]`:
under-production must be able to throttle the car, but a sunny morning must not be able to
over-commit the afternoon. A sustained breach of `[0.6, 1.4]` abandons the plan for the day. The
forecast refresh drops from 12 h to 3 h, skipped overnight — a 12-hour-old forecast cannot steer a
deadline, and a fresh one at 02:00 cannot change any decision.

**Decision 5 — the loan bridges a surplus; it never funds a session.** The battery may lend the
difference between a real surplus and the 6 A floor, repaid later from sun that would otherwise be
exported. It is refused below `MinBridgeSurplusWatts` (2 kW), on any shortfall day, once
`MaxDailyLoanKWh` is spent, and near the floor. Lending 4.2 kW into no sun would be a battery-to-car
transfer: a round trip and a cycle on both packs, buying nothing. Enforcement is not left to the
arithmetic — at the floor the #20 discharge hold is armed automatically, so the grid covers an
estimate error rather than the pack.

**Decision 6 — on a shortfall the car gives way, and we report rather than act.** Priority is fixed:
house → battery to 100 % → EV. Chosen deliberately over the alternatives (grid top-up to a daily
minimum; letting the battery finish below 100 %) because the owner's requirement is the evening 100 %,
and because grid-charging an EV is a decision worth making deliberately rather than automatically.
**No code path initiates grid charging.** What the controller owes the owner instead is early warning:
`Day outlook`, `Projected shortfall` and `EV energy expected today` are published as soon as the day
can be judged, so the decision — drive less, charge elsewhere, plug in on a night tariff — stays with
a person.

Consequences, deliberately accepted:

- **Two new stateful pieces in the worker** (`DayPlanProvider`, and the session/loan integrators in
  `ChargingControlCoordinator`). Nothing is persisted, so a restart loses today's accumulated
  energies and the bias resets to 1.0 — consistent with the rest of the service, and self-correcting
  within a few forecast periods.
- **The house baseline is a single rolling mean, not a per-hour profile.** A learned profile that
  resets on every deploy would be worse than an honest average.
- **The dwell timers can briefly import.** Holding a session at 6 A through a surplus dip for up to
  `MinRunTime` may pull from the grid; the alternative is contactor cycling and vehicle wake cycles on
  every passing cloud.
- **`Forecasted` degrades to `Solar`, never to something more permissive.** Missing forecast, stale
  forecast and broken trust all take the same path.

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
