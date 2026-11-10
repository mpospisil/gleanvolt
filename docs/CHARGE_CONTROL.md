# EV charge control (writes to the charger)

When enabled, the worker drives the EV charger from **live solar surplus**, and only once the home battery is essentially full. Two things are written, by two different callers: the **charge-current setpoint**, by the control loop, on every cycle that calls for a change; and the charger's **use-mode**, once per action — `Fast` when a strategy is started, `Stop` when it is switched off (see "What is written, and by whom" below). It writes only current values that differ from what's already on the device and logs every change.

The current setpoint is always constrained to what the hardware accepts (**6–32 A**): the configured min/max are clamped into that range up-front, so the controller can never even target an illegal value, and the write path clamps again as a final guard.

## How the surplus is calculated

```
Surplus = Solar production − household consumption
```

where household consumption **excludes battery charging and EV charging** — so whatever the house isn't using is what the car may have. Charging from it therefore neither imports from the grid nor discharges the battery, and the car is free to outbid battery charging.

Household consumption is the "Other Loads" residual from the energy balance:

```
OtherLoads = PV + Grid − EV − Battery        (Grid +ve = importing, Battery +ve = charging)
Surplus    = Solar − OtherLoads
```

**This requires the grid meter, not the inverter's output.** `Grid` comes from **`FeedinPower` (`0x0046`, int32, low word first, positive = export)** — the CT/meter reading at the utility connection, the only register that sees the whole house. It lives inside the telemetry block already fetched, so it costs no extra round-trip.

> ⚠️ The per-phase registers `0x6C/0x70/0x74` (mapped as `GridPowerR/S/T`) are **not** the grid meter — they report the **inverter's AC output**. Verified live: they track `Solar − Battery` at ~94–96% (inverter efficiency), while `FeedinPower` simultaneously read a genuine 388 W export. Using them for household load produces nonsense (a 2.4 kW-of-sun reading once yielded a 13 kW "surplus"). They are kept in the map for reference only.

Worked example from a live run: Solar 2498 W, exporting 388 W, battery idle, EV idle →
`OtherLoads = 2498 − 388 = 2110 W`, so `Surplus = 2498 − 2110 = 388 W` — exactly the exported power.

## Smoothing: moving average and hysteresis

Raw solar generation is erratic, so the controller never reacts to instantaneous data. Two buffering strategies keep it stable:

**1. The 3-minute moving average.** Every poll, the surplus (`PV − Load`) is pushed into a rolling time window and the *average* drives every decision. A single 15-second dark cloud therefore can't interrupt a 3-hour charging session — only a sustained drop moves the average enough to matter. The window is `SurplusAverageWindow` (default `00:03:00`); samples older than it are evicted each poll.

**2. The 1-amp hysteresis threshold.** A Modbus write is only issued when the new target differs from the charger's active setpoint by at least `CurrentChangeThresholdAmps` (default 1 A ≈ 230 W per phase). If the car is charging at 10 A, no command is sent until the average calls for 11 A or 9 A. Raise the threshold to damp the charger further (e.g. 3 A means 10 A → 12 A is ignored, 10 A → 13 A is written).

These stack with the existing state hysteresis — the asymmetric start/stop thresholds on both the surplus and the battery SOC gate — so the charger is never nudged by noise.

You can watch all of it in the log; each control cycle prints the raw surplus, the average, the sample count, the charger's active setpoint, and the target:

```
Charge control: Mode=Fast Surplus=4180W Avg=3990W (12 samples) Setpoint=16A Action=Charge Target=17A. Live surplus 3990W -> charge at 17A.
```

and the telemetry line carries the full energy picture plus the charger's active current:

```
SOC=96% BatteryPower=-56W Solar=4180W Grid=-388W EvCharger=Charging EvMode=Fast EvCurrent=16A EvPower=3680W
```

## What is written, and by whom

Until #89 the controller wrote the current setpoint and nothing else: the owner was expected to have
left the charger in Fast by hand, and a mode selected over a charger sitting in Green did nothing at
all and said so only through **Control state** reading `Idle`. Charging is now started by an action
instead, and the action takes responsibility for the use-mode. The two writes stay separate:

- **The control loop writes the current setpoint (`0x628`) and only that**, from its
  `Surplus = PV − household load` calculation. It is still the minimum it can do.
- **An action writes the use-mode (`0x60D`), once.** Pressing a strategy button writes `Fast` and then
  selects the mode; pressing **Off** writes `Stop` and returns the mode to `Off`. Nothing else writes
  it, and nothing re-asserts it.
- **The loop still only acts when all three hold**: the SolaX device is reachable, its own use-mode
  reads **Fast**, and a strategy is running. If the charger is changed at the wallbox mid-session it
  drops out of Fast, every controller goes `Idle`, and **nothing writes `Fast` back** — the owner has
  the last word on their own hardware, and the controller does not fight them for it.

