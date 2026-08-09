# Implementation log

Reverse-chronological. Newest entry at the top.

---

## 2026-08-08 — Fast charge without the battery: the `FastNoBattery` mode (issue #28)

A fourth charge mode, and the first one that turns *itself* off. While it is selected the battery
discharge hold is armed automatically, the charger is pinned at `MaxChargingCurrentAmps` regardless of
sun, SOC or forecast, and when the car reaches its own charge limit the setpoint drops to the pause
current and the mode returns to `Off` — releasing the hold it armed.

### The one contract change

A controller could previously only say Charge / Pause / None. It now has a third thing to express, so
`ChargingControlDecision` gained `SessionComplete` and `ChargingControlInput` gained the two facts a
strategy needs to decide it: `EvDrewPower` and `EvIdleFor`. Both are defaulted, so the existing
controllers and their tests were untouched. The reasoning behind the completion rule — power
authoritative, `SuspendedEv`/`Finishing` corroborating, `ChargePaused` deliberately excluded because
it is what our own pause write produces — is in [DECISIONS.md](DECISIONS.md).

Cross-cycle state stays in `ChargingControlCoordinator`, next to the session-energy and loan tracking
it already owned: since when the car has been drawing nothing, and whether it ever drew at all. Both
reset on plug-in and on `ReleaseControl`, so a newly selected mode can't inherit the previous one's
verdict that the car has already charged and end itself on its first idle poll.

`FastChargingController` itself is the smallest strategy in the codebase — no smoothing, no
hysteresis, no SOC gate, because none of those inputs can change a constant setpoint.

### Ending the mode, in the right order

In `SolaxPollingService` the completion is handled *between* the charge cycle and the hold
reconciliation:

```
RunCycleAsync  -> Pause written, SessionComplete: true
_mode.Set(Off) -> mode := Off for the rest of this iteration
ApplyBatteryHoldAsync(mode: Off) -> release written on the same poll
```

Putting the mode change after the hold reconciliation would have left the inverter held for one extra
poll. Home Assistant needs nothing new: `PublishStatusAsync` already republishes the select's retained
state from `_mode.Mode` every status tick, so a controller-initiated change reaches the UI on its own.

`AutoHold` was generalised from "the forecast mode at its SOC floor" to "whatever the selected mode
wants", with `FastNoBattery` wanting it unconditionally, and now logs its automatic release as well as
its arming. The owner's manual switch is still OR-ed on top and is never released by a mode.

With `BatteryHold:Enabled` false the mode still charges and warns once on selection rather than
refusing to run — a select option that silently does nothing would be the worse failure.

### Hardware quirks and open verification

- **Nothing new is written.** The mode uses the same two write paths that already existed: the
  charger's current setpoint and the inverter's power-control command.
- **`MaxChargingCurrentAmps` becomes a supply limit.** The solar modes only reach the ceiling when the
  sun is that generous; this one sits at it for hours. On the reference install that is 16 A × 230 V ×
  3 ≈ 11 kW drawn continuously from PV and grid. Documented in both the README and the options class.
- **End-of-charge status is unverified.** No completed session has been logged through this controller
  yet, which is why the rule leans on power rather than on the charger's status enum. First live
  session should be logged end to end and the DECISIONS entry amended if the transitions differ.

### Tests

`FastChargingControllerTests` (13) covers the use-mode precondition, the clamped ceiling, indifference
to SOC and surplus, and every branch of the completion rule. `FastNoBatteryModeTests` drives the real
`SolaxPollingService` loop over a scripted telemetry sequence — a fake reader parks after the last
scripted reading, so the assertions need no timing assumptions — and checks the hold is armed with no
forecast at all, that a finished car pauses the charger, returns the mode to `Off` and releases the
hold in the same cycle, and that a hold the owner asked for survives all of it.

### Files changed

