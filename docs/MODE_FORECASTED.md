# Forecast-driven charging (the `Forecasted` mode)

`Solar` waits for the home battery to be essentially full before the car gets anything. That costs
real energy: on a good day the battery only fills around midday, so the car starts as production is
already falling, and on three phases the charger's **6 A floor is ~4.2 kW** (no phase switching), so
any surplus below that charges *nothing* and is exported once the battery is full.

`Forecasted` replaces the fixed gate with a day plan built from the Solcast forecast, recomputed every
poll. It keeps one promise: **the home battery reaches 100 % by the configured evening deadline.**
Everything else follows from that.

## The shoulders belong to the battery; the plateau belongs to the car

A kilowatt-hour at 08:00 is not interchangeable with one at 13:00, because the two consumers can't
take the same power:

| | Surplus below ~4.2 kW ("shoulder") | Surplus at or above it ("plateau") |
|---|---|---|
| Home battery | takes it — it accepts any power | takes it |
| EV charger | **cannot charge at all** | can charge |

So filling the battery from the plateau wastes the one scarce window, while filling it from the
shoulders costs the car nothing. The plan splits the remaining forecast by power level, books the
battery's need **backwards from the deadline** (the latest production first, so the afternoon shoulder
and, only if needed, the plateau tail), and hands the car whatever is left at a usable power.

```
EvBudget      = RemainingPv − ExpectedHouse − BatteryToFull
FeasibleEv    = the part of that arriving at ≥ the charger's minimum power, after the battery's booking
TrajectoryFloor = 100 − (remaining surplus × efficiency ÷ capacity) × 100
SocFloor      = max(TrajectoryFloor, MinBatterySocFloorPercent)
```

The evening guarantee needs no scheduling code: as the day burns down, the remaining surplus falls, so
`SocFloor` climbs toward 100 % and squeezes the car out of the late afternoon by itself. A
`FinalGuardBefore` window (default 1 h) pauses the car outright as a backstop.

### Not flapping on the floor

SOC arrives as **whole percent**, so a car charging right at the floor could stop and restart every
few minutes. Three things prevent that:

- **A resume margin.** Charging continues down to `SocFloor` itself, but a paused session only comes
  back at `SocFloor + FloorResumeMarginPercent` (default 5 %) — the SOC counterpart of
  `ResumeHysteresisWatts`.
- **The clamp and the trajectory are different events.** Falling through `TrajectoryFloor` puts the
  evening 100 % at risk and stops the session at once. Falling through the `MinBatterySocFloorPercent`
  clamp while the trajectory sits far below — the normal case on a sunny morning, when the sun could
  still recover a much deeper discharge — is a preference, not a physics problem, so it goes through
  the `MinRunTime` dwell timer like any other soft reason and the session is held at 6 A rather than
  cut short. Both floors are published, as `soc_floor` and `soc_floor_traj`.
- **A guard band, so the pack recovers instead of hovering.** While SOC is inside the resume margin,
  `FloorGuardReserveWatts` (default 750) of surplus is withheld from the car for the battery, and the
  battery lends nothing. On a 9 kWh pack that clears a 5 % band in about forty minutes and costs a
  three-phase session roughly one amp; where the surplus is marginal it stops the session, giving one
  long charge instead of many short ones.

## The battery loan

On three phases a 3 kW surplus charges nothing. If the forecast shows the day can repay it, the
battery lends the difference — exactly enough to reach the charger's threshold, never more — turning
would-be export into charge. The bounds:

- **never below the plan's SOC floor**, nor below `MinBatterySocFloorPercent` (default 50 %), with
  `LoanSocMarginPercent` of clearance above it before lending starts at all. Lending lowers SOC, which
  raises the floor, which ends the car's day earlier: the repayment needs no scheduling.
- **`MaxLoanPowerWatts`** (default 3800) caps the bridge and the discharge rate. It has to be able to
  reach the charger's floor from the thinnest surplus a loan is granted for, or it silently imposes a
  floor of its own.
- **`MaxDailyLoanKWh`** (default 4) caps *outstanding* lending — lent minus what the pack has charged
  back, reset at local midnight.
- **A minimum live surplus.** While the pack has room for the day's surplus,
  `MinBridgeSurplusWatts` (default 2000): the loan tops up a real surplus and never funds a session
  from the pack. When the day makes more surplus than the pack can absorb by the deadline (published as
  **surplus spill**), the energy lent would otherwise be exported, so `SpillBridgeSurplusWatts`
  (default 400) applies instead.

The same thresholds decide what the plan calls a **charge window**, so a window drawn on the Forecasted
tab is one the controller will enter.

`Forecasted` never arms the [battery discharge hold](BATTERY_HOLD.md) itself; a hold you switch on
still applies.

