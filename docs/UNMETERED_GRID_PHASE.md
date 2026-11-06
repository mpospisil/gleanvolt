# Unmetered grid phase

*Česky: [UNMETERED_GRID_PHASE.cs.md](UNMETERED_GRID_PHASE.cs.md)*

**In short: leave it empty.** Set it only if the grid meter of your PV system does not measure one of
the three phases. On a correctly wired installation, setting it makes things worse, not better.

## What it is about

A three-phase SolaX hybrid inverter has a **grid meter** where the house connects to the grid: a smart
meter or current clamps that measure how much power flows in from the grid, or out to it, on each phase
— L1, L2 and L3 (SolaX calls them R, S and T).

Gleanvolt decides when to charge the car from that meter. The surplus is the solar power that would
otherwise go to the grid. The house's own consumption is not measured anywhere; it is worked out:

> house = solar + grid − car − home battery

So everything depends on the grid figure being complete.

## What goes wrong when one phase is not measured

If the clamp on one phase is missing, disconnected or on the wrong cable, the meter reads **0 W on that
phase whatever actually flows there**. Everything on that phase becomes invisible:

- **The sun the inverter exports on that phase is not seen.** The inverter spreads its output over all
  three phases, so about a third of the export disappears. Gleanvolt concludes that the house is using
  it — the house load seems to follow the sun — and finds little or no surplus. The solar modes then
  never start the car, even on a sunny afternoon with a full home battery.
- **The car's draw on that phase is not seen.** A car charging on three phases takes a third of its
  power through the blind phase. Gleanvolt sees less grid import than there really is, believes the
  sun is covering the car, and the charge quietly takes power from the grid.

## Signs that your meter may be blind on one phase

- Sunny afternoon, home battery full, yet Gleanvolt shows a large house consumption and almost no
  surplus, and SolarGrid or Forecasted does not start charging.
- The house consumption on the Overview rises and falls with the solar production.
- While the car charges, the house consumption shows a dash (it came out below zero), or the grid
  import is smaller than what the car alone draws.
- Your electricity bill shows more import than the inverter or Gleanvolt reports.

## How to confirm it

Look at the grid meter's **per-phase** readings while the sun shines — on the inverter's display, in
the SolaX Cloud app, or ask your installer.

- **Meter wired correctly:** all three phases show values that change over time.
- **Blind phase:** one phase stays at **0 W** all the time, while the inverter's own output on that
  same phase is clearly not zero.

For the technically minded: over Modbus, the meter's per-phase registers are `0x82`/`0x84`/`0x86`; the
inverter's per-phase output registers are `0x6C`/`0x70`/`0x74`. A blind phase reads 0 W in the first set
and not in the second.

If you cannot check it yourself, leave the field empty and ask the installer.

## What to enter

The phase that reads 0 W: `L1`, `L2` or `L3`, or SolaX's `R`, `S` or `T` (R = L1, S = L2, T = L3).
Save, then restart Gleanvolt from the Health page.

## What Gleanvolt does with it

It estimates the flow on the blind phase from what is known:

- the inverter's output on that phase, which the inverter reports itself;
- the car's share, assuming the car draws evenly from the phases it charges on (a single-phase car is
  taken to be on L1);
- the house's consumption on that phase, taken as half of what the two measured phases carry.

It is an estimate: a single large appliance on the blind phase — an oven, a kettle — puts it off for a
while. But it is far closer than leaving the phase out. Every grid figure — the Overview, the history,
Home Assistant — then uses the estimate; the log prints the meter's own reading beside it.

## The real fix

Have the installer fit or fix the clamp. Once the meter measures all three phases, **empty this field
and restart**. Left set on a meter that sees every phase, it counts that phase twice and makes the
charging decisions wrong in the other direction.
