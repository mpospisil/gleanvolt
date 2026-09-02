# Reading your car from the VW Group EU Data Act portal

How to get the four settings the **Vehicle portal** page needs, and what to do when it does not work.

> **Two things, in order.** Steps 1–6 below get the **Vehicle portal** page showing your car when you
> press its button — which is how you prove the credentials and that this car's fields are understood.
> [Step 7](#step-7--switch-the-feed-on) then switches on the *feed*: the same portal read on the
> controller's own clock, feeding the dashboard's vehicle card and a Home Assistant entity. The feed is
> off until you say otherwise, and having credentials is not saying otherwise. The design and the
> reasoning are in [#137](https://github.com/mpospisil/gleanvolt/issues/137) and
> [#140](https://github.com/mpospisil/gleanvolt/issues/140).

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
  couple of days — before the first dataset appears. Until then the page reports `NoDataAvailable` and
  the feed stays `Degraded`, which is correct and not a bug.
- **Fifteen minutes is the floor.** The portal is a batch delivery, not a live API. Polling more
  often than the request's own frequency achieves nothing whatsoever.
- **A delivery is not a snapshot of the whole car.** These are *partial* deliveries: each carries the
  reports that changed, so one may hold the doors, the climate and the settings and no battery at all.
  The controller therefore merges the newest few — see [step 7](#step-7--switch-the-feed-on) — and
  nothing you configure here changes that.

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

A misspelt brand does not fail as a missing one — the page names it and lists what it knows:

```
Missing a known brand -- 'vollkswagen' is not one of vw, audi, skoda, seat, cupra, bentley;
set an explicit client id if yours is missing.
```

</details>

## Step 4 — Your VIN, if you need it

Only when the account can see more than one vehicle. With a single car, leave `VW_VIN` unset and it
is taken automatically.

You do not have to hunt for it: press the button without it and, if there is a choice to be made, it
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

Each press signs in afresh. That is deliberate for a button: it is fine by hand, and repeatedly
replaying a password at a real identity provider is how accounts get locked — which is also why nothing
on this page loops on failure, and why the feed in step 7 holds a session instead.

**The reading from this page is not fed to anything.** It proves the credentials and the mapping, and
nothing more: the dashboard's car comes from whatever feed is configured, and no charging decision sees
any of it.

## Step 7 — Switch the feed on

Once the button works, this is what makes it a feed:

```bash
VW_ENABLED=true
```

(`Vehicle__DataAct__Enabled=true` is the sectioned form, and wins where both are set.) Restart the
controller. From then on it reads the portal **every fifteen minutes** — the portal's own delivery
frequency, and not a setting: asking faster achieves nothing at all — and writes what it finds into the
dashboard's vehicle card.

It **holds one session** rather than signing in on every read, and signs in again only when the portal
bounces it. It also times each session and logs how long it lasted, which is a question nobody could
answer before: nothing had ever kept one.

Each read takes the newest delivery and, **only if that one carries no state of charge**, merges the
one before it, and so on up to four (an hour of a fifteen-minute request). That is not belt-and-braces:
the reference car's newest delivery really did arrive with the odometer, the charge target, the doors
and the climate in it and no battery reading at all. A delivery that does carry the battery costs
exactly one download. A reading assembled this way is dated by the newest report that actually
contributed to it, so a state of charge from forty minutes ago is shown as forty minutes old rather
than as fresh.

> **This stops the MQTT vehicle feed being subscribed.** Two sources writing one reading is a race, so
> the manufacturer's service takes it and the controller says so in its startup log. If a Home
> Assistant automation was publishing to the vehicle topic, it is now doing nothing — stop it, or leave
> it and know which one is live.

### When it needs you

Two states, and they are deliberately not alike:

| The card says | What happened | What to do |
|---|---|---|
| a reading, marked **stale** | the feed is trying and not currently succeeding — a 5xx, a timeout, a delivery not filled yet | nothing; it backs off and clears itself |
| **Sign-in required** | a refused password, a consent screen, an OTP, a CAPTCHA, or no continuous data request | the sentence says which; clear it in a browser, press the button on the page to check, then restart the controller |

**Sign-in required stops the feed asking.** That is the point rather than a limitation: a password
replayed at an identity provider on a clock is how accounts get locked, and a consent screen polled
forever is answered by nobody.

You will not miss it. While it lasts, the web UI carries a band across the top of **every** page with
the reason and a link to this portal page, the **Health** page shows it as a `Car feed` row, and the
controller logs it as a **warning** every six hours — a log line, never another request. The
**Car feed** entity carries the same three states (`Ok`, `Degraded`, `NeedsOwner`) with the sentence as
a `reason` attribute, which is what a Home Assistant notification keys off.

### If it asks for a code from your email

It is treated exactly as the consent screen is: `OwnerActionRequired`, reported, and **never retried**.
There is no way around this and there is not meant to be — a one-time code is a person's mailbox, and a
client that kept trying would only be replaying your password at VW's identity provider on a schedule.

The code page is recognised before anything is posted, two ways: by the words on it (`verification
code`, `security code`, `one-time password`, `Einmalcode`) and by a field asking for one (`otp`,
`emailOtp`, `securityCode` and their like). The second is what still works on a page in a language the
first does not cover, and it is also what stops the client mistaking a code page for a login page when
the page happens to carry your address in a hidden field.

Enter the code once in a browser, press **Read the car now** to confirm the sign-in works, and restart
the controller. On the reference account this has not come up — six cold sign-ins with only an email
and a password — and holding one session rather than signing in ninety-six times a day is partly there
to keep new-device challenges rare.

### About the unrecognised fields

Expect that list to be non-empty on a first run, and read it rather than ignoring it.
[`VwGroupFieldNames`](../src/Gleanvolt.Infrastructure/Vehicles/VwGroup/VwGroupFieldNames.cs) was
written from a *description* of the portal rather than from a capture, so if the battery or range
above is blank, its real name is almost certainly in that list. Add the names it reports to that file
and the mapping stops guessing.

## When it does not work

The page names the **kind** of failure and whether retrying could help, and the feed turns the same
kinds into either a backoff or a full stop. The kinds are deliberate: a consent screen and an expired
session need opposite responses, and a client that cannot tell them apart either hammers a screen it
can never answer, or gives up on something a re-login would fix.

| Reported | What it means | What to do |
|---|---|---|
| `NotConfigured` | Something is missing before anything was attempted | It names which of the three: brand, VW ID, password |
| `SignInRejected` | Credentials replayed and refused — or the form was not the shape the client expects | Check the password. If it is right, VW has changed the sign-in form and the parser needs updating. **Do not loop**: repeated failures risk the account |
| `OwnerActionRequired` | Consent, terms, an OTP or a CAPTCHA — a screen only a person can answer | Open the portal in a browser and clear it. Never retried by design |
| `VehicleNotFound` | No vehicle, or not the one asked for | Link the car in the portal, or set `VW_VIN` from the list it prints |
| `NoDataAvailable` | Signed in, but there is nothing to download | Step 2. Either no continuous data request exists, or it exists and has not been filled yet — the message distinguishes the two |
| `UnusableData` | The bundle arrived and could not be believed | The message says which field, and the page lists the names nothing here reads. Present-but-unusable is rejected whole, on purpose |
| `SessionExpired` | A 401, a bounce to `/login`, or HTML where JSON was expected | Ordinary. Press again; the feed signs itself back in |
| `Transient` | A 5xx, a timeout, a dropped connection — or a **429**, the portal rate-limiting the delivery endpoint | Wait. A 429 is provoked by asking for many deliveries at once, so it clears on its own and a lower `VW_MAX_DATASETS` prevents it. Nothing needs changing, and the message quotes the portal's own `Retry-After` when it sends one |

`Missing a brand ..., a VW ID.` before any network traffic means `.env` was not found or not
read — check you are running from inside the repository. With the feed switched on, the same sentence
appears as **sign-in required** on the dashboard rather than as a failed press.

## About that password

It is worth being blunt, because the alternative is implying a protection that does not exist.

`VW_PASSWORD` is **not a read-only credential**. The same account that reads your car's state also
unlocks it and locates it. There is no scoped token to use instead: this interface authenticates the
owner, not an application.

What protects it here is that `.env` is gitignored, and the client never logs it, never renders it and
never writes it anywhere. That is the whole of it, and it is worth saying plainly rather than dressing
up: an untracked file is what a LAN appliance has to offer.

[#137](https://github.com/mpospisil/gleanvolt/issues/137) accepted this trade knowingly rather than
casually, and records why: the alternative that stores no password cannot re-authenticate
unattended, and a car feed that needs a human at a web form every few hours is not a feed.

## What comes next

Not this: what #140 described is what step 7 now does. The feed runs on its own schedule, writes
through the same `VehicleStateHolder` the MQTT feed writes to, and shows *sign-in required* as a state
distinct from *stale reading*.

What is still open is everything a **second** manufacturer would settle. The contract behind this
(`IVehicleUpdateService`) is deliberately small — no credential abstraction, no capability taxonomy, no
auth-model enum — because one implementation cannot tell you what a second one wants. A car that has to
be woken up to answer, and whose polling costs its owner range, is the argument that will shape it.
