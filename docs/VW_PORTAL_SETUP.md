# Reading your car from the VW Group EU Data Act portal

How to get the four settings the **Vehicle portal** page needs, and what to do when it does not work.

> **Status: a diagnostic, not a feed.** Nothing in the controller reads the portal on a schedule —
> no dashboard card, no Home Assistant entity, no charging decision. This walks you to the point
> where the **Vehicle portal** page in the web UI shows your car's state when you press a button. Turning that into a running service is
> [issue #140](https://github.com/mpospisil/gleanvolt/issues/140); the design and the reasoning
> behind it are in [#137](https://github.com/mpospisil/gleanvolt/issues/137).

## Before you start

- A Volkswagen Group car — VW, Audi, Škoda, SEAT, Cupra or Bentley — with connected services active.
- The brand account you already use for the app (Volkswagen ID, myAudi, MyŠkoda, …), with the car
  linked to it.
- **Access is free.** The EU Data Act obliges the manufacturer to give the *owner* their vehicle's
  data at no charge. There is no developer programme to join, no API key to buy and no separate API
  user to create — which is exactly why the owner's own login ends up being the credential.

## The four parameters

| Variable | What it is | Where it comes from |
|---|---|---|
| `VW_USERNAME` | Your brand ID, an email address | You already have it — it is your app login |
| `VW_PASSWORD` | That account's password | You already have it. Nothing issues it |
| `VW_BRAND` | Which brand you drive | You know it. `vw`, `audi`, `skoda`, `seat`, `cupra`, `bentley` |
| `VW_VIN` | Which car, when the account sees several | Only needed with more than one car — step 4 |

**None of them requires looking anything up.** The first two are the credentials you already own; the
third is the badge on the car.

## Step 1 — Sign in, consent, and link the car

Open <https://eu-data-act.drivesomethinggreater.com/de/en> and sign in with your brand ID.

On first use the portal shows a **consent screen** — the legal grant that lets it release your
vehicle's data. Accept it, then link the vehicle.

> **This step can never be automated.** A consent screen is a legal act, and the client treats one
> appearing mid-flow as `OwnerActionRequired` and refuses to retry, because retrying a consent screen
> has never once helped anybody. The same applies to terms updates, an email OTP or a CAPTCHA.

## Step 2 — Enable a continuous data request

**This is the step people miss, and skipping it looks like a broken client rather than a missing
setting.** Signing in is not enough: the portal delivers nothing at all until you have asked it to.

In the portal: **Data clusters → Vehicle overview → Get customised data**. Choose **All data** and a
**15-minute** frequency.

Two things to expect:

- **It is not instant.** A newly created request can take a while — reports range from hours to a
  couple of days — before the first dataset appears. Until then the harness fails with
  `NoDataAvailable`, which is correct and not a bug.
- **Fifteen minutes is the floor.** The portal is a batch delivery, not a live API. Polling more
  often than the request's own frequency achieves nothing whatsoever.

## Step 3 — Say which brand you drive

```bash
VW_BRAND=vw
```

One of `vw`, `audi`, `skoda`, `seat`, `cupra`, `bentley`. Case and surrounding spaces do not matter.
That is the whole step.

<details>
<summary>What that actually sets, and what to do if your brand is missing</summary>

The portal signs in with an OIDC **client id** that differs per brand. It belongs to the portal
rather than to us — there is no "register your own OAuth app" route on this interface — so it has to
be looked up rather than owned. [`VwGroupBrands`](../src/Gleanvolt.Infrastructure/Vehicles/VwGroup/VwGroupBrands.cs)
holds the table and `VW_BRAND` picks the row, which is what every Home Assistant integration for this
portal does too.

Two rows are shared, which is a fact about the portal rather than a shortcut: VW passenger cars and
commercial vehicles are one client, and SEAT and Cupra are another.

**The ids are reverse-engineered, not published**, so one can go stale without warning. When that
happens — or for a brand the table does not list — state it outright and it overrides the table:

```bash
VW_CLIENT_ID=<the id>
```

To read your own: start signing in at the portal and, while you are on `identity.vwgroup.io`, take
`client_id=` from the address bar. If the redirect is too fast to catch, open developer tools (F12) →
**Network**, tick *Preserve log*, sign in again, and find the `/oidc/v1/authorize` request.

A misspelt brand does not fail as a missing one — the harness names it and lists what it knows:

```
Missing a known brand -- 'vollkswagen' is not one of vw, audi, skoda, seat, cupra, bentley;
set an explicit client id if yours is missing.
```

</details>

## Step 4 — Your VIN, if you need it

Only when the account can see more than one vehicle. With a single car, leave `VW_VIN` unset and it
is taken automatically.

You do not have to hunt for it: run the harness without it and, if there is a choice to be made, it
refuses and lists what it found rather than picking for you —

```
VehicleNotFound: the account can see 2 vehicles (WVW…1234, TMB…5678) and none was configured
```

Those are masked for the log. Take the full VIN from the portal's own vehicle list, or from the
windscreen.

## Step 5 — Put them in `.env`

In the repository root — `.env` is gitignored, and the worker loads the nearest one at or above the
working directory, so it is found from the project folder too:

```bash
VW_BRAND=vw
VW_USERNAME=you@example.com
VW_PASSWORD=<your brand ID password>
VW_VIN=<only if step 4 asked for it>
```

`.env.example` carries the same list with commentary.

The controller reads `.env` once at startup, so **changing any of these means restarting it**.

`Vehicle__DataAct__Brand` and friends work too, and are the better form for a deployment. The plain
`VW_*` names are honoured because they are shorter and because one `.env` should not need the same
credentials written twice; where both are set, the sectioned form wins.

## Step 6 — Press the button

Restart the controller, open the web UI and go to **Vehicle portal** in the navigation. Press
**Read the car now**.

The page shows what the car said — battery, range, charge state, plug state, and the *car's* capture
time rather than this moment's — then the delivery it came in, and then the fields nothing here
recognises yet.

Each press signs in afresh. That is deliberate: it is fine by hand and it is why this is not a poll.
Repeatedly replaying a password at a real identity provider is how accounts get locked, which is also
why nothing on this page loops on failure.

**The reading is not fed to anything.** The dashboard's car still comes from whatever telemetry feed
is configured, and no charging decision sees this. The page proves the credentials and the mapping;
[#140](https://github.com/mpospisil/gleanvolt/issues/140) is what turns it into a service with its
own clock and a health state.

### About the unrecognised fields

Expect that list to be non-empty on a first run, and read it rather than ignoring it.
[`VwGroupFieldNames`](../src/Gleanvolt.Infrastructure/Vehicles/VwGroup/VwGroupFieldNames.cs) was
written from a *description* of the portal rather than from a capture, so if the battery or range
above is blank, its real name is almost certainly in that list. Add the names it reports to that file
and the mapping stops guessing.

## When it does not work

The harness names the **kind** of failure and whether retrying could help. The kinds are deliberate:
a consent screen and an expired session need opposite responses, and a client that cannot tell them
apart either hammers a screen it can never answer, or gives up on something a re-login would fix.

| Reported | What it means | What to do |
|---|---|---|
| `NotConfigured` | Something is missing before anything was attempted | It names which of the three: brand, VW ID, password |
| `SignInRejected` | Credentials replayed and refused — or the form was not the shape the client expects | Check the password. If it is right, VW has changed the sign-in form and the parser needs updating. **Do not loop**: repeated failures risk the account |
| `OwnerActionRequired` | Consent, terms, an OTP or a CAPTCHA — a screen only a person can answer | Open the portal in a browser and clear it. Never retried by design |
| `VehicleNotFound` | No vehicle, or not the one asked for | Link the car in the portal, or set `VW_VIN` from the list it prints |
| `NoDataAvailable` | Signed in, but there is nothing to download | Step 2. Either no continuous data request exists, or it exists and has not been filled yet — the message distinguishes the two |
| `UnusableData` | The bundle arrived and could not be believed | Run with `--save-fixture` and look at what came back. Present-but-unusable is rejected whole, on purpose |
| `SessionExpired` | A 401, a bounce to `/login`, or HTML where JSON was expected | Ordinary. Run it again |
| `Transient` | A 5xx, a timeout, a dropped connection | Try later. The portal is new and community reports include 400s and 500s on the delivery endpoint |

`Missing a brand ..., a VW ID.` before any network traffic means `.env` was not found or not
read — check you are running from inside the repository.

## About that password

It is worth being blunt, because the alternative is implying a protection that does not exist.

`VW_PASSWORD` is **not a read-only credential**. The same account that reads your car's state also
unlocks it and locates it. There is no scoped token to use instead: this interface authenticates the
owner, not an application.

What protects it here is that `.env` is gitignored, the client never logs it and never renders it,
and the harness never writes it anywhere. That is the whole of it. Leaving `VW_PASSWORD` unset and
typing it at the prompt keeps it out of any file, at the cost of typing it each run.

[#137](https://github.com/mpospisil/gleanvolt/issues/137) accepted this trade knowingly rather than
casually, and records why: the alternative that stores no password cannot re-authenticate
unattended, and a car feed that needs a human at a web form every few hours is not a feed.

## What comes next

[#140](https://github.com/mpospisil/gleanvolt/issues/140) turns this client into a service that runs
on its own schedule, feeds the dashboard through the same `VehicleStateHolder` the MQTT feed already
writes to, and surfaces *sign-in required* as a state distinct from *stale reading* — because they
need different things from you. These variables acquire proper `Vehicle__*` configuration names at
that point; the plain `VW_*` names belong to the harness.