- `src/Solax.Core/Enums/ChargeControlMode.cs`, `EvChargerStatusExtensions.cs` (`IsChargeWindingDown`)
- `src/Solax.Core/Models/ChargingControlDecision.cs` (`SessionComplete`, `EvDrewPower`, `EvIdleFor`)
- `src/Solax.Core/Strategies/FastChargingController.cs` (new)
- `src/Solax.Worker/ChargingControlCoordinator.cs`, `ChargeControlStatusHolder.cs`,
  `SolaxPollingService.cs`, `Program.cs`
- `src/Solax.Worker/Configuration/ChargeControlOptions.cs`, `appsettings.json`
- `tests/Solax.Core.Tests/Strategies/FastChargingControllerTests.cs` (new),
  `tests/Solax.Core.Tests/Enums/EvChargerStatusExtensionsTests.cs`
- `tests/Solax.Worker.Tests/FastNoBatteryModeTests.cs` (new),
  `ChargingControlCoordinatorTests.cs`, `HaDiscoveryTests.cs`
- `README.md`, `docs/DECISIONS.md`

---

## 2026-08-08 — Five days of observation: #24 verified on hardware, forecast bias shape (issues #22, #24)

Read-only run 2026-08-02 09:26 → 2026-08-06 18:40, ~46,000 polls, charge mode `Off` throughout.
No code changes — this entry records what the run showed.

### The #24 reconnect fix is verified on hardware

The overnight run the #24 entry below asked for, four times over.

| Day | Polls | Failures | Transaction-ID | Rate | Worst gap |
|---|---|---|---|---|---|
| Aug 3 | 12,329 | 57 | 41 | 0.46 % | 52 s |
| Aug 4 | 11,995 | 92 | 76 | 0.76 % | 42 s |
| Aug 5 | 12,116 | 96 | 80 | 0.79 % | 43 s |
| Aug 6 (to 18:40) | 9,658 | 57 | 44 | 0.59 % | 43 s |

**Nothing ever got stuck.** Zero gaps over 60 s between successful polls across four full days, and
the failure-run distribution is almost entirely single polls:

```
Aug 3: {1 poll: 50, 2: 2, 3: 1}   Aug 5: {1 poll: 94, 2: 1}
Aug 4: {1 poll: 90, 2: 1}         Aug 6: {1 poll: 55, 2: 1}
```

Worst case in four days was three consecutive failed polls. Compare the pre-fix cliff: 43 successes,
564 errors, and zero successes for the last 50 minutes of the log. Traced end to end, one failure
costs exactly one poll:

```
16:14:24 [WRN] Failed to poll — Response was not of expected transaction ID. Expected 905, received 904.
16:14:31 [INF] SOC=99% BatteryPower=0W Solar=2093W ...
```

**But the underlying desync rate is climbing**: 0 (Jul 31) → 15 (Aug 1) → 41 → 76 → 80 → 44 per day.
The fix absorbs it at one poll each, so nothing is broken, but it is *masking* a trend rather than
addressing it. The cause is on the device or network side, not in this code. Worth its own issue if
it keeps rising.

### Forecast accuracy: totals are good, the intraday shape is not

Note the `Solar: Actual/Forecast` log line compares against **P50** (`EstimatedPowerWatts`), while the
planner uses **P10** — these numbers are median-vs-actual.

Daily energy on the three clear days: **+8.3 %, +6.5 %, +4.2 %** actual over forecast. Aug 6 (cloudy,
partial day) came in **−17.7 %**. Magnitudes are fine and err on the safe side.

The shape does not hold up. Hourly actual/forecast on clear days:

```
hour    07     08     09     10     11     12     13     14     15     16
Aug 3  0.67   0.88   0.99   1.07   1.06   1.06   1.19   1.10   1.29   1.14
Aug 4  0.68   0.86   1.01   1.09   1.11   1.12   1.12   1.12   1.11   1.10
Aug 5  0.68   0.84   0.97   1.06   1.10   1.14   1.12   1.12   1.09   1.18
```

