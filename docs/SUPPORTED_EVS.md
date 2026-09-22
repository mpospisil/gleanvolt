# Supported electric vehicles

Which cars Gleanvolt can charge, and which it can read. Those are **two different questions**, and
the answer to the first does not depend on the second.

> **Last reviewed 2026-09-16.** The charging half is a fact about the charging standard and changes
> slowly. The reading half depends on manufacturers' cloud services, which change without notice —
> VW closed its app API to third parties in May 2026. Treat every row marked *not tested* as a
> pointer, not a promise.

## At a glance

| Your car | Can Gleanvolt charge it? | Can Gleanvolt read its battery? | Tested |
|---|---|---|---|
| **Any EV or plug-in hybrid with a Type 2 AC inlet** | Yes — the charger drives it, the car needs no account | Not by itself; see the rows below | — |
| **Volkswagen, Audi, Škoda, SEAT, Cupra, Bentley** | Yes | **Built in**: the VW Group EU Data Act portal | VW ID.4 Pro |
| **Volkswagen** (a car in myVolkswagen) | Yes | **Built in**: volkswagen.de, live while charging | VW ID.4 Pro |
| **Škoda** (a car in MySkoda) | Yes | **Built in**: the MyŠkoda Public API, live | **Not yet** — built from the published spec |
| **Any other brand** | Yes | No — targets are given in kWh instead of % | — |

The reference installation charges a **VW ID.4 Pro** (77 kWh usable, three-phase, 16 A). It is the
only car Gleanvolt has been run against. Everything else below is what the design and the charging
standard say should work, and says so.

---

## Part 1 — Charging: any Type 2 AC car

Gleanvolt never talks to the car to charge it. It talks to the **SolaX X1/X3-HAC charger** over Modbus
TCP, and the charger talks to the car over the Type 2 control pilot (IEC 61851) exactly as any
wallbox does. From the car's side there is no Gleanvolt, only a wallbox offering a current.

That makes the list of chargeable cars simple: **every EV and plug-in hybrid sold in Europe with a
Type 2 AC inlet.** No manufacturer account, no cloud and no internet connection is involved, and a
dead car feed changes nothing about how the charger is driven.

