# Implementation log

Reverse-chronological. Newest entry at the top.

---

## 2026-08-10 — The Windows publish job waits for a Docker daemon instead of assuming one

Follow-up to #35, whose first run on `main` failed in the `windows` job after 12 seconds:

```
failed to connect to the docker API at npipe:////./pipe/docker_engine;
check if the path is correct and if the daemon is running:
open //./pipe/docker_engine: The system cannot find the file specified.
```

Nothing to do with `Dockerfile.windows`, which was never reached. GitHub's Windows runners
intermittently start with the Docker service still stopped — an open upstream regression,
[actions/runner-images#13729](https://github.com/actions/runner-images/issues/13729), affecting
windows-2022 and 2025 on both standard and larger runners since February. The `Log in to GHCR` step
passed right before it because `docker login` talks to the registry, not the daemon, which is why the
failure looked like a build problem rather than an environment one.

The job now starts the service if it is stopped and polls the API until it answers, up to three
minutes, before doing anything else. It also asserts the daemon is in **Windows** container mode:
otherwise a Linux-mode daemon fails much later, deep in the build, complaining about the base image's
OS rather than about the runner.

**One line in that step is load-bearing and looks like noise:** `$PSNativeCommandUseErrorActionPreference = $false`.
pwsh 7.4+ turns a non-zero native exit code into a *terminating* error when
`$ErrorActionPreference` is `Stop`, so the probing `docker version` would have thrown on its first
failed attempt — the exact attempt the retry loop exists to survive. The retry would have been
decorative without it.

Also added `timeout-minutes: 60` to the job. Windows base images are large and this runner caches
them inconsistently, and the default job timeout is six hours.

**The failure did expose a real design consequence**, so it is now documented rather than discovered
twice: because the manifest job needs every platform, one flaky Windows runner holds back `:latest`
for the Pi as well. That is the intended trade-off — a `:latest` that quietly lost a platform is
worse — but the single-platform tags are pushed regardless, so the escape hatch
(`IMAGE_TAG=sha-abc1234-linux-arm64`) is in `deploy/README.md` now. The failed run confirmed it: both
Linux jobs went green and pushed `main-linux-arm64` and `sha-afef9d3-linux-arm64` before the Windows
job failed.

Files changed: `.github/workflows/publish-image.yml`, `deploy/README.md`.

---

## 2026-08-10 — Three platforms under one image name, and a timezone that is now configuration

Issue #35. The image was `linux/arm64` only. It is now `linux/arm64`, `linux/amd64` and Windows Nano
Server ltsc2022, all published under the one name `ghcr.io/mpospisil/solax-controller` as a
multi-platform manifest list — so `:1.0.0` pulls the arm64 image on the Pi and the amd64 image on an
x64 host, and `deploy.sh` needed no change at all.

**Naming was the part with an actual choice in it.** Three separate packages
(`…-linux-arm64`, `…-nanoserver`) would have meant three things to keep in version step and would
have discarded the platform selection Docker already does. One manifest list keeps a single name;
single-platform tags (`:1.0.0-linux-arm64`) still exist underneath it for pinning and for answering
"which one did it actually pull". `docker buildx imagetools inspect` lists what is behind a tag.

**The publish workflow went from one job to four:** `version` (one version string everything else
derives from), `linux` as a two-arch matrix, `windows`, and `manifest` folding the three
single-platform tags into every public name. The split is not cosmetic — the manifest job is what
keeps `:latest` pointing at the previous release until *all three* platforms of the new one exist, so
a half-finished matrix can never publish a partially-platformed release. Per-arch `scope=` on the
GHA cache was needed too; without it the two matrix legs evict each other.

**Windows needs its own Dockerfile, not a `--platform`.** A Dockerfile builds for one OS and a
Windows runtime stage can only be assembled on a Windows daemon, hence `Dockerfile.windows` on a
`windows-2022` runner with plain `docker build` — buildx has no usable Windows driver. ltsc2022 over
ltsc2025 because an ltsc2022 container runs on Server 2022 *and* 2025 hosts while ltsc2025 requires a
2025 host; Nano Server over Server Core for ~300 MB against ~2 GB. Note the `escape=` directive at
the top of that file: the default escape character is a backslash, which is also the path separator.

**The quirk this turned up is not a Modbus one for once.** `.NET` on Windows ignores the `TZ`
environment variable entirely — it reads the OS setting. The `TZ` line in `deploy/docker-compose.yml`
would therefore have done nothing, the container would have run in UTC, and since the forecast day
boundary, the daily loan-budget reset and the zone id recorded on every charging session all came
from `TimeZoneInfo.Local`, evening sessions would have been filed under the following day. Silently:
nothing logs a timezone. That is a data-correctness bug that only surfaces days later as charging
decisions nobody can explain.

So the zone became configuration. `Controller:TimeZone` resolves through `ZonedTimeProvider`, which
overrides `LocalTimeZone` on the `TimeProvider` the services already take and delegates the clock
untouched to `TimeProvider.System`. Routing it through the existing abstraction rather than adding a
second notion of "local" means all six call sites were fixed by one DI registration — except
`SolaxPollingService`, which was reaching for `TimeZoneInfo.Local` statically and now takes a
`TimeProvider` like everything else.

An unresolvable id throws at startup rather than falling back to UTC, because a quiet fallback is
precisely the failure this setting exists to prevent. Empty remains the default, so Linux behaviour
is untouched.

**Nano Server has no ICU**, which has one consequence worth knowing before deploying there: .NET
falls back to Windows NLS, so culture-aware formatting still works, but the IANA→Windows timezone
mapping does not — it is an ICU feature. `Controller__TimeZone` must be a Windows id there
(`Central Europe Standard Time`), not `Europe/Prague`. The same appsettings value is therefore not
portable between the Linux and Windows images. The worker logs a warning at startup if it finds
itself on Windows with the zone unset.

**Verified:** 319 tests pass. The `linux/amd64` image builds and starts; a bad zone id fails fast at
`ZonedTimeProvider.Resolve`. **Not verified:** the Windows build — there is no Windows daemon on the
development machine, so `Dockerfile.windows` and the `windows` job get their first real exercise on
the next push to `main`.

Also folded in from the branching discussion that prompted this work: `ci.yml` and the publish
workflow now trigger on `release/**` as well as `main`, so a release branch is gated and publishable.

Files added: `Dockerfile.windows`, `src/Solax.Worker/Configuration/ControllerOptions.cs`,
`ZonedTimeProvider.cs`, `tests/Solax.Worker.Tests/ZonedTimeProviderTests.cs`.
Files changed: `.github/workflows/publish-image.yml`, `ci.yml`, `src/Solax.Worker/Program.cs`,
`SolaxPollingService.cs`, `appsettings.json`, `README.md`, `deploy/README.md`, `docs/DECISIONS.md`.

---

## 2026-08-09 — Charging sessions are recorded to a local store

Issue #32. Every controlled charging session is now written to a SQLite file: when it started and
finished, which strategy drove it, how the delivered energy split between solar, grid and the home
battery, and a sampled trace of the whole thing.

**Where it plugs in.** `ChargeControlStatusHolder` already raised `Updated` with a full status snapshot
on every poll, and that record already carried nearly everything needed. So recording is a *subscriber*
— `SessionRecordingWorker`, alongside `HomeAssistantMqttWorker` — and `SolaxPollingService` is
untouched apart from one added field. The handler does nothing but drop the snapshot into a bounded
channel; attribution, tracking and SQLite all happen on the worker's own loop, so a slow SD card costs
latency there and nowhere else.

**The new logic is two pure classes in `Solax.Core`.** `ChargingSourceAttribution` splits the charger's
draw across solar, grid and battery — surplus PV first, then the pack, then the grid as the residual,
with the invariant that the three always add back up to the measured draw. `ChargingSessionTracker` is
the state machine: it opens a session when a controlling mode is driving a connected car, integrates
every poll into five `EnergyIntegrator` totals, stores a sample every 30 s *and* on every change, and
closes on unplug / mode-off / the car finishing / shutdown. A mode switched mid-session records an
event rather than splitting the session.

**Four charging figures per sample, not one.** Measured power, the actual current derived from it, the
charger's setpoint read back, and the current we commanded. The gaps are the point: target ≠ active
means our write didn't land, active ≠ actual means the car itself is the limiter. Nothing in the system
could tell those apart after the fact before this.

**`ChargeControlStatus.SessionCompleted` was added** because the information is otherwise destroyed:
the poll loop sets the mode back to `Off` when the fast mode ends itself, after which "the car filled
up" and "somebody selected Off" look identical downstream.

**Two things the tests caught.** `ChargingSessionDocument` originally had a convenience second
constructor, which makes it undeserializable by `System.Text.Json` — it would have failed in whatever
consumes the published object rather than here; it is a static `Create` now. And the day plan's
`NextFeasibleWindow` is a value tuple, whose members are fields, so it vanishes silently under a
default serializer configuration — `IncludeFields` is on, with a test that asserts the window survives
the round trip.

**Not done here:** the `data/` bind mount and its uid chown belong to `deploy/docker-compose.yml`, which
lives on the unmerged #26 branch. Recording works locally without it; in the container it needs that
mount or the history dies with the container.

Files added: `src/Solax.Core/Models/ChargingSession*.cs`, `ChargingSourceSplit.cs`,
`src/Solax.Core/Enums/ChargingSession*.cs`, `src/Solax.Core/Interfaces/IChargingSessionStore.cs`,
`src/Solax.Core/Strategies/ChargingSourceAttribution.cs`, `ChargingSessionTracker.cs`,
`src/Solax.Infrastructure/Sessions/*`, `src/Solax.Worker/Sessions/SessionRecordingWorker.cs`,
`src/Solax.Worker/Configuration/SessionStoreOptions.cs`, and tests for all of it.
Files changed: `ChargeControlStatus.cs`, `SolaxPollingService.cs`, `Program.cs`, `appsettings.json`,
`Solax.Infrastructure.csproj`, `README.md`, `docs/DECISIONS.md`.

---

## 2026-08-09 — Short entity names again; the explanations moved to the README

The previous entry named every entity `Label — what it means`, because HA's hover text is the friendly
name and there is no tooltip field. Seen in HA, that was the wrong trade: every card row was a
truncated sentence and the entity list stopped being scannable — a name that is really a paragraph
confuses more than it explains.

So the entity names are back to the short originals (`Grid power`, `Charging now`, `Required SOC
floor`, …), the `description` attribute and the `Describe` helper are gone from `HaDiscovery`, and the
two tests that enforced them are gone with them — `HaDiscovery.cs` and `HaDiscoveryTests.cs` are byte
for byte what they were before the change.

The explanations themselves were not lost. They are now a table in the README under *Home Assistant
(MQTT) → **What each entity means*** — one row per entity with its unit and meaning, split into the
always-present entities and the ones only the `Forecasted` mode populates. It keeps the detail that
was in the attributes: the enum states of `Control state`, `Charger status` and `Day outlook`, both
sign conventions, that the surplus is a smoothed 3-minute average, that `Active charging current` is a
read-back to compare against the target, that a working discharge hold still leaves a ~60 W trickle,
and that `Charging now` reports our own command rather than the car's draw.

Files changed: `src/Solax.Worker/HomeAssistant/HaDiscovery.cs`,
`tests/Solax.Worker.Tests/HaDiscoveryTests.cs`, `README.md`, `docs/DECISIONS.md`.

---

## 2026-08-08 — Home Assistant entities explain themselves

The entities published to Home Assistant showed their name and nothing else: hovering one gives a
tooltip identical to its title, which tells a reader nothing about what the number means or which way
its sign runs.

**Home Assistant has no description or tooltip field.** The frontend sets the hover text to the
friendly name so a truncated name can still be read in full; there is no per-entity description in
MQTT discovery, and adding one is
[an open feature request](https://community.home-assistant.io/t/add-description-for-each-entity-which-shows-in-the-gui-when-you-hover-over-the-name/622053).
The tooltip therefore cannot be changed except by changing the name — so that is what changed:

1. **Every entity is now named `Label — what it means`.** `Grid power` became `Grid power — positive
   while importing from the grid, negative while exporting`. HA truncates the label in a card and
   reveals the whole sentence on hover, which is the only tooltip mechanism it has. The label comes
   first so a truncated row is still scannable.
2. **The detail that will not fit on one line is an attribute.** `json_attributes_topic` points at the
   existing state topic and `json_attributes_template` is a **constant** — no placeholders, so every
   state message renders the same JSON. It is built with `JsonSerializer` rather than by hand so
   anything in the prose that would break the JSON is escaped; the only authoring rule is to avoid
   Jinja delimiters. It shows under Attributes in the more-info dialog and covers the enum states, the
   sign conventions, and the traps: the surplus is a smoothed 3-minute average, the active current is a
   read-back, a working discharge hold still leaves a ~60 W trickle.

A test enforces both halves — the name must be in `Label — meaning` form with a label short enough to
survive truncation, and the description must be present, valid JSON and not merely the name repeated —
so a new entity cannot be added without an explanation.

The binary sensor named `Charging now` was also a misnomer: it reports `HoldingControl`, whether *we*
are commanding a current rather than whether the car is drawing one. It now says so.

Renaming is safe for existing installs: `entity_id` is assigned when an entity is first discovered and
does not follow later name changes, so dashboards and automations keep working. A fresh install
derives its entity ids from the new names.

Files changed: `src/Solax.Worker/HomeAssistant/HaDiscovery.cs`,
`tests/Solax.Worker.Tests/HaDiscoveryTests.cs`, `README.md`, `docs/DECISIONS.md`.

---

## 2026-08-08 — Fast charge without the battery: the `FastNoBattery` mode (issue #28)

A fourth charge mode, and the first one that turns *itself* off. While it is selected the battery
discharge hold is armed automatically, the charger is pinned at `MaxChargingCurrentAmps` regardless of
sun, SOC or forecast, and when the car reaches its own charge limit the setpoint drops to the pause
current and the mode returns to `Off` — releasing the hold it armed.

### The one contract change

A controller could previously only say Charge / Pause / None. It now has a third thing to express, so
`ChargingControlDecision` gained `SessionComplete` and `ChargingControlInput` gained the two facts a
strategy needs to decide it: `EvDrewPower` and `EvIdleFor`. Both are defaulted, so the existing
controllers and their tests were untouched. The reasoning behind the completion rule — power
authoritative, `SuspendedEv`/`Finishing` corroborating, `ChargePaused` deliberately excluded because
it is what our own pause write produces — is in [DECISIONS.md](DECISIONS.md).

Cross-cycle state stays in `ChargingControlCoordinator`, next to the session-energy and loan tracking
it already owned: since when the car has been drawing nothing, and whether it ever drew at all. Both
reset on plug-in and on `ReleaseControl`, so a newly selected mode can't inherit the previous one's
verdict that the car has already charged and end itself on its first idle poll.

`FastChargingController` itself is the smallest strategy in the codebase — no smoothing, no
hysteresis, no SOC gate, because none of those inputs can change a constant setpoint.

### Ending the mode, in the right order

In `SolaxPollingService` the completion is handled *between* the charge cycle and the hold
reconciliation:

```
RunCycleAsync  -> Pause written, SessionComplete: true
_mode.Set(Off) -> mode := Off for the rest of this iteration
ApplyBatteryHoldAsync(mode: Off) -> release written on the same poll
```

Putting the mode change after the hold reconciliation would have left the inverter held for one extra
poll. Home Assistant needs nothing new: `PublishStatusAsync` already republishes the select's retained
state from `_mode.Mode` every status tick, so a controller-initiated change reaches the UI on its own.

`AutoHold` was generalised from "the forecast mode at its SOC floor" to "whatever the selected mode
wants", with `FastNoBattery` wanting it unconditionally, and now logs its automatic release as well as
its arming. The owner's manual switch is still OR-ed on top and is never released by a mode.

With `BatteryHold:Enabled` false the mode still charges and warns once on selection rather than
refusing to run — a select option that silently does nothing would be the worse failure.

### Hardware quirks and open verification

- **Nothing new is written.** The mode uses the same two write paths that already existed: the
  charger's current setpoint and the inverter's power-control command.
- **`MaxChargingCurrentAmps` becomes a supply limit.** The solar modes only reach the ceiling when the
  sun is that generous; this one sits at it for hours. On the reference install that is 16 A × 230 V ×
  3 ≈ 11 kW drawn continuously from PV and grid. Documented in both the README and the options class.
- **End-of-charge status is unverified.** No completed session has been logged through this controller
  yet, which is why the rule leans on power rather than on the charger's status enum. First live
  session should be logged end to end and the DECISIONS entry amended if the transitions differ.

### Tests

`FastChargingControllerTests` (13) covers the use-mode precondition, the clamped ceiling, indifference
to SOC and surplus, and every branch of the completion rule. `FastNoBatteryModeTests` drives the real
`SolaxPollingService` loop over a scripted telemetry sequence — a fake reader parks after the last
scripted reading, so the assertions need no timing assumptions — and checks the hold is armed with no
forecast at all, that a finished car pauses the charger, returns the mode to `Off` and releases the
hold in the same cycle, and that a hold the owner asked for survives all of it.

### Files changed

- `src/Solax.Core/Enums/ChargeControlMode.cs`, `EvChargerStatusExtensions.cs` (`IsChargeWindingDown`)
- `src/Solax.Core/Models/ChargingControlDecision.cs` (`SessionComplete`, `EvDrewPower`, `EvIdleFor`)
- `src/Solax.Core/Strategies/FastChargingController.cs` (new)
- `src/Solax.Worker/ChargingControlCoordinator.cs`, `ChargeControlStatusHolder.cs`,
  `SolaxPollingService.cs`, `Program.cs`
- `src/Solax.Worker/Configuration/ChargeControlOptions.cs`, `appsettings.json`
- `tests/Solax.Core.Tests/Strategies/FastChargingControllerTests.cs` (new),
  `tests/Solax.Core.Tests/Enums/EvChargerStatusExtensionsTests.cs`
- `tests/Solax.Worker.Tests/FastNoBatteryModeTests.cs` (new),
  `ChargingControlCoordinatorTests.cs`, `HaDiscoveryTests.cs`
- `README.md`, `docs/DECISIONS.md`

---

## 2026-08-08 — Five days of observation: #24 verified on hardware, forecast bias shape (issues #22, #24)

Read-only run 2026-08-02 09:26 → 2026-08-06 18:40, ~46,000 polls, charge mode `Off` throughout.
No code changes — this entry records what the run showed.

### The #24 reconnect fix is verified on hardware

The overnight run the #24 entry below asked for, four times over.

| Day | Polls | Failures | Transaction-ID | Rate | Worst gap |
|---|---|---|---|---|---|
| Aug 3 | 12,329 | 57 | 41 | 0.46 % | 52 s |
| Aug 4 | 11,995 | 92 | 76 | 0.76 % | 42 s |
| Aug 5 | 12,116 | 96 | 80 | 0.79 % | 43 s |
| Aug 6 (to 18:40) | 9,658 | 57 | 44 | 0.59 % | 43 s |

**Nothing ever got stuck.** Zero gaps over 60 s between successful polls across four full days, and
the failure-run distribution is almost entirely single polls:

```
Aug 3: {1 poll: 50, 2: 2, 3: 1}   Aug 5: {1 poll: 94, 2: 1}
Aug 4: {1 poll: 90, 2: 1}         Aug 6: {1 poll: 55, 2: 1}
```

Worst case in four days was three consecutive failed polls. Compare the pre-fix cliff: 43 successes,
564 errors, and zero successes for the last 50 minutes of the log. Traced end to end, one failure
costs exactly one poll:

```
16:14:24 [WRN] Failed to poll — Response was not of expected transaction ID. Expected 905, received 904.
16:14:31 [INF] SOC=99% BatteryPower=0W Solar=2093W ...
```

**But the underlying desync rate is climbing**: 0 (Jul 31) → 15 (Aug 1) → 41 → 76 → 80 → 44 per day.
The fix absorbs it at one poll each, so nothing is broken, but it is *masking* a trend rather than
addressing it. The cause is on the device or network side, not in this code. Worth its own issue if
it keeps rising.

### Forecast accuracy: totals are good, the intraday shape is not

Note the `Solar: Actual/Forecast` log line compares against **P50** (`EstimatedPowerWatts`), while the
planner uses **P10** — these numbers are median-vs-actual.

Daily energy on the three clear days: **+8.3 %, +6.5 %, +4.2 %** actual over forecast. Aug 6 (cloudy,
partial day) came in **−17.7 %**. Magnitudes are fine and err on the safe side.

The shape does not hold up. Hourly actual/forecast on clear days:

```
hour    07     08     09     10     11     12     13     14     15     16
Aug 3  0.67   0.88   0.99   1.07   1.06   1.06   1.19   1.10   1.29   1.14
Aug 4  0.68   0.86   1.01   1.09   1.11   1.12   1.12   1.12   1.11   1.10
Aug 5  0.68   0.84   0.97   1.06   1.10   1.14   1.12   1.12   1.09   1.18
```

**The 07:00 hour over-predicts by ~32 %, three days running, to within one percentage point** —
forecast ~1500 W against ~1000 W delivered. Midday is the opposite, a steady 6–14 % under-prediction.
A deficit that recurs at the same hour with the same magnitude on consecutive clear days is not
weather; it looks like morning shading the Solcast site model doesn't know about, or a wrong
azimuth/tilt in the site configuration.

**The single scalar bias cannot represent this.** The tracker whipsaws within each day — 1.00 at
start, down to 0.71–0.79 through the morning, back to 1.04–1.08 by evening — because no one number
is simultaneously +12 % and −32 %. Whichever value it holds is wrong for half the day, and applying
the evening value at 07:00 would make the over-prediction worse.

This lands where `SolarDayPlanner` is most sensitive: 1000–1500 W at 07:00 sits on the
shoulder/plateau boundary (surplus below vs above the charger's minimum power), so an optimistic
morning forecast is exactly what would open a charge window that cannot be sustained.

**Not yet a code change.** Check the Solcast site configuration first — azimuth, tilt, declared
capacity, horizon/shading if the plan offers it. If the site model is wrong, correcting it at source
beats compensating for it here. Only if the configuration is already right does per-period bias
(instead of one daily scalar) become the fix, and three clear days is thin evidence for that.

### Still not exercised

Across every log to date the charge mode never left `Off`, the battery hold was never armed, and
`ForecastedChargingController` has never run a cycle. The planner and accuracy tracker have been
validated read-only; **no feature that writes to hardware has been observed in a live run.** Every
outstanding verification item on #20 and #22 remains open for that reason.

---

## 2026-08-02 — Validation fixes after three days of observation (issue #22)

Branch: `feature/22-forecasted-charging`

Three days running in `Off` mode, ~34,000 polls. The service was stable — the #24 reconnect fix held
(22 transaction-ID errors, every one followed by successful polls) — and the forecast tracker proved
itself: Solcast p50 tracked reality within 5% all day, cumulative 50.0 kWh forecast against 51.1 kWh
actual. **p10 runs 13–16% low on this roof**, which is the insurance premium `ForecastConfidence: P10`
charges.

Four defects fell out of the logs.

### A. The house baseline ran away (the serious one)

`HouseBaselineEstimator` was one EWMA with a ~1.4 h time constant. That is slow per minute and fast
per day: it followed the diurnal curve, so by mid-afternoon it reported the afternoon peak and the
planner projected that flat across every remaining hour. Measured: **264 W at 05:00 → 2124 W at 11:00
→ 5406 W at 15:00**. The consequence, on a day whose forecast was accurate to 5%:

- 05:00 — "Window 08:00–16:30, 33.6 kWh available for the car"
- 12:00 — "No EV charging today", Plateau 0.0 kWh, window none, SOC floor 100%

Replaced by `HouseLoadProfile`: 24 hour-of-day buckets, each a slow EWMA (~3 days per bucket), seeded
from `BaselineHouseLoadWatts` until an hour has been observed. The planner now asks
`IHouseLoadProfile.ExpectedWattsAt(instant)` per forecast slice instead of taking one figure for the
whole day — which is the question it was always really asking.

### B. `ForecastToday` in the day summary was nonsense

`ForecastToday=6.6kWh ActualToday=52.7kWh (702%)`. Solcast returns only *future* periods, so by the
19:00 deadline "today's forecast" holds nothing but the evening. Both figures now come from the
accuracy tracker, which integrates them live period by period — the same numbers that were already
correct in the `Forecast check` lines.

### C. The day-plan line logged every poll

13,475 Information lines a day, 5–8 MB of log. The change-detection signature carried the budget to
0.1 kWh, which drifts continuously. Signature coarsened to whole kWh and whole percent, plus a
five-minute floor between lines — bypassed when the outlook changes or the plan becomes
usable/unusable, so the first real plan after startup is never held back. Measured after the fix: one
line per 90 s smoke run instead of eighteen.

### D. The outlook chattered

`Shortfall → Tight → Shortfall → Tight` inside three minutes, because the Tight/Shortfall boundary is
half the EV target and the day sat exactly on it. `Classify` now takes the previous outlook and
applies `OutlookHysteresisFraction` (0.05), so leaving a state needs the margin and entering it does
not.

### Also

- The forecast refresh now wakes 30 minutes *before* first light, so the day no longer starts on a
  stale forecast and the live-solar fallback (observed as an `Unknown` plan from 02:45 to 04:45).
- A once-daily `House profile:` line dumps the learned hourly shape, since that is the first thing to
  read when a day's decisions look wrong.

### Still open

**What draws ~5 kW at midday on the reference site?** The battery is full by 10:00 every day, and from
then on `OtherLoads` is ~91% of PV (r=0.69) against a 300 W night base and 47 kWh/day total. That
shape — load that appears exactly when surplus does — suggests a PV-surplus diverter. If so it is
self-fulfilling: the plan reads it as fixed house load, stands down, and leaves it the surplus it was
measuring. **This affects `Solar` mode identically** (its surplus was ~440 W at midday), so it is not
specific to the forecast strategy. Needs an answer from the site owner before the next measurement
round can be interpreted.

---

---

## 2026-08-02 — Raspberry Pi deployment: three containers, arm64 image from CI (issue #26)

Branch: `feature/26-raspberry-pi-docker-deploy`

The controller only ran from a developer machine, which contradicts the project's premise: a service
built to survive internet outages stopped when a laptop closed. This puts the whole system — the
controller, Home Assistant, and an MQTT broker — on a Raspberry Pi 3 B as three Docker containers.

### What changed

| File | |
|---|---|
| `Dockerfile` | Two stages; cross-compiles rather than emulating, runtime stage has no `RUN` |
| `.dockerignore` | Keeps `dev/`, `tests/`, and anything `*.env` out of the build context |
| `.github/workflows/publish-image.yml` | Builds `linux/arm64`, pushes to GHCR as `latest` / `sha-` / semver |
| `deploy/docker-compose.yml` | The three services, all state on bind mounts, per-service `mem_limit` |
| `deploy/deploy.sh` | tar-over-ssh + `docker compose pull && up -d`; refuses to guess if the Pi isn't prepared |
| `deploy/mosquitto/config/mosquitto.conf` | Authenticated broker, no host port, logs to stdout |
| `deploy/homeassistant/config/*.yaml` | Seed config with recorder tuning for a 1 GB board and an SD card |
| `deploy/.env.example` | Image tag, device addresses, MQTT + Solcast secrets, memory limits |
| `deploy/README.md` | Pi preparation, first run, operations, backup/restore, troubleshooting |
| `README.md`, `docs/DECISIONS.md` | Deployment section; the decision record for all of the above |
| `src/Solax.Worker/Program.cs` | Enable Serilog's `SelfLog` — the only `src/` change, see below |

The rationale for each choice is in [DECISIONS.md](DECISIONS.md).

### Rebased onto the session store (#32)

Two things main changed underneath this branch, neither of which git could flag:

- **`ChargeControl:Enabled` no longer exists.** The service boots in mode `Off` and takes control only
  when Home Assistant selects a mode, so the compose file was passing a setting that silently did
  nothing — and the deployment's safety claim rested on it. Replaced with `ChargeControl__DryRun`.
- **The SQLite session store needs a bind mount**, exactly as the #32 record predicted. `data/` is now
  mounted from `/opt/solax/data`, created in the image's build stage beside `logs/`, and covered by
  the same ownership handling — extended to a loop over both directories. SQLite makes it stricter
  than logs: `-wal` and `-shm` live beside the database, so the directory itself must be writable by
  uid 1654, and the documented backup stops the controller rather than copying a live database.

### Verified, not assumed

- **The arm64 cross-build works and is fast.** `docker buildx build --platform linux/arm64` completes
  in ~75 s from cold, with the `dotnet publish` step taking 3.5 s — proof it ran natively rather than
  under emulation. No QEMU is installed in the workflow.
- **The image runs as uid 1654 and writes where it should.** An amd64 build of the same Dockerfile
  started, logged, and created `/app/logs/solax-20260802.log` owned by `app` — which is what makes the
  bind mount over that path work once the host directory is chowned to 1654.
- **Bridge networking reaches the Modbus devices.** The smoke-test container, with no special network
  configuration, polled the live inverter and charger (`SOC=56% BatteryPower=-748W Solar=101W
  EvCharger=Available`). This was the main open question about the container topology, and it means
  host networking is not needed for either the controller or HA.
- **No log file is written inside any container.** With the logs bind mount in place, `docker diff`
  on the running controller is **completely empty** and `solax-<date>.log` appears on the host owned
  by 1654. HA logs to its own bind-mounted `/config`, and the broker only logs to stdout.
- **The way that breaks is silent, and is now guarded twice.** Given a root-owned logs directory —
  what Docker creates if the mount source is missing — the container runs happily, polls, reports
  healthy, keeps `docker diff` empty, and writes **no log file anywhere**; Serilog's file sink fails
  and the process never mentions it. Hence `Serilog.Debugging.SelfLog.Enable(Console.Error)` in
  `Program.cs` (the failure now appears in `docker logs` as `RollingFileSink: the target file could
  not be opened or created`) and directory preparation in `deploy.sh`. Both verified against the
  reproduction.
- **The compose file's required-variable guards fire.** Without `MQTT_USERNAME`/`MQTT_PASSWORD`,
  `docker compose config` exits 1 with `required variable MQTT_USERNAME is missing a value: set
  MQTT_USERNAME in .env`, rather than starting a broker nothing can authenticate against.

### Things worth knowing

- **The runtime stage deliberately contains no `RUN`.** It looks like an odd constraint until you
  notice that one `RUN mkdir` in an arm64 stage is enough to drag QEMU into the build. The logs
  directory is created in the build stage instead and `COPY --chown` does the ownership.
- **`mem_limit` is silently ignored on Raspberry Pi OS** until `cgroup_enable=memory cgroup_memory=1`
  is added to `/boot/firmware/cmdline.txt`. Easy to miss, and the failure mode is the whole board
  thrashing instead of one container being killed.
- **`deploy/` mirrors `/opt/solax` path-for-path.** An earlier version rewrote paths with `tar
  --transform` on the way over; making the two layouts identical deleted that cleverness.
- **The deploy script does no first-time setup.** Creating and chowning directories needs `sudo`, and
  a routine deploy should never be the thing that does it — so it checks, and prints the exact
  commands if something is missing.
- **HA's `.storage` is the one irreplaceable directory** (account, entity registry, MQTT integration).
  `deploy/README.md` documents the backup and the restore.

### Not done here

Hardware verification: the 72-hour soak, the reboot and power-cut tests, and the measured memory
headroom from issue #26's acceptance criteria all need the Pi itself.

## 2026-07-29 — Modbus reconnect on failure (issue #24)

Branch: `fix/24-modbus-reconnect`

A three-hour run produced 43 successful polls and 564 transaction-ID errors. Bucketed by ten minutes
it is not intermittent at all — it is a cliff:

| bucket | ok | errors |
|---|---|---|
| 18:50 | 4 | 0 |
| 19:00 | 39 | 10 |
| 19:10 | **0** | 91 |
| 19:20–20:00 | **0** | ~97 each |

### Cause

A late response left in the socket buffer puts the stream permanently one or more replies behind.
NModbus's own retries cannot escape it, and nothing tears the connection down — `IsConnected` is true
because the socket is fine. See [DECISIONS.md](DECISIONS.md).

### What changed

`ModbusTcpClient` rewritten around a single `ExecuteAsync` path: serialise on a `SemaphoreSlim`,
connect on demand, wait out `DeviceConfig.MinRequestInterval`, run the operation, and — on any failure
— invalidate the connection through an exception filter so the original exception still surfaces
unchanged. `DeviceConfig` gained `MinRequestInterval` (250 ms default).

### Things worth knowing

- **NModbus retries a mismatched response by re-sending the request**, so corrupting a single reply is
  self-healing and proves nothing. The tests had to model the *persistent* offset to reproduce the
  field failure — that discovery is baked into `FakeModbusTcpServer.Persistent`.
- **The tests need a real socket.** `FakeModbusTcpServer` implements enough MBAP framing for function
  codes 3, 4, 6 and 16, plus deliberate transaction-id corruption.
- **Verified as regression tests**: disabling only the invalidation makes 3 of the 9 fail; restoring it
  makes all 9 pass.
- **Not verified on hardware yet** — the fix wants an overnight run showing successful polls all the
  way to the end of the log. *(Done — see the 2026-08-08 entry above: four full days, ~46,000 polls,
  no gap over 60 s, worst failure run three polls.)*

### Files

`src/Solax.Infrastructure/Modbus/ModbusTcpClient.cs`, `src/Solax.Core/Models/DeviceConfig.cs`,
`tests/Solax.Infrastructure.Tests/{ModbusTcpClientTests,FakeModbusTcpServer}.cs`, docs.

---

## 2026-07-27 — Always boot in Off, with the battery free (issue #22 follow-up)

Branch: `feature/22-forecasted-charging`

The service used to seed its runtime state from configuration: `ChargeControl:Enabled` chose the boot
charge mode (`true` → Solar) and `BatteryHold:HoldAtStartup` could arm the discharge hold at startup.
Both are gone. The service now **always** starts with the charge mode `Off` and the hold `off`, and no
configuration key can change that.

### Why

A restart happens for reasons nobody chose — a crash, a power cut, a deploy — and in each case the
safe assumption is that the controller has no business acting until somebody asks it to. Seeding from
config inverted that: a machine rebooting at 3am would take control of the charger and, if
`HoldAtStartup` was set, immediately re-arm a hold that keeps the pack idle. The hold in particular is
a *command with a lifetime*, not a stored setting, so re-arming it on boot conflicts with the failsafe
that #20 was built around: stop renewing and the inverter returns to normal within `Duration`.

### Changed

- `Program.cs` seeds `ChargeControlModeSelector` with `Off` and `BatteryHoldSelector` with `false`,
  unconditionally.
- Removed `ChargeControl:Enabled` (it did nothing else) and `BatteryHold:HoldAtStartup`.
  `BatteryHold:Enabled` stays — it is a real master switch that decides whether the inverter's Modbus
  client is writable at all, and it supersedes the note in the #20 entry below about `HoldAtStartup`.
- Startup log lines now say what the state is *and* that the hardware is untouched until asked.
- README updated in four places; existing `.env` files carrying the removed keys are harmless (unbound
  configuration keys are ignored), but they no longer do anything.

---

## 2026-07-27 — Forecast-driven charge mode: `Forecasted` (issue #22)

Branch: `feature/22-forecasted-charging`

A third charge mode alongside `Off` and `Solar`, selectable at runtime from the Home Assistant select.
Where `Solar` waits for a 95 % battery and then follows the last three minutes of surplus, `Forecasted`
plans the whole remaining day from the Solcast forecast so the car can start hours earlier while the
home battery still reaches 100 % by a configured evening deadline.

### What was built

**`Solax.Core` (all pure, all unit-tested)**

- `SolarDayPlanner` — the heart of it. Slices the remaining forecast (prorating the period the plan is
  built inside), splits it into *shoulder* (surplus below the charger's minimum power) and *plateau*
  (at or above it), books the battery's need backwards from the deadline, and reports what is left as
  both an energy budget and a **deliverable** budget restricted to periods that clear the minimum
  power. Also produces the SOC floor, the next viable charge window, the shortfall and the outlook.
- `ForecastedChargingController` — decides the current from the plan. Hard stops (session ceiling,
  final guard, SOC floor, no window) bypass the dwell timers; soft reasons respect them and hold the
  session at 6 A rather than stopping inside `MinRunTime`. Grants the bounded battery loan. Delegates
  to `LiveSolarChargingController` whenever the plan is unusable.
- `ForecastAccuracyTracker` — accumulates today's actual against forecast energy per period, exposes
  the clamped bias, hands each closed period to the caller once for logging, and withdraws trust after
  a sustained breach.
- `EnergyIntegrator`, `HouseBaselineEstimator`, `SolarDayPlan`, `DayOutlook`, `ForecastConfidence`,
  `IForecastRuntimeSettings`, plus p10/p90 bands on `SolarForecastPeriod`.

**`Solax.Infrastructure`** — Solcast now parses `pv_estimate10`/`pv_estimate90` (only the median was
read before) and logs all three bands on refresh.

**`Solax.Worker`** — `DayPlanProvider` (baseline + accuracy + plan + all four log lines + day roll),
`ForecastRuntimeSettings` (the three HA-settable numbers), mode-keyed controller routing in
`ChargingControlCoordinator`, session/loan energy tracking, automatic arming of the #20 discharge hold
at the plan's SOC floor, daylight-only forecast refresh, and thirteen new HA sensors plus three number
entities.

### Things worth knowing

- **The SOC floor had to be redefined mid-implementation.** Deriving it from the energy *booked* for
  the battery made it equal the current SOC — a floor that forbids all discharge. It counts all
  remaining surplus instead; see [DECISIONS.md](DECISIONS.md).
- **`MaxLoanPowerWatts` defaults to 2500, not the 1500 the issue first proposed.** Bridging a typical
  2–3 kW surplus up to the ~4.2 kW three-phase floor needs ~2.2 kW; a 1.5 kW cap could never reach it,
  which would have made the loan silently useless.
- **The dwell timers change what a "pause" means.** Inside `MinRunTime` a soft pause holds the charger
  at 6 A instead of stopping. Five of the loan tests initially failed because of exactly this, which is
  the behaviour working as intended.
- **`ChargeControlStatus` and `ChargingControlInput` both grew.** The input gained defaulted
  parameters (plan, dwell, session energy, loaned energy) so the live-solar controller and its tests
  are untouched.
- **Nothing is persisted.** A restart loses today's totals and resets the bias to 1.0; it re-converges
  within a few forecast periods.

### Not done / open

- **Not verified against hardware.** No live day has run through this yet. `BatteryCapacityKWh` must
  be set to the real pack before the plan means anything, and the accuracy tracker should be left
  running read-only for a week (it works in every mode) to see whether p10 is systematically low for
  this roof.
- The undocumented interaction between an auto-armed hold and a manual one is resolved by OR-ing them
  (manual always wins), but has not been exercised live.
- Whether the pack curtails PV near 100 % SOC — the open question from #20 — still matters here: it
  would affect the trajectory's final approach.

### Files

`src/Solax.Core/{Enums,Models,Interfaces,Strategies}/*` (11 new, 4 changed),
`src/Solax.Infrastructure/Solcast/*` (2 changed),
`src/Solax.Worker/{Forecasting/*,Configuration/*,HomeAssistant/*,Program.cs,SolaxPollingService.cs,ChargingControlCoordinator.cs,SolarForecastRefreshWorker.cs,appsettings.json}`,
tests in all three test projects (48 new).

---

## 2026-07-27 — Battery hold verified on hardware; discharge deadband; grid-power sensor (issue #20)

Branch: `feature/20-battery-discharge-hold`

Follow-up to the entry below, which shipped the hold unverified. It has now been exercised against
the live inverter.

### What was found

The mechanism works. Arming the hold at dusk (PV ~360 W, SOC 87 %, no EV charging) moved the house
off the battery within a single poll: battery **−2846 W → −56 W**, grid **0 W → +1601 W**, solar
unchanged at ~370 W. Renewal at half the duration held it continuously with no observed lapse, and PV
was not curtailed. Full measurements are in [DECISIONS.md](DECISIONS.md).

### What changed

- **A 150 W deadband on the "hold armed but battery discharging" warning**
  (`SolaxPollingService.ResidualDischargeWatts`). A working hold still leaves a **50–65 W trickle**
  out of the battery — inverter standby draw, not load being served; it persisted while house load
  swung between 143 W and 2877 W. The warning originally fired on any negative value, so it fired
  every poll and drowned out the signal it existed to give.
- **Grid power exposed as a Home Assistant sensor** (`grid_w` in the state payload, `grid_power`
  discovery config), so the hold can be observed from HA rather than only from the log — watching
  import rise as battery discharge falls is the clearest evidence the hold is working.

### Consequences

- **Issue #20's "`BatteryPowerWatts` is never negative" acceptance criterion is not literally
  achievable** on this hardware, because of the standby trickle. The achievable guarantee is that the
  battery stops *serving house load*.
- Defaults are unchanged: `Enabled: false`, `DryRun: true`. Verification on one inverter and firmware
  says nothing about any other.

### Still unobserved

Behaviour under strong midday PV (does the battery still charge from surplus while held, and is PV
curtailed at full output), behaviour with the EV actually charging, and what the undocumented
`timeout` field does relative to `duration`.

134 unit tests pass.

---

## 2026-07-26 — Battery discharge hold (issue #20)

Branch: `feature/20-battery-discharge-hold`

A switch that stops the home battery discharging, so the EV charges from PV and grid while the
battery is still free to charge from PV surplus. Orthogonal to charge control: either can be on
without the other.

### What was built

**`Solax.Core`**

- `Enums/InverterControlRegister.cs` — the inverter's *holding* register space (a different address
  space from `InverterRegister`, which is input registers), carrying the Modbus Power Control block
  at `0x7C` and the "verify against your hardware" warning.
- `Enums/InverterPowerControlMode.cs`, `Enums/InverterPowerControlSetType.cs` — the device-level
  enums, with an explicit note on why there is no `No Discharge` value.
- `Strategies/BatteryDischargeHoldStrategy.cs` — pure, stateless computation of the `active_power`
  target: `-min(house load, PV)`.
- `Interfaces/IBatteryDischargeControl.cs`, `Interfaces/IBatteryHoldSelector.cs`,
  `Models/BatteryHoldState.cs`.
- `Models/EnergyState.HouseLoadPowerWatts` — total house load *including* the EV charger
  (`PV + Grid − Battery`), as distinct from the existing `OtherLoadsPowerWatts` residual, which
  excludes it. The hold needs the EV counted as load the grid may cover.

**`Solax.Infrastructure`**

- `RegisterMaps/PowerControlPayload.cs` — pure encoder for the 13-register block, 32-bit fields low
  word first.
- `BatteryDischargeControl.cs` — the write path on the keyed inverter client. Writes on arm, release,
  retarget beyond the threshold, and renewal; nothing in a steady state.

**`Solax.Worker`**

- `BatteryHoldSelector`, `Configuration/BatteryHoldOptions`, poll-loop reconciliation, and a Home
  Assistant `switch` plus **Battery power** and **Battery hold target** sensors.

### Architecture decisions

The central one — that the inverter has no `No Discharge` mode, so the hold is a computed
`Enabled Power Control` target rather than the fire-and-forget switch the issue described — is
recorded in [DECISIONS.md](DECISIONS.md) along with what it costs.

Two smaller ones worth noting here:

- **`BatteryHold:Enabled` is a real master switch, not a boot default.** `ChargeControl:Enabled`
  only seeds the mode, because Home Assistant can select `Solar` at runtime afterwards. That pattern
  can't hold here: issue #20 requires that `Enabled: false` performs no inverter writes *at all*, and
  a runtime-settable switch would break it. So while the flag is off the feature is entirely inert —
  no HA switch is published, the poll loop skips it, and the inverter's Modbus client is wrapped
  read-only. `HoldAtStartup` covers the boot value of the hold itself.
- **`WriteProofInDryRun` became `WriteProof`, taking an explicit `writable` flag.** It previously
  gated *both* device clients on `ChargeControl:DryRun`, which would have left the inverter writable
  whenever charge control was live — regardless of the battery-hold settings. Each device now derives
  writability from the feature that actually writes to it.

### Hardware quirks and edge cases

- **Holding register `0x7C` is overloaded**: written it is the power-control command, read it is the
  ARM firmware version. There is no read-back of the active command, so the HA switch reports what we
  last successfully wrote. A failed write therefore shows in HA as the switch returning to OFF rather
  than as an assumed success.
- **Nothing verified on hardware yet.** The register map comes from the upstream integration, not a
  SolaX document, and issue #20's Phase 0 observations are still outstanding. Hence
  `Enabled: false` + `DryRun: true` defaults. *(Superseded — see the 2026-07-27 entry above: the hold
  was subsequently verified on the reference inverter. The defaults stand regardless.)*
- **The compensating check for the missing read-back**: if the battery is discharging while we
  believe the hold is armed (and we are not in dry-run), the poll loop logs a warning. It is the only
  observable signal that the command isn't taking effect on this firmware. *(A 150 W deadband was
  added on 2026-07-27; a working hold still trickles 50–65 W.)*
- **Clock going backwards** (NTP step, telemetry timestamp jitter) is treated as "renewal due" rather
  than deferring renewal indefinitely.
- A failed write is not recorded as armed, so the next poll retries instead of reporting a hold that
  was never established.

### Verification performed

- 131 unit tests pass, covering the payload encoding field by field, arm/release/retarget/renew/
  no-change, duration clamping, dry-run, failed-write retry, the target strategy, the selector, and
  the HA discovery configs and state payload.
- Smoke-run against the live inverter with `Enabled: true` + `DryRun: true`, confirming the encoded
  block is logged (`registers [1,1,0,0,0,0,60,0,0,0,0,0,0] at 0x7C`) and that no write reaches the
  device — the `ReadOnlyModbusClient` tripwire warning never fires.
