# Deprecate the built-in car feeds, and ship the car plugins: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task by task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Step 5 of the car plugins design (#476).
- Gleanvolt ships the three car plugins from gleanvolt-cars as compose services, handing each the built-in's
  non-secret settings.
- It labels the built-in volkswagen.de, MyŠkoda and Data Act feeds deprecated on the Car page and in the log.
- Feed selection stays as it is.

**Architecture:**
- **Compose services.** `gleanvolt-plugin-{volkswagen,skoda,vwgroup}` under the profiles of the same names, built as
  the charger plugins are: host network, `plugins/<name>` as `/data`, their own image tag variables.
  `deploy/_lib.sh` learns them as car plugins, and at most one runs.
- **Carried settings.** Each service gets the built-in's non-secret `.env` values as the plugin's settings. A value
  saved on the plugin's page wins.
  - The Volkswagen plugin reads no username or VIN from configuration today. It learns to (Task 1), and that ships as
    volkswagen 1.0.1.
- **One mapping in Core.** `VehicleFeeds.Plugin(feed)` names each built-in's replacement. It feeds:
  - the Car page's deprecation notice, which also lists the values to copy;
  - the option labels;
  - one startup warning.

**Tech stack:** .NET 10, Blazor Server, bUnit, xUnit, Docker Compose, bash. The manual is Astro, with screenshots
from `tests/Gleanvolt.Web.Screenshots`.

**Spec:** `docs/superpowers/specs/2026-11-28-car-plugins-design.md`, section 4 ("Deprecation release") and section 5
(docs).

## Global constraints

- **Two repositories, two PRs, each branched from `origin/main`:**
  - gleanvolt-cars `volkswagen-config` (Task 1);
  - gleanvolt `car-plugins-deprecation` (Tasks 2–4).

  The gleanvolt PR also carries the step 4 plan (`docs/superpowers/plans/2026-11-28-skoda-and-data-act-plugins.md`)
  and this one.
- **Releases need the owner's explicit yes, each one:** `volkswagen-v1.0.1`, and the Gleanvolt release that ships
  this (Task 5). The three plugins are already released at 1.0.0.
- **Identities:**

  | Plugin | Profile, folder | Image | Port | Port variable | Tag variable |
  |---|---|---|---|---|---|
  | Volkswagen | `volkswagen` | `gleanvolt-plugin-volkswagen` | 8095 | `VOLKSWAGEN_PORT` | `VOLKSWAGEN_PLUGIN_TAG` |
  | MyŠkoda | `skoda` | `gleanvolt-plugin-skoda` | 8101 | `SKODA_PORT` | `SKODA_PLUGIN_TAG` |
  | VW Group Data Act | `vwgroup` | `gleanvolt-plugin-vwgroup` | 8102 | `VWGROUP_PORT` | `VWGROUP_PLUGIN_TAG` |

- **Carried values. Passwords and the Škoda key never are.**
  - volkswagen.de: `Volkswagen__Username: ${VW_WEBSITE_USERNAME:-}` and `Volkswagen__Vin: ${VW_WEBSITE_VIN:-}`.
  - MyŠkoda: `Skoda__Vin: ${SKODA_VIN:-}`.
  - Data Act: `VwGroup__Username: ${VW_USERNAME:-}`, `VwGroup__Brand: ${VW_BRAND:-}`,
    `VwGroup__ClientId: ${VW_CLIENT_ID:-}` and `VwGroup__Vin: ${VW_VIN:-}`.
- **Feed selection is unchanged.** This release only adds the plugins, the labels and the warning.
- **Words.**
  - Owners see "plugin" and "deprecated".
  - Every new UI string goes in `CarText.resx` and `CarText.cs.resx`, in plain, correct wording.
  - In Czech, mode names stay English, and import/export stay "import"/"export".
- **Docs.**
  - UI changes update the manual's en and cs pages and regenerate their screenshots; `npm run screenshots:check`
    passes before the PR.
  - `docs/DECISIONS.md` and `docs/IMPLEMENTATION_LOG.md` get entries before `gh pr create`.
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; PR bodies with the Claude Code line.

## Review focus

1. **A secret in `.env`** (`VW_WEBSITE_PASSWORD`, `VW_PASSWORD`) never reaches a plugin's environment. The check is
   in Task 2.