**The 07:00 hour over-predicts by ~32 %, three days running, to within one percentage point** —
forecast ~1500 W against ~1000 W delivered. Midday is the opposite, a steady 6–14 % under-prediction.
A deficit that recurs at the same hour with the same magnitude on consecutive clear days is not
weather; it looks like morning shading the Solcast site model doesn't know about, or a wrong
azimuth/tilt in the site configuration.

**The single scalar bias cannot represent this.** The tracker whipsaws within each day — 1.00 at
start, down to 0.71–0.79 through the morning, back to 1.04–1.08 by evening — because no one number
is simultaneously +12 % and −32 %. Whichever value it holds is wrong for half the day, and applying
the evening value at 07:00 would make the over-prediction worse.

This lands where `SolarDayPlanner` is most sensitive: 1000–1500 W at 07:00 sits on the
shoulder/plateau boundary (surplus below vs above the charger's minimum power), so an optimistic
morning forecast is exactly what would open a charge window that cannot be sustained.

**Not yet a code change.** Check the Solcast site configuration first — azimuth, tilt, declared
capacity, horizon/shading if the plan offers it. If the site model is wrong, correcting it at source
beats compensating for it here. Only if the configuration is already right does per-period bias
(instead of one daily scalar) become the fix, and three clear days is thin evidence for that.

### Still not exercised

Across every log to date the charge mode never left `Off`, the battery hold was never armed, and
`ForecastedChargingController` has never run a cycle. The planner and accuracy tracker have been
validated read-only; **no feature that writes to hardware has been observed in a live run.** Every
outstanding verification item on #20 and #22 remains open for that reason.

---

## 2026-08-02 — Validation fixes after three days of observation (issue #22)

Branch: `feature/22-forecasted-charging`

Three days running in `Off` mode, ~34,000 polls. The service was stable — the #24 reconnect fix held
(22 transaction-ID errors, every one followed by successful polls) — and the forecast tracker proved
itself: Solcast p50 tracked reality within 5% all day, cumulative 50.0 kWh forecast against 51.1 kWh
actual. **p10 runs 13–16% low on this roof**, which is the insurance premium `ForecastConfidence: P10`
charges.

Four defects fell out of the logs.

### A. The house baseline ran away (the serious one)

`HouseBaselineEstimator` was one EWMA with a ~1.4 h time constant. That is slow per minute and fast
per day: it followed the diurnal curve, so by mid-afternoon it reported the afternoon peak and the
planner projected that flat across every remaining hour. Measured: **264 W at 05:00 → 2124 W at 11:00
→ 5406 W at 15:00**. The consequence, on a day whose forecast was accurate to 5%:

- 05:00 — "Window 08:00–16:30, 33.6 kWh available for the car"
- 12:00 — "No EV charging today", Plateau 0.0 kWh, window none, SOC floor 100%

Replaced by `HouseLoadProfile`: 24 hour-of-day buckets, each a slow EWMA (~3 days per bucket), seeded
from `BaselineHouseLoadWatts` until an hour has been observed. The planner now asks
`IHouseLoadProfile.ExpectedWattsAt(instant)` per forecast slice instead of taking one figure for the
whole day — which is the question it was always really asking.

### B. `ForecastToday` in the day summary was nonsense

`ForecastToday=6.6kWh ActualToday=52.7kWh (702%)`. Solcast returns only *future* periods, so by the
19:00 deadline "today's forecast" holds nothing but the evening. Both figures now come from the
accuracy tracker, which integrates them live period by period — the same numbers that were already
correct in the `Forecast check` lines.

### C. The day-plan line logged every poll

13,475 Information lines a day, 5–8 MB of log. The change-detection signature carried the budget to
0.1 kWh, which drifts continuously. Signature coarsened to whole kWh and whole percent, plus a
five-minute floor between lines — bypassed when the outlook changes or the plan becomes
usable/unusable, so the first real plan after startup is never held back. Measured after the fix: one
line per 90 s smoke run instead of eighteen.

### D. The outlook chattered

`Shortfall → Tight → Shortfall → Tight` inside three minutes, because the Tight/Shortfall boundary is
half the EV target and the day sat exactly on it. `Classify` now takes the previous outlook and
applies `OutlookHysteresisFraction` (0.05), so leaving a state needs the margin and entering it does
not.

### Also

- The forecast refresh now wakes 30 minutes *before* first light, so the day no longer starts on a
  stale forecast and the live-solar fallback (observed as an `Unknown` plan from 02:45 to 04:45).
- A once-daily `House profile:` line dumps the learned hourly shape, since that is the first thing to
  read when a day's decisions look wrong.

### Still open

**What draws ~5 kW at midday on the reference site?** The battery is full by 10:00 every day, and from
then on `OtherLoads` is ~91% of PV (r=0.69) against a 300 W night base and 47 kWh/day total. That
shape — load that appears exactly when surplus does — suggests a PV-surplus diverter. If so it is
self-fulfilling: the plan reads it as fixed house load, stands down, and leaves it the surplus it was
measuring. **This affects `Solar` mode identically** (its surplus was ~440 W at midday), so it is not
specific to the forecast strategy. Needs an answer from the site owner before the next measurement
round can be interpreted.

---

## 2026-07-29 — Modbus reconnect on failure (issue #24)

Branch: `fix/24-modbus-reconnect`

A three-hour run produced 43 successful polls and 564 transaction-ID errors. Bucketed by ten minutes
it is not intermittent at all — it is a cliff:

| bucket | ok | errors |
|---|---|---|
| 18:50 | 4 | 0 |
| 19:00 | 39 | 10 |
| 19:10 | **0** | 91 |
| 19:20–20:00 | **0** | ~97 each |

### Cause

A late response left in the socket buffer puts the stream permanently one or more replies behind.
NModbus's own retries cannot escape it, and nothing tears the connection down — `IsConnected` is true
because the socket is fine. See [DECISIONS.md](DECISIONS.md).

### What changed

`ModbusTcpClient` rewritten around a single `ExecuteAsync` path: serialise on a `SemaphoreSlim`,
connect on demand, wait out `DeviceConfig.MinRequestInterval`, run the operation, and — on any failure
— invalidate the connection through an exception filter so the original exception still surfaces
unchanged. `DeviceConfig` gained `MinRequestInterval` (250 ms default).

### Things worth knowing

- **NModbus retries a mismatched response by re-sending the request**, so corrupting a single reply is
  self-healing and proves nothing. The tests had to model the *persistent* offset to reproduce the
  field failure — that discovery is baked into `FakeModbusTcpServer.Persistent`.
- **The tests need a real socket.** `FakeModbusTcpServer` implements enough MBAP framing for function
  codes 3, 4, 6 and 16, plus deliberate transaction-id corruption.
- **Verified as regression tests**: disabling only the invalidation makes 3 of the 9 fail; restoring it
  makes all 9 pass.
- **Not verified on hardware yet** — the fix wants an overnight run showing successful polls all the
  way to the end of the log. *(Done — see the 2026-08-08 entry above: four full days, ~46,000 polls,
  no gap over 60 s, worst failure run three polls.)*

### Files

`src/Solax.Infrastructure/Modbus/ModbusTcpClient.cs`, `src/Solax.Core/Models/DeviceConfig.cs`,
`tests/Solax.Infrastructure.Tests/{ModbusTcpClientTests,FakeModbusTcpServer}.cs`, docs.

---

## 2026-07-27 — Always boot in Off, with the battery free (issue #22 follow-up)

Branch: `feature/22-forecasted-charging`

The service used to seed its runtime state from configuration: `ChargeControl:Enabled` chose the boot
charge mode (`true` → Solar) and `BatteryHold:HoldAtStartup` could arm the discharge hold at startup.
Both are gone. The service now **always** starts with the charge mode `Off` and the hold `off`, and no
configuration key can change that.

### Why

A restart happens for reasons nobody chose — a crash, a power cut, a deploy — and in each case the
safe assumption is that the controller has no business acting until somebody asks it to. Seeding from
config inverted that: a machine rebooting at 3am would take control of the charger and, if
`HoldAtStartup` was set, immediately re-arm a hold that keeps the pack idle. The hold in particular is
a *command with a lifetime*, not a stored setting, so re-arming it on boot conflicts with the failsafe
that #20 was built around: stop renewing and the inverter returns to normal within `Duration`.

### Changed

- `Program.cs` seeds `ChargeControlModeSelector` with `Off` and `BatteryHoldSelector` with `false`,
  unconditionally.
- Removed `ChargeControl:Enabled` (it did nothing else) and `BatteryHold:HoldAtStartup`.
  `BatteryHold:Enabled` stays — it is a real master switch that decides whether the inverter's Modbus
  client is writable at all, and it supersedes the note in the #20 entry below about `HoldAtStartup`.
- Startup log lines now say what the state is *and* that the hardware is untouched until asked.
- README updated in four places; existing `.env` files carrying the removed keys are harmless (unbound
  configuration keys are ignored), but they no longer do anything.

---

## 2026-07-27 — Forecast-driven charge mode: `Forecasted` (issue #22)

Branch: `feature/22-forecasted-charging`

A third charge mode alongside `Off` and `Solar`, selectable at runtime from the Home Assistant select.
Where `Solar` waits for a 95 % battery and then follows the last three minutes of surplus, `Forecasted`
plans the whole remaining day from the Solcast forecast so the car can start hours earlier while the
home battery still reaches 100 % by a configured evening deadline.

### What was built

**`Solax.Core` (all pure, all unit-tested)**

- `SolarDayPlanner` — the heart of it. Slices the remaining forecast (prorating the period the plan is
  built inside), splits it into *shoulder* (surplus below the charger's minimum power) and *plateau*
  (at or above it), books the battery's need backwards from the deadline, and reports what is left as
  both an energy budget and a **deliverable** budget restricted to periods that clear the minimum
  power. Also produces the SOC floor, the next viable charge window, the shortfall and the outlook.
- `ForecastedChargingController` — decides the current from the plan. Hard stops (session ceiling,
  final guard, SOC floor, no window) bypass the dwell timers; soft reasons respect them and hold the
  session at 6 A rather than stopping inside `MinRunTime`. Grants the bounded battery loan. Delegates
  to `LiveSolarChargingController` whenever the plan is unusable.
- `ForecastAccuracyTracker` — accumulates today's actual against forecast energy per period, exposes
  the clamped bias, hands each closed period to the caller once for logging, and withdraws trust after
  a sustained breach.
- `EnergyIntegrator`, `HouseBaselineEstimator`, `SolarDayPlan`, `DayOutlook`, `ForecastConfidence`,
  `IForecastRuntimeSettings`, plus p10/p90 bands on `SolarForecastPeriod`.

**`Solax.Infrastructure`** — Solcast now parses `pv_estimate10`/`pv_estimate90` (only the median was
read before) and logs all three bands on refresh.

**`Solax.Worker`** — `DayPlanProvider` (baseline + accuracy + plan + all four log lines + day roll),
`ForecastRuntimeSettings` (the three HA-settable numbers), mode-keyed controller routing in
`ChargingControlCoordinator`, session/loan energy tracking, automatic arming of the #20 discharge hold
at the plan's SOC floor, daylight-only forecast refresh, and thirteen new HA sensors plus three number
entities.

### Things worth knowing

- **The SOC floor had to be redefined mid-implementation.** Deriving it from the energy *booked* for
  the battery made it equal the current SOC — a floor that forbids all discharge. It counts all
  remaining surplus instead; see [DECISIONS.md](DECISIONS.md).
- **`MaxLoanPowerWatts` defaults to 2500, not the 1500 the issue first proposed.** Bridging a typical
  2–3 kW surplus up to the ~4.2 kW three-phase floor needs ~2.2 kW; a 1.5 kW cap could never reach it,
  which would have made the loan silently useless.
- **The dwell timers change what a "pause" means.** Inside `MinRunTime` a soft pause holds the charger
  at 6 A instead of stopping. Five of the loan tests initially failed because of exactly this, which is
  the behaviour working as intended.
- **`ChargeControlStatus` and `ChargingControlInput` both grew.** The input gained defaulted
  parameters (plan, dwell, session energy, loaned energy) so the live-solar controller and its tests
  are untouched.
- **Nothing is persisted.** A restart loses today's totals and resets the bias to 1.0; it re-converges
  within a few forecast periods.

### Not done / open

- **Not verified against hardware.** No live day has run through this yet. `BatteryCapacityKWh` must
  be set to the real pack before the plan means anything, and the accuracy tracker should be left
  running read-only for a week (it works in every mode) to see whether p10 is systematically low for
  this roof.
- The undocumented interaction between an auto-armed hold and a manual one is resolved by OR-ing them
  (manual always wins), but has not been exercised live.
- Whether the pack curtails PV near 100 % SOC — the open question from #20 — still matters here: it
  would affect the trajectory's final approach.

### Files

`src/Solax.Core/{Enums,Models,Interfaces,Strategies}/*` (11 new, 4 changed),
`src/Solax.Infrastructure/Solcast/*` (2 changed),
`src/Solax.Worker/{Forecasting/*,Configuration/*,HomeAssistant/*,Program.cs,SolaxPollingService.cs,ChargingControlCoordinator.cs,SolarForecastRefreshWorker.cs,appsettings.json}`,
tests in all three test projects (48 new).

---

## 2026-07-27 — Battery hold verified on hardware; discharge deadband; grid-power sensor (issue #20)

Branch: `feature/20-battery-discharge-hold`

Follow-up to the entry below, which shipped the hold unverified. It has now been exercised against
the live inverter.

### What was found

The mechanism works. Arming the hold at dusk (PV ~360 W, SOC 87 %, no EV charging) moved the house
off the battery within a single poll: battery **−2846 W → −56 W**, grid **0 W → +1601 W**, solar
unchanged at ~370 W. Renewal at half the duration held it continuously with no observed lapse, and PV
was not curtailed. Full measurements are in [DECISIONS.md](DECISIONS.md).

### What changed

- **A 150 W deadband on the "hold armed but battery discharging" warning**
  (`SolaxPollingService.ResidualDischargeWatts`). A working hold still leaves a **50–65 W trickle**
  out of the battery — inverter standby draw, not load being served; it persisted while house load
  swung between 143 W and 2877 W. The warning originally fired on any negative value, so it fired
  every poll and drowned out the signal it existed to give.
- **Grid power exposed as a Home Assistant sensor** (`grid_w` in the state payload, `grid_power`
  discovery config), so the hold can be observed from HA rather than only from the log — watching
  import rise as battery discharge falls is the clearest evidence the hold is working.

### Consequences

- **Issue #20's "`BatteryPowerWatts` is never negative" acceptance criterion is not literally
  achievable** on this hardware, because of the standby trickle. The achievable guarantee is that the
  battery stops *serving house load*.
- Defaults are unchanged: `Enabled: false`, `DryRun: true`. Verification on one inverter and firmware
  says nothing about any other.

### Still unobserved

Behaviour under strong midday PV (does the battery still charge from surplus while held, and is PV
curtailed at full output), behaviour with the EV actually charging, and what the undocumented
`timeout` field does relative to `duration`.

134 unit tests pass.

---

## 2026-07-26 — Battery discharge hold (issue #20)

Branch: `feature/20-battery-discharge-hold`

A switch that stops the home battery discharging, so the EV charges from PV and grid while the
battery is still free to charge from PV surplus. Orthogonal to charge control: either can be on
without the other.

### What was built

**`Solax.Core`**

- `Enums/InverterControlRegister.cs` — the inverter's *holding* register space (a different address
  space from `InverterRegister`, which is input registers), carrying the Modbus Power Control block
  at `0x7C` and the "verify against your hardware" warning.
- `Enums/InverterPowerControlMode.cs`, `Enums/InverterPowerControlSetType.cs` — the device-level
  enums, with an explicit note on why there is no `No Discharge` value.
- `Strategies/BatteryDischargeHoldStrategy.cs` — pure, stateless computation of the `active_power`
  target: `-min(house load, PV)`.
- `Interfaces/IBatteryDischargeControl.cs`, `Interfaces/IBatteryHoldSelector.cs`,
  `Models/BatteryHoldState.cs`.
- `Models/EnergyState.HouseLoadPowerWatts` — total house load *including* the EV charger
  (`PV + Grid − Battery`), as distinct from the existing `OtherLoadsPowerWatts` residual, which
  excludes it. The hold needs the EV counted as load the grid may cover.

**`Solax.Infrastructure`**

- `RegisterMaps/PowerControlPayload.cs` — pure encoder for the 13-register block, 32-bit fields low
  word first.
- `BatteryDischargeControl.cs` — the write path on the keyed inverter client. Writes on arm, release,
  retarget beyond the threshold, and renewal; nothing in a steady state.

**`Solax.Worker`**

- `BatteryHoldSelector`, `Configuration/BatteryHoldOptions`, poll-loop reconciliation, and a Home
  Assistant `switch` plus **Battery power** and **Battery hold target** sensors.

### Architecture decisions

The central one — that the inverter has no `No Discharge` mode, so the hold is a computed
`Enabled Power Control` target rather than the fire-and-forget switch the issue described — is
recorded in [DECISIONS.md](DECISIONS.md) along with what it costs.

Two smaller ones worth noting here:

- **`BatteryHold:Enabled` is a real master switch, not a boot default.** `ChargeControl:Enabled`
  only seeds the mode, because Home Assistant can select `Solar` at runtime afterwards. That pattern
  can't hold here: issue #20 requires that `Enabled: false` performs no inverter writes *at all*, and
  a runtime-settable switch would break it. So while the flag is off the feature is entirely inert —
  no HA switch is published, the poll loop skips it, and the inverter's Modbus client is wrapped
  read-only. `HoldAtStartup` covers the boot value of the hold itself.
- **`WriteProofInDryRun` became `WriteProof`, taking an explicit `writable` flag.** It previously
  gated *both* device clients on `ChargeControl:DryRun`, which would have left the inverter writable
  whenever charge control was live — regardless of the battery-hold settings. Each device now derives
  writability from the feature that actually writes to it.

### Hardware quirks and edge cases

- **Holding register `0x7C` is overloaded**: written it is the power-control command, read it is the
  ARM firmware version. There is no read-back of the active command, so the HA switch reports what we
  last successfully wrote. A failed write therefore shows in HA as the switch returning to OFF rather
  than as an assumed success.
- **Nothing verified on hardware yet.** The register map comes from the upstream integration, not a
  SolaX document, and issue #20's Phase 0 observations are still outstanding. Hence
  `Enabled: false` + `DryRun: true` defaults. *(Superseded — see the 2026-07-27 entry above: the hold
  was subsequently verified on the reference inverter. The defaults stand regardless.)*
- **The compensating check for the missing read-back**: if the battery is discharging while we
  believe the hold is armed (and we are not in dry-run), the poll loop logs a warning. It is the only
  observable signal that the command isn't taking effect on this firmware. *(A 150 W deadband was
  added on 2026-07-27; a working hold still trickles 50–65 W.)*
- **Clock going backwards** (NTP step, telemetry timestamp jitter) is treated as "renewal due" rather
  than deferring renewal indefinitely.
- A failed write is not recorded as armed, so the next poll retries instead of reporting a hold that
  was never established.

### Verification performed

- 131 unit tests pass, covering the payload encoding field by field, arm/release/retarget/renew/
  no-change, duration clamping, dry-run, failed-write retry, the target strategy, the selector, and
  the HA discovery configs and state payload.
- Smoke-run against the live inverter with `Enabled: true` + `DryRun: true`, confirming the encoded
  block is logged (`registers [1,1,0,0,0,0,60,0,0,0,0,0,0] at 0x7C`) and that no write reaches the
  device — the `ReadOnlyModbusClient` tripwire warning never fires.
