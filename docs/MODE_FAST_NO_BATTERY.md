# Fast charge without the battery (the `FastNoBattery` mode)

The "I leave in an hour" button. Where `Solar` and `Forecasted` ration the car to what the sun can
spare, this mode does the opposite: **charge as fast as the installation allows, and keep the home
battery out of it.** While it is selected:

1. The **battery discharge hold is armed** — the pack does not serve the car (see
   [Battery discharge hold](BATTERY_HOLD.md) for the mechanism). You
   can switch it off while the charge runs; see [The hold, and who owns it](#the-hold-and-who-owns-it).
2. The charger is pinned at **`MaxChargingCurrentAmps`** — written by the start action itself, before
   the first poll, and then re-commanded every cycle whatever the sun, the SOC, the forecast or the
   time of day. PV covers what it can and the **grid covers the rest**.

   Writing it at the press rather than leaving it to the poll loop closes a real gap: a finished
   charge ends by writing `PauseCurrentAmps` (0 A here), so without this the next fast charge spends
   up to a poll interval sitting in `Fast` with the car told to take nothing. A charger already at
   the maximum is left alone — the setpoint write is skipped when it would not move anything — and a
   setpoint write that fails does **not** fail the start, because the control loop commands the same
   current seconds later.
3. When the charge is over — the **amount you asked for** has been delivered, or the car stopped at
   **its own** charge limit — the setpoint drops to `PauseCurrentAmps`, the charger is written `Stop`,
   the mode returns itself to **`Off`**, and the hold is released — exactly the end state the **Off**
   button produces, in the one cycle.

Point 3 is what makes it safe to press. The state it creates is expensive — maximum current, grid
import, battery locked — and it ends by itself instead of sitting armed until somebody notices.

## How much? (the amount)

By default the **car** decides when it has had enough, which is what this mode did for its whole life
before #119. You can instead say how much to deliver, in either of the two ways the targeted tab
already asks — and the answer is a **stopping condition and nothing else**:

| Basis | What you say | When it ends |
|---|---|---|
| **Until the car stops** *(default)* | nothing | the car reaches its own charge limit and stops drawing |
| **Energy to add** | `20` kWh | 20 kWh has been delivered, measured at the charger |
| **Battery target** | `60` % | the same, against kilowatt-hours converted from the gap |

- **It does not pace the charge.** The setpoint is at the maximum from the first cycle to the last;
  the amount only decides when to stop. Anything that waits for cheaper sun, or plans around a
  departure, is [`Targeted`](MODE_TARGETED.md) — that is the whole difference
  between the two modes, and the reason both exist.
- **A battery target is converted once**, when you press the button, using
  `Ev:Vehicles:0:BatteryCapacityKWh` and `Ev:Vehicles:0:ChargeEfficiency`. A later reading does not
  move a charge that is already part delivered — the same rule, and the same reasoning, as
  [a targeted SOC request](MODE_TARGETED.md#setting-a-target). It is offered only where it can be honoured:
  a configured capacity **and** a reading from the car — and, since
  [#179](https://github.com/mpospisil/gleanvolt/issues/179), one no older than `Vehicle:MaxAge`. A
  reading past it is refused with its age in the sentence rather than converted. Ask in
  kilowatt-hours, or press `Full`.
- **Delivery is metered from the moment you start**, not from when the car was plugged in. Energy the
  car took under an earlier mode does not count towards this amount.
- **Whichever comes first wins.** A car that reaches its own limit at 12 kWh of the 20 asked for ends
  the mode there and says how far short it got.
- **It does not survive a restart**, like the mode itself and the targeted request.

## When? (the departure)

By default the charge starts the moment you press the button. Give it a **departure** and it starts as
late as it can while still finishing in time:

```
start no later than  =  departure − safety margin − (energy still needed ÷ charging power)
```

30 kWh at 11 kW, wanted by 07:00, with the standard 15-minute margin: the charge waits until about
**04:02**, then runs flat out. Until then the charger sits in `Fast` at `PauseCurrentAmps` and the
controller says what it is waiting for.

**Why you would want this: the pack.** A lithium cell ages faster the longer it is held at a high state
of charge, so the 20 points above 80% are worth buying as late as possible. The charge itself takes two
or three hours; it is the nine hours of *sitting* at 95% that does the damage. Pressing the button at
22:00 for an 07:00 departure turns those nine hours into about twenty minutes.

- **It changes *when*, never *how*.** When it runs, it runs at `MaxChargingCurrentAmps` exactly as it
  always did. Nothing here paces the charge, waits for cheaper electricity, or consults a forecast —
  that is [`Targeted`](MODE_TARGETED.md), and the difference is the whole reason
  both exist.
- **It needs an amount.** With **Until the car stops** there is no duration to work back from and so
  no such thing as the latest moment it could start; the combination is refused in words rather than
  quietly charging at once.
- **The schedule is rebuilt every poll**, so the start time moves *later* as energy goes in — and
  *earlier* the moment the car turns out to draw less than the charger offers. An 11 kW wallbox in
  front of a car with a 7.4 kW on-board charger is otherwise a plan an hour short of the time it
  needs, and this is the mode with no slack in it.
- **Before the car draws anything, the plan is a guess.** The car's on-board limit is not knowable
  until it charges, so the first schedule uses the installation's maximum and says so — *"the
  installation's maximum, not yet measured"*.
- **Not enough time?** It charges flat out from now and reports how far short it will fall, rather
  than pretending. The same honesty `Targeted` commits to.
- **Plugged in after the start time?** It begins at once. The plan is a "not before" gate, never a
  "not after" one.
- **The departure passing ends the mode**, reporting what was delivered — the same rule `Targeted`
  follows. A car that is not ready at the moment you said you were leaving has run out of the window
  it was given.

> ⚠️ **A deferred charge does not survive a restart.** Neither does the mode nor the amount, and the
> reasoning is the same throughout — but this is the sharpest form of it: a charge deferred to 04:02
> and lost to a 23:00 container restart is discovered at 07:00 with a car that never charged. If you
> restart the stack in the evening, set it again afterwards.

## The hold, and who owns it

A fast charge **arms the battery discharge hold when it starts charging**, and ending it — for *any*
reason: you pressed Off, the car finished, the amount was delivered, the car was unplugged —
**releases it**.

*When it starts charging*, not when the mode is selected: a charge deferred to 04:02 would otherwise
lock the pack out of serving the house from 22:00, all night, with nothing being charged at all.

Between those two moments the switch is yours: turn **Battery discharge hold** off in Home Assistant
or the web UI and the pack is allowed to help, while the car goes on charging at maximum. That is a
legitimate thing to want and it is your call.

> Before #119 it was not. The mode's hold was a floor OR-ed over your switch, so flipping it off
> during a fast charge moved the switch on screen and changed nothing at all.

Two consequences worth knowing:

- **A hold you had switched on before starting is off when the charge ends.** The mode releases the
  hold on the way out rather than remembering what the switch said an hour ago — the alternative
  leaves the pack locked out of serving the house with nothing charging.
- **Taking the charger out of `Fast` at the wallbox is not an ending.** The mode stays selected, the
  controller goes `Idle`, and the hold stays armed. Press **Off** if that is what you meant.

> ⚠️ **`MaxChargingCurrentAmps` is a supply limit in this mode, not a preference.** The other modes
> only reach it when the sun is that generous; this one sits there for hours. On the reference install
> 16 A × 230 V × 3 phases ≈ **11 kW** drawn continuously from PV and grid together. Set it to what
> your supply and main breaker actually allow.

## When is the car "finished"?

The charger reports the car's state, and what it reports at the end of a session is firmware-specific,
so the rule leans on power and treats the status as a corroborating signal:

- the car counts as **idle** while it draws no more than `CompletionPowerThresholdWatts` (200 W —
  well above standby, well below the 6 A floor), **or** while the charger reports `SuspendedEv` or
  `Finishing`, which is the car saying it is done even if it is still trickling;
- the session is **finished** once it has been idle continuously for `CompletionDwell` (2 min);
- but only if the car **has drawn power at least once** since it was plugged in. Without that, a car
  still negotiating — or waiting on its own departure timer — would end the mode seconds after it was
  selected;
- **unplugging ends it immediately**, on the same path.

`ChargePaused` and `SuspendedEvse` are deliberately *not* treated as "the car is done": those are the
charger's own doing, which is exactly what our pause write produces.

## FastNoBattery on low tariff only

With a [grid tariff](CONFIGURATION.md#grid-tariff-the-tariff-section) set up, **Low tariff only** — a checkbox on the
*Fast* tab, the **Fast low tariff only** switch in Home Assistant, or `PUT /charging/low-tariff-only` —
makes the mode wait through high tariff and charge flat out once low tariff starts. The boot value is
`ChargeControl:Fast:LowTariffOnly` (`false`); a runtime change doesn't survive a restart.

- **A wait is not a finished car.** The car is "finished" only while the mode is asking it to charge,
  so hours of no draw through high tariff end nothing, and when low tariff starts the car gets a fresh
  `CompletionDwell` to spin up in. A car that reaches its own limit in low tariff still ends the mode.
- **The charger is stood down** (use-mode `Stop`) for a wait of 15 minutes or more and paused for a
  shorter one, the rule every mode uses (see [How every mode waits and ends](CHARGE_CONTROL.md#how-every-mode-waits-and-ends)):
  this wallbox tolerates 0 A in Fast for minutes, not hours.
- **The discharge hold is released through the wait** and armed again at the low edge. Nothing is
  charging, and the hold also keeps the pack out of the house: armed through a high tariff, it would
  run the house on the grid at the expensive rate.
- **A car plugged in during high tariff draws for a moment.** The charger starts it on its own at
  plug-in (Fast, 16 A), and the next poll stops it.
- **A departure time doesn't know about the tariff.** The latest start is worked out as if the car could
  charge at any hour, so a wait for low tariff can leave a deferred charge short of time; the plan says
  so rather than hiding it.
- **With no tariff set up the option is ignored**, and the Overview and the tab say so.

## What FastNoBattery doesn't do

- **It doesn't survive a restart.** Like every mode, the service comes back in `Off` — a service that
  restarted mid-session and silently resumed drawing 11 kW from the grid is not a behaviour anyone
  wants unattended. The charger keeps its last setpoint until a mode is selected; the inverter's hold
  lapses within `BatteryHold:Duration`.
- **It doesn't change the charger's use-mode while it runs.** Starting it wrote `Fast` once and
  ending it writes `Stop`; in between, like every other mode, it only moves the current setpoint.
- **It doesn't act on a charger that isn't in `Fast`.** If the read-back says anything else — someone
  moved it at the wallbox, or the charger did not hold the write — every cycle logs
  *"Charger use-mode is X, not Fast; leaving it untouched"* and **no current is commanded**. That line
  in the log is the first thing to check when a fast charge appears to do nothing.

With `BatteryHold:Enabled` false the mode still charges at maximum current, and logs a warning once on
selection: it cannot keep the battery out of the charge, which is half of what it promises.

```jsonc
"ChargeControl": {
  "MaxChargingCurrentAmps": 16,          // the ceiling this mode pins the charger at
  "CompletionPowerThresholdWatts": 200,  // below this draw, the car counts as not charging
  "CompletionDwell": "00:02:00"          // idle this long -> finished; pause and return to Off
}
```

The amount and the departure are not configuration — they belong to one charge — so there is nothing
to set here for them. A battery target reads `Ev:Vehicles:0:BatteryCapacityKWh` and
`Ev:Vehicles:0:ChargeEfficiency`, the same two figures a targeted SOC request uses; a departure reads
`ChargeControl:Targeted:SafetyMargin` and `ChargeControl:Targeted:MaxHorizon`. Those two are
deliberately shared rather than duplicated: *"ready at 07:00 must not mean still charging at 07:00"*
is a fact about your morning, not about which strategy happens to be running, and two settings for it
would only ever drift apart.