2. **A built-in set up only on the Car page**, so its values sit in the overrides file and not in `.env`: compose
   can't carry them, and the Car page's notice lists them to copy. The test is in Task 3.
3. **An owner already on "A plugin"** sees no deprecation notice, and the log gives no warning. The test is in Task 3.
4. **Two car plugins in `GLEANVOLT_PLUGINS`**: the deploy script refuses, naming both. A car plugin beside a charger
   plugin is fine. The check is in Task 2.
5. **The Pi's hand-run `gleanvolt-volkswagen`** uses port 8095 too. Nothing in this release starts the compose
   `volkswagen` profile on its own, and the docs say to stop the hand-run one first. The text is in Task 2.

---

## Part A: gleanvolt-cars

### Task 1: The Volkswagen plugin takes its username and VIN from configuration

**Files:** `volkswagen/src/Gleanvolt.Plugin.Volkswagen/VolkswagenAccount.cs` (`AccountStore`), its page if it
prefills, `volkswagen/README.md` (configuration table), and the tests in `volkswagen/tests/…/`.

**Interfaces:**
- `AccountStore(IProviderSecretStore files, IConfiguration configuration)`. Change the constructor; DI supplies the
  configuration.
- `Current` falls back to `Volkswagen:Username` and `Volkswagen:Vin` for each field the saved `account.json` lacks.
  The password only ever comes from the file.
- The page's fields show the values in force. Saving writes all three to the file, which then wins.

