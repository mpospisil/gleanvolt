# Targeted charging (the `Targeted` mode)

The other modes all ration the car to what the day happens to offer. None of them answers the
question an owner actually asks the night before a trip: **"I need 22 kWh in the car by 07:00 — do
that, and use as little grid as you can."**

`FastNoBattery` is the closest thing to it, and it is a blunt instrument even now that it can be given
an amount: it charges flat out from the moment it is selected, so a car plugged in at 21:00 for an
07:00 departure imports the whole lot overnight and ignores a sunny morning entirely. Its amount says
*when to stop*; this mode's says *when to start, and on whose energy*.

This mode takes an **energy in kWh** and a **departure time**, and plans backwards from the deadline.
The plan is rebuilt from a refreshed forecast and the **measured** delivery on every poll, and nothing
in it is ever committed to.

With [vehicle telemetry](VEHICLE.md#vehicle-telemetry-the-vehicle-section) configured you can state the target as
a **state of charge** instead — *"80% by seven"* — and the controller converts it to kilowatt-hours
once, when you start it. Energy is still what is promised and still what is metered; see
[Setting a target](#setting-a-target).

Everything turns on one comparison: what is still needed against `P_max × (departure − now)`, the
physical ceiling of the charger running flat out for every remaining minute.

**Not enough time** — the charger takes everything from now until departure, sun and grid together,
with the discharge hold armed so the pack stays out of it. The value of the plan here is honesty: it
reports how much will really reach the car, how far short of the request that is, and the departure
time that *would* have covered it.

**Enough time** — the car is **paced** across the whole window rather than run flat out.

```
pace = (energy still needed − sun forecast to reach the car) ÷ time left to the deadline
charge at  live surplus + pace,  clamped to the charger's 6–16 A range
```

Rate, not timing, is what decides the solar share, because **the charger can only use the sun that
shines while it runs**. At 16 A the car outruns the roof by several kW whatever the weather and then
stops: on 2026-08-22 a 13 kWh target on a day peaking at 8.5 kW was over in 87 minutes and took 9 kWh
from the grid. The same 13 kWh paced over a four-hour window is ~3.3 kW — a rate the roof matches for
much of the day.

Sun above the pace is taken in full: free energy, and it lowers the pace for every minute after it.
Sun below it is topped up from the grid. Falling behind raises the pace again on the next poll, so the
loop self-corrects in both directions and nothing is ever committed to.

A pace of **zero** means the forecast covers the target outright — then the car simply waits for the
sun and buys nothing. Activated at 02:00 for 15 kWh by 17:00 on a good forecast, nothing happens
overnight and charging starts at the first half-hour whose surplus clears the charger's 6 A minimum.

## Sun first, grid allowed — or sun only

Solar is the **preferred** source and the car takes as much of it as the window will give. Live
surplus is never refused for being unplanned: the plan schedules *imports*, not sunshine, so a
half-hour the forecast said nothing about — or underestimated — is charged from all the same. The
only thing that holds the car back from the sun is the plan's SOC floor, which is the home battery's
priority talking.

Grid energy is **allowed**, and there are two ways it arrives. The first is the planned block above.
The second is the **grid bridge**, which exists for one situation:

> On three phases the charger's 6 A floor is ~4.14 kW. A perfectly good 3.5 kW surplus therefore
> charges *nothing at all* — the car cannot sip it — so it is exported, and on a plan that already
> owes the grid, the same kilowatt-hours are bought back after dark.

The bridge runs the charger at its floor and buys the difference: 640 W now instead of 4.14 kW later,
with 3.5 kW of sun that would have been given away going into the car. It shrinks the planned grid
block watt for watt. It is the same trade the `Forecasted` mode makes with its
[battery loan](MODE_FORECASTED.md#the-battery-loan) — but funded from the grid, because this mode will not touch the
pack.

It never funds a session on its own. It is refused when the sun covers the whole target (no committed
import to bring forward, and the day was about to hand that energy over free), refused below the
plan's SOC floor, refused on a surplus under `ChargeControl:Forecast:MinBridgeSurplusWatts`, and sized
to reach the floor and not one watt past it. Set `ChargeControl:Targeted:GridBridge` to `false` to
keep imports strictly inside the blocks the plan drew.

**Sun only** turns the grid off for one request: a checkbox on the *Targeted* tab, the **Target sun
only** switch in Home Assistant, `maxGridEnergyWh: 0` through the API. The plan is then met from the
roof or not at all. Whatever the sun can't deliver by the departure is reported as **shortfall**, not
bought, and the preview shows that figure before you start. With nothing planned from the grid there
is no committed import for the bridge to bring forward, so it never arms. Expect close to no import
rather than exactly none: a cloud the 3-minute average hasn't caught up with can still draw a little
while the car runs at its floor, the same measurement limit `Forecasted` has.
**Just in time** is off under Sun only. Holding the last stretch back to the departure, with no grid
to make up a cloudy hour, can only lose sun. The web form greys it out, and Home Assistant charges a
sun-only `JustInTime` request as `Cheapest` and says so in the log. The API takes any grid budget in
kWh; the form and Home Assistant offer only the two ends.

## What it does and doesn't do

- **The home battery keeps its priority.** Its need is reserved out of the forecast by the same
  backward pass [`SolarDayPlanner`](MODE_FORECASTED.md#the-shoulders-belong-to-the-battery-the-plateau-belongs-to-the-car)
  uses, so the car is only ever offered what is left; and the discharge hold arms **whenever the car is
  drawing more than the roof is giving** — inside a grid block, while the grid bridge runs, or when a
  cloud leaves the car drawing more than `BatteryHold:BridgeShortfallWatts` (300 W) beyond the live
  surplus before the 3-minute average notices. Outside a block it stays armed for
  `BatteryHold:BridgeReleaseDwell` (3 min) after the last of those while the car charges, so it does not
  flap on the floor. Unlike `FastNoBattery`, the hold is scoped to the importing part of the cycle
  rather than to the whole mode. Below the plan's SOC floor the sun belongs to the
  pack outright: the car still gets its pace, but funded entirely by the grid, and the whole of it
  counts as imported so the hold arms for all of it.
- **There is still no battery loan in this mode.** The pack keeps priority and the grid is the honest
  source for the gap, bridge included.
- **Sub-minimum sun is still worth having.** A 3.2 kW half-hour cannot run the charger alone, but once
  the charger is held at its floor the roof supplies that 3.2 kW and only the ~0.9 kW difference is
  bought. Skipping it and buying the same energy after dark costs the whole 4.14 kW. The plan keeps two
  separate figures for this: what the sun could do **unaided** decides whether anything need be bought
  at all, and what it can do **assisted** decides how much.
- **No usable forecast is not a failure.** Every solar term goes to zero, the plan becomes grid-only,
  and the target is still met — live surplus is used opportunistically anyway. Where `Forecasted`
  degrades towards conservatism, this mode degrades towards *keeping the promise*.
- **Delivery is metered on measured charger power**, from the moment the request was activated. A car
  that limits itself to less than we asked for simply gets a longer grid block on the next poll, with
  no special case anywhere.
- **A safety margin** (`ChargeControl:Targeted:SafetyMargin`, 15 minutes by default) pulls the finish
  line in from the departure, so "ready at 07:00" doesn't mean "still charging at 07:00".
- **It returns itself to `Off`** when the target is met, when the departure passes, or when the car
  stops drawing at its own limit — the same arrangement `FastNoBattery` uses.

> ⚠️ **The request does not survive a restart.** Neither does the mode, and for the same reason: after
> a crash, a power cut or a deploy the charger is left exactly as its owner set it. But this is the
> one runtime setting whose loss is discovered at 05:00 with a flat car, so it is worth saying
> plainly. If the service restarts overnight, nothing will have told you, and nothing will be
> charging.

## Charging priority: cheapest, or just in time

By default a targeted charge is delivered **as cheaply as possible**: paced across the whole window,
taking every watt of sun above that pace, and finished whenever the sun and the pace happen to land it
— usually well before you leave. That is exactly right for a partial charge and wrong for a full one.
On a sunny day with an 07:00 departure the car hits 100% by mid-afternoon and then sits full all night,
which is the one thing every manufacturer tells you not to do to a lithium pack.

So the request carries a **priority**:

| | |
|---|---|
| **Cheapest** | The default, and what every request did before this existed. Nothing about the mode changes. |
| **Just in time** | Everything up to a **rest SOC** is delivered exactly as *Cheapest* would deliver it, on the sun, whenever the sun is there. The **last stretch above that** is held back and scheduled to land shortly before departure. |

**Only the last stretch.** Delaying the whole charge would forfeit a sunny day to protect the top of
the pack and then buy the lot from the grid at 04:00. The split is at the rest SOC (80% by default,
`ChargeControl:Targeted:JustInTime:RestSocPercent`, and settable per request):

```
100% |                                          .--  departure
     |                                        .-'
 80% +========================================'      <- rest SOC: reached on
     |      /  sun-driven, paced, any time               sun, then held here
 45% +-----'
     +--------------------------------------------------------------
      12:00          16:00          20:00        04:00   06:45
                                                  ^ release
```

The release point is `deadline - tail / max charge power - ReleaseSlack`, and that is the whole of the
arithmetic. **There is no taper model and no reading of the car's own charge limit**, deliberately: the
plan is rebuilt every poll from *measured* delivery, so a tail the car takes more slowly than the
charger's rated maximum simply raises its own pace on the next poll until it saturates at maximum
current, and **Target shortfall** reports the gap before departure if even that will not do it. And if
the car stops on its own limit — set to 80% in the car, say, against a 100% target — the controller's
completion path says so in the words it always has: *"car stopped drawing for 12 min at 12.4 kWh of
15.0 kWh — its own limit, short of the target"*. Predicting any of that in advance would add ways to be
wrong without adding a way to be right.

> **It is allowed to cost money, and it says so first.** While the tail waits, sun is genuinely turned
> down — taking a bright afternoon would put the car at its target by teatime, which is what the
> priority exists to prevent. So *Preview* runs the planner **both ways** and names the difference:
> *"Just in time buys about 4.2 kWh more from the grid than charging as cheaply as possible would."*
> That trade — a few kilowatt-hours against cycle life — is yours to make with the number in front of
> you. The comparison is done in the preview only; running two plans every poll to report a
> counterfactual is not worth the cycles on a Pi.

**The deadline still outranks the priority.** The hold is given up entirely — and the plan reverts to
an ordinary paced charge — when the release point has already passed, when there is nothing above the
rest point to hold, or when holding would leave the rest of the charge more than the shortened window
can deliver. The departure was the promise; the timing was only a preference.

**Offered only when it can be honoured.** The rest point is a state of charge, so it needs the car's
reported SOC *and* `Ev:Vehicles:0:BatteryCapacityKWh` — the same rule the **Battery target (%)** basis
follows. Without both, the control does not appear on the web tab, and a Home Assistant activation logs
a warning and charges with nothing held rather than silently pretending to hold something.

While the hold is in force the charger sits **visibly idle**, sometimes for hours. Both surfaces say so
in as many words — *"the charger is idle on purpose"* — because that state is otherwise the single most
convincing impersonation of a fault this controller can produce.

## Setting a target

From the web UI, the **Targeted** tab of **Charging plan**: what the car needs, the departure,
**Preview plan**, and then **Start charging**.

**The plan is shown before the charger moves.** *Preview* runs the same planner the poll loop runs,
against the same telemetry and the same forecast, and writes to nothing — no request, no mode, no
device. What comes back is the plan you will then watch, in the same words and the same figures, and
*Start charging* under it commits exactly that. Editing any field drops the preview, so it is never
possible to confirm a plan that no longer describes the form above it. This matters most in the case
that cannot be fixed afterwards: *"even flat out you get 24 of the 31 kWh you asked for; the departure
that covers it is 05:40"* is worth knowing **before** the charger starts.

**Two ways to say what you need.** The default, and the only one an install without a vehicle feed ever
sees, is **Energy to add (kWh)**. With a car reporting its SOC *and* a configured pack capacity,
a second basis appears — **Battery target (%)** — and the kilowatt-hours are worked out for you:

```
(target% − now%) / 100 × usable capacity ÷ charge efficiency
```

The tab shows what the car last said about itself above the form — **battery, range, plug state,
charge state and the age of the reading** — because those are the numbers the plan is about to be built
from.

**A reading past `Vehicle:MaxAge` is refused rather than converted** ([#179](https://github.com/mpospisil/gleanvolt/issues/179)).
This used to go the other way, and the reversal is worth stating: the argument for converting anyway
was that a parked car's SOC does not drift, so refusing would only push you into doing the same
arithmetic in your head from the same figure. That held while **two** feeds wrote to one holder and the
newest won — one of them was usually recent. It stopped holding when the MQTT feed went off and
`vw-website` fell silent except during a charge: for most of the day the portal is the only thing
reporting, and there is no second opinion left to correct a stale reading. [#141](https://github.com/mpospisil/gleanvolt/issues/141)
measured that portal at a **median 5 h 10 m old when it arrived**, worst case **2 d 22 h**.

So the form says how old the reading is and what the limit is, and asks for kilowatt-hours instead:

> The car last reported 42% 13.0 h ago, past the 12.0 h this installation treats as current
> (`Vehicle:MaxAge`), so there is no honest way to say what that target is in kilowatt-hours. Read the
> car again, or ask in kilowatt-hours instead.

Said **under the box as well as on Start**, so a hint promising "that is about 32.5 kWh" cannot sit
above a button that refuses. Pressing **Ask the car** on [`/car`](WEB_UI.md#car) is the fix, and then the
same request again.

Three things this deliberately is not:

- **It does not gate charging.** Asking in kilowatt-hours never touches the car's reading, so a dead
  feed cannot stop a charge — and `Full` on a fast charge asks the car for nothing at all. The feed
  stays advisory; nothing on a hardware path depends on it.
- **It never fires on a car with no feed.** An installation that reports nothing is fully supported
  ([#137](https://github.com/mpospisil/gleanvolt/issues/137)) and sees no change: *absent* is refused
  for being absent ("the car has not reported a state of charge"), which is a different sentence
  wanting a different fix. The guard triggers on *configured and old*, never on *missing*.
- **It does not touch a request already running.** The conversion was made once, at activation, from
  whatever was current then. A reading going stale afterwards does not revise a promise that is
  already part delivered — that is the same one-shot rule, from the other side.

The conversion happens **once, at the moment you start it**, and is never re-derived. A parked car
reports when it feels like it, so a SOC that jumps six points at 02:00 because the car finally phoned
home would otherwise silently move a promise that is already half delivered. What is recorded is the
energy; the target and the SOC it was measured from are kept only so the request can be read back as
"42% → 80%".

From Home Assistant: the
**Target energy** number, the **Departure time** text (`07:00` means the next 07:00; `2026-08-11
07:00` means exactly that), and the **Activate target** button. Both drive the same two seams in
`Gleanvolt.Core` — the request selector and then the charge action, in that order — so the two
surfaces cannot disagree about what was asked for. Home Assistant speaks kilowatt-hours only: the
battery-target basis is a convenience over a contract it already speaks, and a `target_soc` entity can
follow if it is wanted. **Start charging** also puts the charger into `Fast`,
like every other way of starting charging; **Cancel** is the same `Off` action the page's own button
is, plus the one thing that button cannot know to do — dropping the request, so nothing is left
looking like a promise.

A departure may be at most `ChargeControl:Targeted:MaxHorizon` (36 hours) ahead. Solcast's cached
forecast runs days further, but a target four days out is a promise the forecast cannot keep — and
neither can a request that does not survive a restart.

```jsonc
"ChargeControl": {
  "Targeted": {
    "SafetyMargin": "00:15:00",   // finish this long before the stated departure
    "MaxHorizon": "1.12:00:00",   // the furthest ahead a departure may be set (d.hh:mm:ss -- "36:00:00" is 36 *days*)
    "GridBridge": true,           // may the grid lift a sub-floor surplus to 6A? see above
    "JustInTime": {
      "RestSocPercent": 80,       // where the car waits before the last stretch is released
      "ReleaseSlack": "00:30:00"  // how much earlier than strictly necessary that stretch starts
    }
  }
}
```