## The 6 A hard cutoff — pause by dropping the current

An EV won't accept a 2 A or 4 A charge — **6 A is the floor** (IEC 61851). So (on the *averaged* surplus):

| Surplus | Decision | Current written |
|---|---|---|
| ≥ 6 A equivalent | `Charge` | the computed current (whole amps, clamped to the min/max) |
| < 6 A equivalent | `Pause`, or a stand-down for a longer wait | `PauseCurrentAmps` (default **0 A**), or use-mode `Stop` |

If it simply left the charger at its 6 A minimum when the surplus dropped below it, the charger would **make up the shortfall from the grid** — exactly what solar-only charging avoids. So the pause drops the current to `PauseCurrentAmps`: **0 A**, which suspends the car the way Green mode does, without changing the mode or ending the session — charging resumes when surplus returns. A pause is only ever short: see [How every mode waits and ends](#how-every-mode-waits-and-ends). (SolaX documents the current register as 6–32 A; if your charger doesn't accept 0, set `PauseCurrentAmps` to a sub-6 A value the car refuses instead.)

The threshold is **phase-aware** — the 6 A floor is ~1.4 kW single-phase but ~4.2 kW three-phase (see `Phases`), so on a three-phase charger the cutoff triggers far earlier in watt terms. Hysteresis is asymmetric on purpose: charging continues down to exactly the 6 A floor, but only *restarts* once the surplus clears `6 A + ResumeHysteresisWatts`.

A **battery-SOC gate** with hysteresis fronts the whole thing: charging engages only at/above `BatteryFullSocPercent` (so the car never competes with charging the home battery) and, once charging, keeps going until SOC falls below `BatteryReleaseSocPercent` — the band stops the car's own draw from flapping the gate.

An aging or unbalanced pack may never read `BatteryFullSocPercent`: the BMS ends the charge when its highest cell reaches the limit, and its SOC estimate drifts until a real full charge corrects it. So a battery already inside the band (at/above `BatteryReleaseSocPercent`) that has taken less than `BatteryStallWatts` for `BatteryStallDwell`, with enough sun to start the car, **counts as full**. The log says so: *"Battery 93% has stopped taking charge; counted as full."* A healthy pack in the band is still charging, so this never fires for it. The real 100 %, which the BMS needs now and then to recalibrate, comes from the evening target in `Forecasted` and `Targeted`.

```jsonc
"ChargeControl": {
  "DryRun": false,              // when Enabled: log intended writes but don't write (validation)
  "NominalVoltage": 230,
  "Phases": 3,                  // 1 = single-phase, 3 = three-phase (e.g. X3-HAC)
  "MinChargingCurrentAmps": 6,
  "MaxChargingCurrentAmps": 16, // setpoint is clamped to this (see "vehicle limit" below)
  "CurrentStepAmps": 1,         // whole-amp granularity the charger accepts
  "PauseCurrentAmps": 0,        // current written to pause (0 = suspend like Green mode)
  "SurplusAverageWindow": "00:03:00",  // rolling window the surplus is averaged over
  "CurrentChangeThresholdAmps": 1,     // min amp change before re-commanding the charger
  "ResumeHysteresisWatts": 200, // extra surplus needed to (re)start, to avoid flapping
  "BatteryFullSocPercent": 95,  // SOC at/above which charging engages
  "BatteryReleaseSocPercent": 90, // SOC it must fall below to disengage
  "BatteryStallDwell": "00:10:00", // a battery in the band taking < BatteryStallWatts this long counts as full
  "BatteryStallWatts": 100
}
```

**The vehicle is usually the binding limit, not the charger.** Charging negotiates down to the *lowest shared capability*, so `MaxChargingCurrentAmps` should reflect whichever of the car and the wallbox is lower. For a **VW ID.4** (the reference setup here):

| Setup | Car's limit | Configure |
|---|---|---|
| Three-phase (X3-HAC 11/22 kW) | **16 A/phase → 11 kW** — the ID.4's onboard charger caps here even on a 22 kW/32 A wallbox | `Phases: 3`, `MaxChargingCurrentAmps: 16` |
| Single-phase (X1-HAC-7) | **32 A → 7.2 kW** — the ID.4 pulls the wallbox's full current | `Phases: 1`, `MaxChargingCurrentAmps: 32` |

Setting a max above what the car will accept isn't dangerous (it simply won't draw it), but it makes the surplus maths optimistic — the controller thinks it has more headroom than the car will use. The defaults above are the three-phase ID.4 values.