- [ ] **Step 1: Write the failing tests.**
  - `TheUsernameAndVinComeFromConfigurationUntilTheAccountIsSaved`: no file and both keys configured gives
    `Current.Username`/`Vin` from configuration and `IsComplete == false`. A `Save(u, p, v)` gives the saved values.
  - `AnAccountSavedBeforeIsKept`: an existing `account.json` (the Pi's volume) wins over configuration.
- [ ] **Step 2: Run them.** Expected: FAIL to compile.
- [ ] **Step 3: Implement.** Run `dotnet test volkswagen/Gleanvolt.Plugin.Volkswagen.slnx`. Expected: PASS. Then the
  whole repository solution.
- [ ] **Step 4: Commit, push and open the PR,** "Volkswagen: the username and VIN from configuration (gleanvolt#476)".
  After the merge, ask the owner before tagging `volkswagen-v1.0.1`.

## Part B: gleanvolt

### Task 2: The car plugins in compose and the deploy scripts

**Files:**
- `deploy/docker-compose.yml`: three services after the charger plugins, each with a comment as the charger ones
  have.
- `deploy/docker-compose.nas.yml`: the same three, with `depends_on: gleanvolt-init`. `gleanvolt-init` mounts
  `plugins/{volkswagen,skoda,vwgroup}`.
- `deploy/_lib.sh`:
  - `CAR_PLUGINS="volkswagen skoda vwgroup"`, with `KNOWN_PLUGINS` including them;
  - two car plugins are refused, mirroring the charger check.
- `.env.example`: a car plugins block naming the profiles and the carried values. `deploy/README.md`: a car plugins
  section under "Shipped plugins".

- [ ] **Step 1: Write the services and the script change.**
- [ ] **Step 2: Verify the rendering.**
  - Run `docker compose -f deploy/docker-compose.yml --profile volkswagen --profile skoda --profile vwgroup config`
    with an env file setting `VW_WEBSITE_USERNAME=u`, `VW_WEBSITE_VIN=V1`, `VW_WEBSITE_PASSWORD=secret`,
    `SKODA_VIN=V2`, `VW_USERNAME=u2`, `VW_BRAND=cupra`, `VW_CLIENT_ID=c`, `VW_VIN=V3` and `VW_PASSWORD=secret2`.
    Expected: each plugin service has exactly its carried values, and `secret` appears only in
    `gleanvolt-controller`.
  - The same with `-f deploy/docker-compose.nas.yml`.
  - Run `bash -n deploy/_lib.sh deploy/deploy.sh deploy/deploy-controller-only.sh`. Expected: no output.
  - Run `_lib.sh`'s plugin check with `GLEANVOLT_PLUGINS=volkswagen,skoda`, stubbing `env_value`. Expected: the
    error names both. With `solax,volkswagen` it passes.
- [ ] **Step 3: Commit.** "Ship the car plugins in compose, with the built-ins' non-secret settings (#476)".

### Task 3: The built-ins labelled deprecated

**Files:**
- `src/Gleanvolt.Core/Models/EvSettings.cs`: `VehicleFeeds.Plugin`.
- `src/Gleanvolt.Web/Components/CarEditorForm.razor`: the option labels and the notice.
- `src/Gleanvolt.Web/Resources/CarText{,.cs}.resx`.
- The startup warning, where `ConfiguredVehicleFeed` is built or in `VehicleUpdateWorker`'s start, whichever runs
  once with the configured feed.
- Tests: `tests/Gleanvolt.Core.Tests`, `tests/Gleanvolt.Web.Tests/CarEditorFormTests.cs` and
  `tests/Gleanvolt.Hosting.Tests/VehicleUpdateWorkerTests.cs` (or `VehicleFeedRegistrationTests.cs`).

**Interfaces:**
- `public sealed record VehicleFeedPlugin(string Id, string Name, IReadOnlyList<string> CarriedKeys);`
- `public static VehicleFeedPlugin? VehicleFeeds.Plugin(VehicleFeed feed)`. It is null for `ChargeOnly`, `OwnTopic`
  and `External`.
  - `Volkswagen` → `("volkswagen", "Volkswagen", [WebsiteUsername, WebsiteVin])`.
  - `Skoda` → `("skoda", "MyŠkoda", [SkodaVin])`.
  - `DataAct` → `("vwgroup", "VW Group Data Act", [DataActUsername, DataActBrand, DataActClientId, DataActVin])`.
- **On the page:**
  - A built-in's option reads "<name> (deprecated)".
  - With a built-in chosen, a notice `id="car-feed-deprecated"` says: this built-in feed will be removed in the next
    release; run the <Name> plugin and choose **A plugin**. It links to the manual's car page and lists each carried
    key's current value, labelled as the form labels it and never a password, under "Enter these on the plugin's
    page".
- **The startup warning,** once:
  "The built-in {feed} car feed is deprecated and goes in the next release: run the {name} plugin from gleanvolt-cars
  and choose 'A plugin' on the Car page. Its username and VIN carry over from .env; the password doesn't."

- [ ] **Step 1: Write the failing tests.**
  - `EveryBuiltInNamesItsPluginAndNoPasswordIsCarried`, in Core: no carried key is a secret
    (`EvSettingKeys.IsSecret`).
  - `ABuiltInFeedIsLabelledDeprecatedWithItsPluginAndTheValuesToCopy`, in Web: the notice holds the VIN and the
    username, not the password.
  - `ThePluginChoiceShowsNoDeprecationNotice`, in Web.
  - `TheBuiltInFeedWarnsOnceAtStartup` and `AnExternalFeedDoesNotWarn`, in Hosting.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement,** with the texts in both resx files.
- [ ] **Step 4: Run the full solution's tests in Release.** Expected: PASS. Use `DOTNET_USE_POLLING_FILE_WATCHER=true`
  for the inotify limit.
- [ ] **Step 5: Commit.** "The built-in car feeds are deprecated: the Car page and the log name the plugin (#476)".

### Task 4: The manual, the notes, and the PR

**Files:**
- `docs/manual/src/content/docs/{en,cs}/setting-up/car.mdx`:
  - "Through another program" becomes the first tab, as "A plugin", listing the three plugins with their profiles;
  - the built-in tabs get a deprecation note.
- `…/setting-up/plugins.mdx`: the car plugins in its list.
- The regenerated screenshots (`npm run screenshots` in `docs/manual`). `docs/DECISIONS.md`.
  `docs/IMPLEMENTATION_LOG.md`.

- [ ] **Step 1: Edit the pages, and regenerate the screenshots.** Run `npm run screenshots:check`. Expected: no
  differences.
- [ ] **Step 2: Write the DECISIONS record** (carry-over by compose; Volkswagen 1.0.1 for config; feed selection
  unchanged) and the IMPLEMENTATION_LOG entry.
- [ ] **Step 3: Commit, push and open the PR,** "Ship the car plugins, and deprecate the built-in car feeds (#476)",
  from the log entry.

### Task 5: Releases, with the owner's yes

- [ ] `volkswagen-v1.0.1` in gleanvolt-cars, after Task 1's merge.
- [ ] The Gleanvolt release, after Task 4's merge. The version is the owner's call; the spec says "next minor".
  Wait for `publish-image.yml` before any deploy.