## When the sun can't cover everything

The priority order is fixed and not configurable:

> **1. House load → 2. Battery to 100 % by the deadline → 3. EV.**

The car absorbs the entire shortfall, and **no grid charging is ever initiated**. The plan is published
so a thin day is visible early:
the **charge window**, the **EV energy budget** in it, the **projected shortfall** (how far the day
falls short of the house plus a full battery — the evening 100%, which the car is never part of),
and the **plan state** sentence, all on HA and in the log as soon as the forecast allows.
Tomorrow's forecast rides along in the same Solcast response, so a bad day comes with the context of
whether waiting is worth it.

**The mode takes no amount.** To stop at a charge level, set the limit in the car. To ask for an amount
by a time, use [`Targeted`](MODE_TARGETED.md) — with
[**Sun only**](MODE_TARGETED.md#sun-first-grid-allowed--or-sun-only) if nothing may be bought.

## Until full

A car left at home over a weekend or a week rarely fills on one sunny afternoon; three usually do it.
**Until full** — a checkbox on the *Forecasted* tab, the **Forecasted until full** switch in Home
Assistant, or `PUT /charging/until-full` — keeps one `Forecasted` session going over as many days as that
takes.

- **By day, nothing changes.** The same plan, floor, loan, dwell timers and final guard: the home battery
  still reaches 100 % by the deadline before the car gets anything extra, and the grid never helps.
- **At nightfall it stands down instead of ending.** Where the mode would end itself as `NoSunLeftToday`,
  it puts the charger in `Stop` for the night. The Overview says *"Forecasted mode (until full, day 2) is
  on and has stood the charger down until ~09:40 (tomorrow's first window)"*; that time is the forecast's
  estimate. The morning restart is the new day's plan: once it has a window and the sun is there, the
  charger goes back to `Fast`. A day with no window stays stood down, and the session waits for the next.
  It never gives up on its own.
- **Without a usable forecast** it falls back to live surplus, as it always does, and the wait rule stands
  it down through the night.
- **It ends** when the car is full (`SessionComplete`), it is unplugged (`CarUnplugged`), the charger is
  taken over (`ChargerTakenOver`), or you press **Off**.
- **One session.** The session history keeps it as one charge, with each night's stand-down and each
  morning's re-arm as events, and the Overview's session card reads *"Forecasted · until full · day 2"*.
- **It survives a restart.** The charge mode is otherwise never restored: a restart comes back to Off.
  A session running with **Until full** on is the one exception, because a deploy or a reboot would end a
  week-long charge. It is kept in `data/until-full.json` while it lasts, and on startup the mode is started
  again the way a button starts it: the charger goes to `Fast`, and the first poll stands it straight back
  down if it is night. A charger that does not answer at startup is retried every poll for ten minutes.
  The day count carries on; the session history records the restart as a new session, as it does for any
  restart.

With it off, `Forecasted` ends with the day's sun, as before. The option itself is a runtime setting:
`ChargeControl:Forecast:UntilFull` is only its value at boot.

## Forecast versus reality

A plan is only as good as the forecast under it, so the controller checks continuously:

- **House load is learned per hour of day**, not as one rolling average. At startup each hour is rebuilt
  from the last `HouseLoadHistoryDays` (14) of the energy history, so a deploy doesn't send the plan
  back to guessing. An hour with less than two days of history starts from `BaselineHouseLoadWatts`.
  The profile is logged at startup and once a day (`House profile:`) so the learned shape is visible.
- **Realised bias** — `actual ÷ forecast` over elapsed daylight, clamped to `[0.5, 1.2]` and applied
  to the remaining forecast. The clamp is asymmetric, so a sunny morning can't talk the planner into
  over-committing the afternoon. It stays at 1.0 until `BiasMinPeriods` daylight periods have closed.
- **Per-period reconciliation** — one log line per closed 30-minute period.
- **Trust guard** — if the bias leaves `[0.6, 1.4]` for `TrustBreachPeriods` consecutive periods, the
  plan is abandoned for the day with a warning and the mode falls back to `Solar` behaviour. The same
  fallback covers a missing or stale forecast: an absent forecast must never read as headroom.

The plan is built on the **p10** band (`pv_estimate10`) by default, not the median: a guarantee planned
on p50 is missed about half the time. While this mode is running the forecast is refreshed every **3
hours**, and not overnight — about 5 calls a day against the free tier's 10.

## What it logs

```
Day plan: Shoulder=3.4kWh Plateau=11.0kWh … EvBudget=10.5kWh Feasible=10.5kWh Window=12:30-15:50 Short=0.0kWh SocFloor=62% Bias=0.94 (P10)
Forecast check: Period=12:00-12:30 Forecast=3120Wh Actual=2890Wh Delta=-230Wh (-7%) … Bias=0.94
Charge control: Mode=Forecasted … Action=Charge Target=6A Loan=1140W Session=8.4kWh LoanedToday=1.8kWh. …
Day summary: ForecastToday=28.1kWh ActualToday=26.4kWh (-6%) BatterySoc@19:00=100% EvDelivered=14.2kWh …
```

The day plan logs at `Information` when it changes (usable or not, the window appearing or going, the
budget moving by a whole kWh) and at `Debug` otherwise. The summary logs once, at the deadline.

```jsonc
"ChargeControl": {
  // ... the live-solar settings above still apply; Forecasted reuses the same charger limits
  "Forecast": {
    "FullByTime": "19:00:00",        // evening deadline (local time) for a 100% battery
    "UntilFull": false,              // boot value of "Until full": one session over several days
    "UntilFullSessionPath": "data/until-full.json", // such a session, kept so a restart resumes it
    "BatteryCapacityKWh": 9.0,       // REQUIRED, no default: USABLE capacity (see the warning below), not nameplate
    "ChargeEfficiency": 0.95,
    "BaselineHouseLoadWatts": 350,   // house load for an hour neither the history nor the live profile knows yet
    "HouseLoadHistoryDays": 14,      // energy history the house-load profile is rebuilt from at startup, 0 = off
    "ForecastConfidence": "P10",     // P10 | P50 | P90 — P10 is what makes the guarantee honest
    "MinBatterySocFloorPercent": 50, // hard floor, whatever the forecast says
    "FloorResumeMarginPercent": 5,   // SOC recovery required before a paused session restarts
    "FloorGuardReserveWatts": 750,   // surplus kept for the battery inside that band, 0 = off
    "EnableBatteryLoan": true,
    "MaxLoanPowerWatts": 3800,       // must be able to bridge the thinnest allowed surplus up to ~4.2 kW
    "MinBridgeSurplusWatts": 2000,   // no loan below this while the pack has room for the surplus
    "SpillBridgeSurplusWatts": 400,  // ...and below this when it has not: the energy was leaving anyway
    "MaxDailyLoanKWh": 4,            // a wear backstop, counted on OUTSTANDING lending (lent − recovered)
    "LoanSocMarginPercent": 2,
    "MinViableWindow": "00:30:00",   // shortest forecast window worth starting a session for
    "MinRunTime": "00:10:00",        // dwell timers: no start/stop churn faster than these
    "MinPauseTime": "00:15:00",      // guards a restart only, never a mode's first charge (SolarGrid,
                                     //   Forecasted and Targeted share it)
    "FinalGuardBefore": "01:00:00",  // pause the car this long before the deadline if SOC < 100%
    "StaleForecastAfter": "04:00:00",// older than this → fall back to Solar behaviour
    "BiasMinPeriods": 4,             // closed daylight periods before the bias is trusted
    "BiasClampMin": 0.5,
    "BiasClampMax": 1.2,
    "TrustBandMin": 0.6,             // sustained breach → abandon the plan for the day
    "TrustBandMax": 1.4,
    "TrustBreachPeriods": 3
  }
}
```

> ⚠️ **`BatteryCapacityKWh` is the pack's *usable* capacity, not its nameplate.** It is the one value
> with no safe default: the SOC floor, the battery's booking and the shortfall all scale off it, and a
> wrong figure makes the plan wrong in a way nothing else catches. So it ships unset: until it is set,
> the Forecasted day plan is unavailable (the mode charges from live surplus, as without a forecast),
> Targeted books nothing for the battery, and the log says so at startup. Under Docker it is
> `HOME_BATTERY_CAPACITY_KWH` in `.env`.
>
> The reference install is a **SolaX T-BAT H 2.5** stack of **10 kWh**, and runs with **9**: ~90 % of
> it usable (check your own datasheet for the usable figure). The distinction matters because the inverter reports SOC across the range it will actually cycle: 0–100 %
> spans the *usable* energy, not the nameplate.
>
> **If you must guess, guess high.** Both uses move in the safe direction when the figure is
> overstated — the battery books more of the forecast, and the SOC floor sits higher. Understating it
> is what risks missing the evening 100 %.
>
> You can measure it from the logs you are already producing. Over one uninterrupted climb with no
> discharge in between (say 30 % → 90 %), integrate the logged `BatteryPower` over time and divide by
> the SOC delta: `usable kWh = (∫ BatteryPower dt) / 0.60`. That also validates `ChargeEfficiency`,
> since the integral is measured at the battery terminals.

**Check the forecast before relying on it.** The accuracy tracker runs in every mode, so a week on `Off`
or `Solar` fills the log with `Forecast check` and `Day summary` lines: is p10 systematically low for
this roof, and does the shoulder/plateau split match the real curve?