**Set `Phases` to match your charger.** The 6 A EVSE minimum is a *current* limit; its power floor depends on phase count — ~1.4 kW single-phase vs **~4.2 kW three-phase** — and the watts↔amps setpoint uses `watts / (NominalVoltage × Phases)`. A three-phase charger left at `Phases: 1` would start on a ~1.4 kW surplus while the car pulls ~4.2 kW, importing the difference from the grid.

The current setpoint is encoded to the SolaX hardware's requirements automatically: rounded to a whole amp, clamped to `0…32 A` (0 for pause), and written with the register's **0.01 A scale** (value = amps × 100).

**Validate first with `DryRun`.** Set `DryRun: true` to run the full control loop and log exactly what it *would* write, without touching the charger — both writes, in the same shape:

```
[DRY RUN] would set charger use-mode: Fast (register 1). Solar started by Web UI.
[DRY RUN] would set charger current setpoint: 6A -> 16A (register 1600). Charge at 16A from 3700W surplus.
```

This is the safe way to confirm the register values against your device before allowing real writes. In dry-run the values that were "written" also stand in for the hardware on the next read, so a dry run behaves like a real one rather than reporting every mode `Idle` because the charger is still in Green.

There is deliberately **no `ChargeControl:Enabled`**: the service boots in mode `Off` and writes nothing at all until somebody presses a button, which is a stronger guarantee than a config flag and one nobody can leave switched on by accident.

In dry-run, **nothing is ever written to a SolaX device**. That's enforced twice: each write site is skipped, and the Modbus clients are wrapped in a read-only decorator that drops writes outright, so even a caller that forgot its guard cannot reach the hardware. A suppressed write logs a warning as a tripwire — it should never appear.

> ⚠️ **This feature writes to your charger — two registers.** The charge-current setpoint (`ChargeCurrentSetpoint 0x628`, written by the control loop) and the use-mode (`ChargerUseMode 0x60D`, **written** by a start/stop action: `0=Stop, 1=Fast`). Both come from the SolaX X1/X3-HAC protocol / the wills106 register map, but **GEN1/GEN2 and firmware differences exist** (GEN1 uses Datahub Charge Current `0x624`). `0x60D` in particular was read-only in this project until #89, so its write path has less mileage on it than the setpoint's. Also confirm your charger accepts `PauseCurrentAmps` (0 A by default). **Run with `DryRun: true` and check the logged register values against your hardware first.** Nothing is written until a button is pressed, which is why there is no enable flag to forget.

## How every mode waits and ends

Every mode talks to the same wallbox, so how a mode **waits** and how it **ends** are shared rules
([#280](https://github.com/mpospisil/gleanvolt/issues/280)). How much current each mode asks for is
not: that is what makes them different.

**Waiting.** A mode decides only "not now", and until when if it knows: the next sun in the forecast,
the next tariff edge, the end of a restart dwell. Then:

- **Pause** (0 A in Fast) when the wait ends in under **15 minutes**.
- **Stand down** (use-mode `Stop`) when it is 15 minutes or more away, or nobody knows.
- **Stand down** a pause that has already lasted **5 minutes**.

The reason is the wallbox: held in Fast at 0 A it ends the session itself after about seven minutes,
reports `Finishing` and switches to `Stop`, and the mode then loses its charger. A stood-down charger
stays in `Stop` until the mode wants to charge, which switches it back to `Fast`. A running charge
still finishes its minimum run time at 6 A before it waits. The Overview says "stood the charger down
until 09:40", and the session records a **Charger stood down** and a **Charger switched back to Fast**
event. A fast charge's deferred start and a targeted charge's held tail were already stood down, and
are kept as appointments: re-arming out of a wait for sun does not cancel a held tail later that day.

**A full car.** On a full car this wallbox reads `Charging / Fast` at full power on one poll and
`Finishing / Stop / 0 W` on the next, with no taper. A mode that was asking the car to charge, with the
car drawing on the poll before, ends at once as `SessionComplete`. The wallbox giving up on our own
pause reads the same, but comes after a pause with nothing drawing, so it is not mistaken for a full car.
A car that stays in Fast and draws nothing for `CompletionDwell` (2 min) while being asked to charge
also counts as finished, and an unplugged car ends the mode as `CarUnplugged`. Every mode applies these,
`Solar` and `Forecasted` included. The energy asked for and the departure are checked first.

**The end of the day.** `Solar`, `Forecasted` and `SolarGrid` end themselves as `NoSunLeftToday` once
the forecast has nothing left today that clears what they can charge on: the charger's 6 A floor for
`Solar`, the least a battery loan will bridge for `Forecasted` (the floor with the loan off), your
minimum for `SolarGrid`. Without a usable forecast they stay on, stood down, until you switch them off.
`Forecasted` with [**Until full**](MODE_FORECASTED.md#until-full) on stands the charger down for the night instead, and
carries on the next morning.
