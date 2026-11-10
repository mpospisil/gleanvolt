# Solar with grid help (the `SolarGrid` mode)

For the day you want the car to take the sun as it comes, are happy to buy a little grid to make a weak
sun usable, and want nothing to happen once the sun is gone. It is `Solar` without the battery-full
gate, plus a grid bridge, plus an end.

One number steers it: the **minimum solar surplus** — `ChargeControl:SolarGrid:MinSurplusWatts`,
2000 W by default, settable at runtime from the **Solar + grid** tab and the **Min solar surplus**
entity. It is measured against the same smoothed 3-minute surplus every solar mode decides on.

| Smoothed surplus | Decision | From the grid |
|---|---|---|
| at or above the charger's floor (~4.14 kW at 6 A on three phases) | follow the surplus, whole amps, up to `MaxChargingCurrentAmps` | nothing |
| at or above your minimum, below the floor | charge at the 6 A floor | the gap to the floor, with the discharge hold armed |
| below your minimum | pause — see below | nothing, except while a running charge is held at the floor |

Set the minimum at or above the floor and the grid never helps: the mode is then plain surplus
charging with no battery gate, ending at sunset.

## Clouds

The same machinery as the other solar modes, so a passing shadow does not stop the car:

- the **3-minute moving average** (`ChargeControl:SurplusAverageWindow`) is what is compared, never
  the instantaneous reading;
- a paused car restarts only **`ResumeHysteresisWatts` above** the minimum, while a running one
  continues down to the minimum itself;
- a running charge that dips below the minimum is **held at 6 A** for `ChargeControl:Forecast:MinRunTime`
  (10 min) — with the gap reported as a grid bridge, so the pack stays out of it — and a paused one
  waits `ChargeControl:Forecast:MinPauseTime` (15 min) before restarting. The restart wait only applies
  once **this mode** has charged the car: a wait counted from the button press would waste the sun that
  is there now. A charge the charger started by itself when the car was plugged in does not count — the
  mode takes that car over at once, at whatever the surplus supports.

## The end of the day

Only once the live surplus is under the minimum does the forecast get a say, and it answers one
question: does any stretch of the rest of today have a forecast surplus — PV at
`ChargeControl:SolarGrid:ForecastConfidence` (the median, `P50`, by default), less the learned house
load — that clears the minimum?

- **Yes, now or within 15 minutes** — pause; this is a dip. A dip that outlasts 5 minutes is stood
  down all the same.
- **Yes, but later** — the charger is **stood down** (use-mode `Stop`), because this wallbox will not
  sit in Fast at 0 A for hours. The first live surplus over the start threshold re-arms it.
- **No** — the car is paused and the mode returns itself to `Off`, exactly as the Off button would.

**The forecast never turns away real sun**: a surplus over the minimum charges the car whatever the
forecast said about the afternoon. With **no usable forecast** — none fetched, or older than
`ChargeControl:Forecast:StaleForecastAfter` with sun still in it — the mode waits on live surplus,
stood down because nobody knows for how long, and never ends itself on the forecast's word. The one exception is an old forecast with no PV at all left
today, even in its optimistic band: the evening is exactly when the forecast stops being refreshed,
and "the sun has set" does not go out of date.

It also ends itself, like `Targeted`, when the car **stops drawing** for `CompletionDwell` while being
asked to charge, or is **unplugged** — a full car held at a bridged 6 A would otherwise keep the pack
held and the house on the grid for nothing.

## What SolarGrid doesn't do

- **No battery-full gate.** The car competes with the home battery for the surplus from the first watt
  over the minimum. Use `Forecasted` if the battery must reach 100% by evening.
- **No battery loan.** The grid is the only source for the bridge — and only with
  `BatteryHold:Enabled`. The hold arms whenever the car draws more than the sun gives: while the bridge
  runs, and also when a cloud leaves the car drawing more than `BatteryHold:BridgeShortfallWatts`
  (300 W) beyond the live surplus, before the 3-minute average has caught up and a bridge is decided.
  It stays armed for `BatteryHold:BridgeReleaseDwell` (3 min) after the last of those while the car is
  charging, so an average hovering on the 6 A floor does not flap it; a pause or the end of the mode
  releases it at once. Without the hold the bridge still runs, but the inverter serves the gap from the
  pack; a warning says so when the mode is selected.
- **Nothing survives a restart**, the minimum set at runtime included.

## SolarGrid on low tariff only

With a [grid tariff](CONFIGURATION.md#grid-tariff-the-tariff-section) set up, **Low tariff only** — on the *Solar +
grid* tab, the **SolarGrid low tariff only** switch, or `PUT /charging/low-tariff-only` — makes **high
tariff pause the car, sun or not.** The mode then charges only in low tariff, as `Fast` does with the
same option. The cost is a sunny high-tariff afternoon: the surplus goes to the home battery or the grid
rather than the car. Waits follow the rule above: stood down for 15 minutes or more, paused for less.

The day's end still applies. The option narrows when the mode charges and never keeps it alive past its
sun, so a low tariff that starts after dark charges nothing in this mode; use `Fast` with the same
option for that. With no tariff set up the option is ignored, and the Overview and the tab say so.

| Key | Default | Meaning |
|---|---|---|
| `ChargeControl:SolarGrid:MinSurplusWatts` | `2000` | The boot value of the minimum solar surplus, in watts. |
| `ChargeControl:SolarGrid:ForecastConfidence` | `P50` | The forecast band that decides whether sun is still to come. |
| `ChargeControl:SolarGrid:LowTariffOnly` | `false` | The boot value of **Low tariff only**. |
