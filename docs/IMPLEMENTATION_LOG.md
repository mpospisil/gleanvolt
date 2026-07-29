# Implementation log

Reverse-chronological. Newest entry at the top.

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