What does differ from car to car is what it will accept. Describe it in the
[`Ev` section](../README.md#the-car-the-ev-section) (or `EV_*` in `deploy/.env`), and the controller
works to the narrower of the car and the installation:

| Setting | Why it matters for your car |
|---|---|
| `EV_PHASES` | **The one that goes wrong quietly.** A single-phase car behind a three-phase charger that does not say so has every power figure overstated threefold. |
| `EV_MIN_CHARGING_CURRENT_AMPS` | A car that refuses 6 A draws nothing at all when offered it, and a connected car drawing nothing reads as *finished*. |
| `EV_MAX_CHARGING_CURRENT_AMPS` | The on-board charger's ceiling. An ID.4 takes 16 A per phase even from a 32 A wallbox. |
| `VEHICLE_BATTERY_CAPACITY_KWH` | The **usable** pack. Needed only to ask for a target as a percentage. |

### Cars that need a closer look

None of these stops a car charging. They are the cases where leaving the settings above at their
defaults gives a charge that stalls, flaps, or is budgeted wrongly.

| Behaviour | Cars known to show it | What to do |
|---|---|---|
| **Minimum current above 6 A** | Renault Zoe and Twingo Electric: 8 A on three phases, older Zoe 10 A ([evcc docs](https://docs.evcc.io/en/vehicles/renault/)) | Set `EV_MIN_CHARGING_CURRENT_AMPS`. Surplus modes then start later in the day, which is correct. |
| **Single-phase on-board charger** | Nissan Leaf (ZE0/ZE1), and many plug-in hybrids | Set `EV_PHASES=1`. The X1-HAC or a three-phase HAC then runs the car from 6 A ≈ 1.4 kW, so surplus charging starts far earlier. |
| **Refuses to resume after repeated pauses** | Reported for early Renault Zoe and Nissan Leaf models | Solar modes pause by dropping the current to 0 A. Hysteresis (`ResumeHysteresisWatts`) and the restart wait (`MinPauseTime`, 15 min) keep pauses rare, but a car that faults after a few will need replugging. Not tested. |
| **High three-phase floor** | Every three-phase car | The HAC does not switch phases, so 6 A × 3 ≈ **4.2 kW** of surplus is needed before a solar charge starts. That is the charger, not the car; single-phase cars start at ~1.4 kW. |

If your car shows a behaviour that is not in this table, that is worth an issue: this table is how the
next owner of the same car finds out.

---

## Part 2 — Reading the car: optional

Reading the car adds three things and removes none:

- a **battery target as a percentage** ("80% by seven") instead of only kilowatt-hours;
- the car's own **range** and **charge state** on the dashboard;
- a **state-of-charge curve** recorded against each charging session.

**No charging decision depends on it.** A reading shapes what you *ask* for; it never steers how the
charger delivers it. Without a feed, every kWh target, every solar mode and every fast charge works
unchanged.

There are three routes, all built into Gleanvolt.

### Route A — VW Group EU Data Act portal (built in)

The EU Data Act obliges the manufacturer to hand the owner their car's data, free. Gleanvolt signs in
to VW Group's statutory portal as the owner and reads it every fifteen minutes.

| | |
|---|---|
| **Brands** | Volkswagen (passenger cars and commercial vehicles), Audi, Škoda, SEAT, Cupra, Bentley — `VW_BRAND=vw`, `audi`, `skoda`, `seat`, `cupra`, `bentley` |
| **Cars** | Any connected car the portal lists for your account. For Gleanvolt that means EVs and plug-in hybrids: a combustion car has no battery to report |
| **Reads** | State of charge, range, charge state, charge time remaining, the car's own target SOC |
| **Does not read** | Plug state — never, on 568 of 568 reads on the reference car |
| **Freshness** | **Hours behind the car**: 1 h 48 m to 7 h 16 m measured. Good for planning, too late to follow a charge |
| **Needs you** | A one-off consent in a browser, and a continuous data request set up in the portal |
| **Tested** | VW ID.4 Pro only. Field names come from a real ID.4 (MEB) download; the flat layout older plug-in hybrids use is from a description, not a capture |
| **Setup** | [VW_PORTAL_SETUP.md](VW_PORTAL_SETUP.md) |

A brand missing from the table, or one whose sign-in id has changed, can still be used with
`VW_CLIENT_ID` — see the setup guide. If battery or range comes back blank on a model other than an
ID.4, the **Vehicle portal** page lists the field names it did not recognise, and those are what
`VwGroupFieldNames` is missing.

### Route B — volkswagen.de (built in)

The live source, as the portal is the batch one. It reads the charging status the myVolkswagen pages
on volkswagen.de show, and it asks **only while a charging session is open**.

| | |
|---|---|
| **Brands** | Volkswagen only |
| **Cars** | A car linked to your Volkswagen ID in myVolkswagen, with you as primary user. Verified on an ID.4; other VW EVs and plug-in hybrids that show a charging status there should work, and are not tested |
| **Account** | A Czech Volkswagen ID works on volkswagen.de; other countries' accounts are expected to, and are not tested |
| **Reads** | State of charge, range, charge state, charging power, **plug state** |
| **Freshness** | About 20 seconds behind the car |
| **Needs you** | An email one-time code on every cold sign-in, entered on **Vehicle portal → Sign in**. The remembered session survives restarts |
| **Setup** | [`Vehicle:Website`](../README.md#vehiclewebsite--volkswagende-the-live-source) in the README |

Routes A and B complement each other and run side by side on the reference install: the portal carries
the target SOC and time remaining, the website carries plug state and a curve while charging, and the
freshest reading wins.

> **This is not the app API that closed.** In May 2026 VW shut third-party access to its "Brand App
> Interface" for the ID. family across VW, Audi, Cupra and Škoda
> ([electrive](https://www.electrive.com/2026/06/01/vw-locks-api-for-external-charging-control/)),
> which is what broke most third-party VW tools. Route B uses the website's own sign-in
> instead. It is still undocumented, so VW can change it without notice; when it does, Route A is the
> fallback and nothing about charging is affected.

### Route C — MyŠkoda Public API (built in, not yet verified on a car)

The live source for a Škoda, in Route B's place. Škoda published a documented REST API for owners on
2026-08-31 (MySkoda app 8.16): an [OpenAPI spec](https://public.api.connect.skoda-auto.cz/v3/api-docs),
typed problem responses and rate-limit headers.

| | |
|---|---|
| **Brands** | Škoda only. Nothing says the other VW Group brands will follow; `Vehicle:Skoda:BaseUrl` is there to try it if one does |
| **Cars** | A connected Škoda EV or plug-in hybrid in your MySkoda account |
| **Reads** | State of charge, range, charge state, charge time remaining, **plug state** (derived: `CONNECT_CABLE` means unplugged) |
| **Freshness** | As fresh as Škoda's cloud: every 15 minutes idle, every 5 while charging. How often the car itself reports while charging is not yet known |
| **Needs you** | An API key created in the MySkoda app, pasted once on **Vehicle portal**. It expires; renewing is pasting a new one |
| **Quota** | 20 requests an hour per VIN, shared with *Ask the car* |
| **Tested** | **No.** Built from the published spec with spec-built fixtures; nobody has run it against a Škoda yet. Reports in [issue #193](https://github.com/mpospisil/gleanvolt/issues/193) are very welcome |
| **Setup** | [`Vehicle:Skoda`](../README.md#vehicleskoda--the-myškoda-public-api-the-live-source-for-a-škoda) in the README |

Route C and Route B cannot run together — they read two different cars, and an installation has one.
Route A (`VW_BRAND=skoda`) may run beside Route C, and the freshest reading wins. The key Route C uses
is scoped to the cars it names, expires, and is revocable in the app — a much smaller secret than a
brand password — and it clears Gleanvolt's bar below more comfortably than Route B: a documented
interface the manufacturer published for exactly this use.

**Prefer the weakest credential that does the job, because whichever one you choose ends up on the
Pi.** All three routes leave something bearer-equivalent in the data directory — Route C an expiring,
revocable, VIN-scoped key; Route B a live volkswagen.de session; Route A the portal credentials — and
what protects it there is a file mode on Linux and DPAPI on a Windows install, never encryption the
controller could claim with a straight face. That is the whole argument for the ranking above, and
[the data directory holds secrets](../README.md#the-data-directory-holds-secrets-the-secrets-section)
is where it is written down.

### Why only VW Group is built in

Gleanvolt's rule is that a manufacturer earns a built-in client only through a **documented statutory
interface**: the owner's own data by law, free, and publicly accountable when it breaks. An
undocumented app API can disappear in a release note, as VW's did.

Most manufacturers' EU Data Act offerings do not yet meet that bar. What owners reported by late 2025
([evcc discussion #23684](https://github.com/evcc-io/evcc/discussions/23684)):

| Manufacturer | Data Act access for owners | Usable as a feed? |
|---|---|---|
| VW Group | Portal, scheduled JSON/CSV deliveries | **Yes — Route A** |
| Škoda (beyond the Data Act) | A documented owner API with keys, since 2026-08 | **Yes — Route C** |
| BMW, MINI | CarData: API and a real-time MQTT stream, free | **Yes** — the strongest candidate for a second built-in client |
| Tesla | The existing Fleet API, with a free tier | Possibly, but it wakes the car |
| Hyundai, Kia, Genesis | A file sent by email on request | No |
| Stellantis (Peugeot, Citroën, Opel, Fiat, Jeep, DS) | A ticket in a privacy portal | No |
| Renault | A paid commercial data service | No |
| Porsche, Volvo, Mazda | Manual download requests | No |

Until one of those becomes a continuous feed, those cars are charged but not read.

---

## Help the list grow

If you charge a car that is not the reference ID.4, an issue with **the make, model and year, the
phases and minimum current it actually charges at, and which route (if any) reads it** is the most
useful thing you can contribute to this page. A row that says *tested* is worth ten that say
*expected*.
