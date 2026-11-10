# EV charge control (writes to the charger)

Every charge mode drives the charger the same way, through two writes:

- **The charging current**, set by the control loop every poll in which it should change, and only
  when it differs from the charger's current setpoint by at least `CurrentChangeThresholdAmps`.
- **Start and stop**, written once per action: starting a mode switches the charger to `Fast`; **Off**
  switches it to `Stop`. Nothing else writes it, and nothing writes it back.

The loop acts only while the charger is reachable, set to `Fast`, and a mode is running. If someone
changes the charger at the wallbox mid-session, the mode goes `Idle` and leaves it alone: the owner has
the last word.

**Nothing is written until a button is pressed.** The service always starts with charging **off**, so
there is no `ChargeControl:Enabled` to forget. With `ChargeControl:DryRun` set, the controller decides
and logs what it would write, without writing.

## The surplus

```
Surplus = Solar − house load
```

House load counts neither the home battery charging nor the car, so the surplus is exactly the power
the car can take without importing from the grid or discharging the battery. It is worked out from the
grid meter — the meter at the grid connection, which sees the whole house:

```
House load = Solar + Grid − Car − Battery        (Grid + = importing, Battery + = charging)
```

A grid meter that misses a phase gets this wrong; see
[`UnmeteredGridPhase`](CONFIGURATION.md#the-pv-system-the-pv-section).

## Smoothing

The controller never reacts to a single reading:

- **A 3-minute average.** Every decision uses the surplus averaged over `SurplusAverageWindow`
  (default 3 minutes), so a passing cloud can't interrupt a charge.
- **A 1 A threshold.** The current is changed only when the new value differs by at least
  `CurrentChangeThresholdAmps` (default 1 A, about 230 W per phase). Raise it to change the current
  less often.
- **Asymmetric start and stop.** A charge continues down to the 6 A floor, but only restarts once the
  surplus clears the floor plus `ResumeHysteresisWatts`.

## The 6 A floor, and pausing

A car won't charge below **6 A** (IEC 61851) — about **1.4 kW** on one phase and **4.2 kW** on three.
Left at 6 A when the surplus falls below that, the charger would make up the difference from the grid.
So:

| Average surplus | Decision | Current |
|---|---|---|
| enough for 6 A or more | `Charge` | the computed current, in whole amps, between the min and max |
| less than 6 A | `Pause`, or stand down for a longer wait | `PauseCurrentAmps` (0 A), or the charger switched to `Stop` |

A pause suspends the car without ending the session, and is always short; see
[How every mode waits and ends](#how-every-mode-waits-and-ends). If your charger refuses 0 A, set
`PauseCurrentAmps` to a value below 6 A that the car won't charge at.

**The `Solar` mode waits for a full home battery.** It starts only at `BatteryFullSocPercent` (95%) or
above and continues until the battery falls below `BatteryReleaseSocPercent` (90%), so the car never
competes with the home battery. An ageing pack may never show 95%: a battery inside the band that has
taken less than `BatteryStallWatts` for `BatteryStallDwell` counts as full, and the log says so
(*"Battery 93% has stopped taking charge; counted as full."*).

## Settings

```jsonc
"ChargeControl": {
  "DryRun": false,              // decide and log the writes, but don't make them
  "NominalVoltage": 230,
  "Phases": 3,                  // 1 = single-phase, 3 = three-phase
  "MinChargingCurrentAmps": 6,
  "MaxChargingCurrentAmps": 16, // the most the charger is set to -- see below
  "CurrentStepAmps": 1,         // whole-amp steps
  "PauseCurrentAmps": 0,        // the current written to pause
  "SurplusAverageWindow": "00:03:00",  // the surplus is averaged over this
  "CurrentChangeThresholdAmps": 1,     // the smallest change worth writing
  "ResumeHysteresisWatts": 200, // extra surplus needed to restart
  "BatteryFullSocPercent": 95,  // Solar starts at or above this
  "BatteryReleaseSocPercent": 90, // ...and stops below this
  "BatteryStallDwell": "00:10:00", // a battery in the band taking < BatteryStallWatts this long counts as full
  "BatteryStallWatts": 100
}
```

**Set `Phases` to match your charger.** Amps are converted to watts as
`amps × NominalVoltage × Phases`. A three-phase charger left at `Phases: 1` would start on a 1.4 kW
surplus while the car draws 4.2 kW, importing the difference. If the car charges on fewer phases than
the charger has, say so on the car: see [Phases and the two currents](VEHICLE.md#phases-and-the-two-currents).

**Set `MaxChargingCurrentAmps` to the lower of the car's and the charger's limit.** A higher value is
harmless — the car just won't draw it — but the controller then thinks it has more headroom than it
has. For a VW ID.4:

| Setup | Car's limit | Configure |
|---|---|---|
| Three-phase (11 or 22 kW wallbox) | **16 A per phase → 11 kW**, even on a 22 kW wallbox | `Phases: 3`, `MaxChargingCurrentAmps: 16` |
| Single-phase (7 kW wallbox) | **32 A → 7.2 kW** | `Phases: 1`, `MaxChargingCurrentAmps: 32` |

The charger's own range also applies: the limits are checked against the selected charger's driver at
startup and on save.

> ⚠️ **This writes to your charger.** Before charging for real, check that the readings on the Overview
> match the charger's own display, and that your charger accepts `PauseCurrentAmps`. If in doubt, run a
> while with `DryRun` on and compare the logged writes with what the charger shows. Which models are
> known to work, and their caveats, is in [Supported EV chargers](SUPPORTED_CHARGERS.md).

## How every mode waits and ends

How a mode **waits** and how it **ends** are rules shared by every mode. How much current each mode
asks for is what makes them different.

**Waiting.** A mode decides only "not now", and until when if it knows: the next sun in the forecast,
the next tariff edge, the end of a restart dwell. Then:

- **Pause** (0 A in `Fast`) when the wait ends in under **15 minutes**.
- **Stand down** (the charger switched to `Stop`) when it is 15 minutes or more away, or nobody knows.
- **Stand down** a pause that has already lasted **5 minutes**.

The reason is the wallbox: held in `Fast` at 0 A it ends the session itself after about seven minutes,
and the mode then loses its charger. A stood-down charger stays in `Stop` until the mode wants to
charge, which switches it back to `Fast`. A running charge still finishes its minimum run time at 6 A
before it waits. The Overview says "stood the charger down until 09:40", and the session records a
**Charger stood down** and a **Charger switched back to Fast** event.

**A full car.** A mode ends as `SessionComplete` when the car stops drawing while being asked to
charge: at once, if the charger closes the session straight after the car drew, or after
`CompletionDwell` (2 minutes) of no draw. Unplugging ends it as `CarUnplugged`. Every mode applies
these, `Solar` and `Forecasted` included; an amount asked for and a departure are checked first.

**The end of the day.** `Solar`, `Forecasted` and `SolarGrid` end themselves as `NoSunLeftToday` once
the forecast has nothing left today that clears what they can charge on: the 6 A floor for `Solar`,
the least a battery loan will bridge for `Forecasted` (the floor with the loan off), your minimum for
`SolarGrid`. Without a usable forecast they stay on, stood down, until you switch them off.
`Forecasted` with [**Until full**](MODE_FORECASTED.md#until-full) on stands the charger down for the
night instead, and carries on the next morning.
