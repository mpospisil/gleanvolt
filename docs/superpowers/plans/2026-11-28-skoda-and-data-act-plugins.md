# MyŠkoda and the Data Act portal as car plugins: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task by task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Step 4 of the car plugins design (#475). Port Gleanvolt's built-in MyŠkoda feed and VW Group Data Act portal
feed into gleanvolt-cars as car plugins on `Gleanvolt.Cars.Hosting`, rule for rule, with their tests.

**Architecture:**
- Each feed's clients, parsers and sign-in move over as they are. Only Gleanvolt's own types are swapped:
  - `VehicleState` becomes the contract's `VehicleReading`;
  - `Phrase.Of(...)` becomes a `PageText` resx text through `Words.From`;
  - `ISecretStore` becomes `IProviderSecretStore`;
  - `VehicleSourceHealth` becomes the car layer's `Healthy`/`Problem`/`Block`.
- Each feed's update service becomes one `PluginCar` subclass. It overrides `NextDelay` with the feed's own clock (the
  Škoda quota and backoff; Data Act's fifteen-minute batches and backoff).
- The account moves from Gleanvolt's Car page to the plugin's page.
- Non-secret settings are `PluginSettings`, so configuration (and step 5's compose carry-over) reaches them:
  - Škoda: `Skoda:Vin`;
  - Data Act: `VwGroup:Username`, `VwGroup:Brand`, `VwGroup:ClientId`, `VwGroup:Vin`.

  Secrets stay on the page.

**Tech stack:** .NET 10, Blazor Server, xUnit, `Gleanvolt.Cars.Hosting` and `Gleanvolt.Providers.Testing`
1.3.3-preview.6.

**Spec:** `docs/superpowers/specs/2026-11-28-car-plugins-design.md`, section 3, and the decided carry-over in
section 4.

## Global constraints

- The work happens in `~/git/gleanvolt-cars`, as two PRs, each on a branch from `origin/main`:
  - `skoda` (Tasks 1–2);
  - `vwgroup` (Tasks 3–5), started after the Škoda PR is merged.

  No release and no tag: releasing is step 5, with the owner's yes.
- **Identities:**
  - Škoda: id `skoda`, name `MyŠkoda`, section `Skoda`, port 8101.
  - Data Act: id `vwgroup`, name `VW Group Data Act`, section `VwGroup`, port 8102.
- **Secrets:**
  - the Škoda key goes in `api-key.json`;
  - the Data Act password goes in `password.json`;
  - the Data Act session cookies are kept in memory only, as the built-in did; it never persisted them.

  All of them go through `IProviderSecretStore`, are never logged, and are never shown back.
- **Rules are kept, not improved.** Each port keeps its built-in's numbers and choices. Any departure is a ruling in
  the ledger, with the reason the plugin needs it.
  - Škoda: 20 requests an hour shared, `ReservedForAsks` 3, a 1-minute minimum spacing, a 30-minute backoff when the
    car declines, `Retry-After` honoured, and backoff doubling from the idle interval up to an hour.
  - Data Act: a 15-minute interval, backoff up to an hour, a full stop on a refused password, consent, OTP or missing
    data request, and the portal session held for the process's life.
- **Status codes:**

  | Situation | Code | How |
  |---|---|---|
  | No key, no account, no VIN | `not-set-up` | `Block` |
  | Key or password refused, VIN unknown to the service | `refused` | `Block` |
  | Consent, OTP or CAPTCHA wanted, or the portal has no data request | `needs-sign-in` | `Block` |
  | Quota or 429 | `rate-limited` | `Problem` |
  | Car declining, network failure, 5xx | `unreachable` | `Problem` |
  | An answer that can't be read | `service-changed` | `Problem` |

  A block is lifted by the owner's action on the page (a new key, a saved account, Sign in or Try again), which calls
  `Resume()`.
- Texts in `Resources/PageText.resx` and `.cs.resx`, Czech that a Czech reader finds natural. Owners see "plugin".
- `TreatWarningsAsErrors`. Tests need no network: fakes through `HttpMessageHandler` and the fixtures moved from
  `tests/Gleanvolt.Infrastructure.Tests/Fixtures/{Skoda,VwGroup}` (already sanitised and already public).
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; PR bodies with the Claude Code line.
- This plan is committed to the gleanvolt repository with step 5's PR, since step 4 changes nothing there.

## Review focus

1. **The quota is shared with the owner's own asks** (Škoda): the clock must stop with 3 requests left until the
   window resets, while Read now and Gleanvolt's *Ask the car* may still spend them. The test is in Task 2.
2. **A key pasted after a refusal** is tried at once. The refusal blocks only for the key it was refused with. The test
   is in Task 2.
3. **Data Act signs in on its own only when the portal bounces the session,** never on a clock, and a refusal stops it
   completely. The test is in Task 4.
4. **The Data Act diagnostics** (Check now: what the delivery held, which field names matched and which nothing knows)
   work with the feed blocked, and never change the feed's clock. The test is in Task 5.
5. **Settings from configuration** (`Skoda:Vin`, `VwGroup:Username`, `Brand`, `ClientId`, `Vin`) start the plugin
   without the page, apart from the secret. A value saved on the page wins. The tests are in Tasks 2 and 4.

---

## Part 1: MyŠkoda (PR on branch `skoda`)

### Task 1: The Škoda client, parser and key store, moved

**Files (in `skoda/`):**
- `Gleanvolt.Plugin.Skoda.slnx` and `README.md`.
- `src/Gleanvolt.Plugin.Skoda/`: csproj (as volkswagen's, `UserSecretsId` `gleanvolt-plugin-skoda`),
  `Properties/launchSettings.json` (8101), `Resources/PageText{,.cs}.resx`, `Say.cs`.
- Moved from `src/Gleanvolt.Infrastructure/Vehicles/Skoda/`, namespace `Gleanvolt.Plugin.Skoda`:
  - `SkodaApiClient.cs` (unchanged apart from the namespace);
  - `SkodaVehicleResponse.cs` (`Parse` returns `VehicleReading`, keeping its mapping);
  - `SkodaApiKeyStore.cs` (on `IProviderSecretStore`, file `api-key.json`; `Version`, `Refresh` and `Clear` kept);
  - `SkodaApiOptions.cs` (`BaseUrl` and `Timeout` only).
- `tests/Gleanvolt.Plugin.Skoda.Tests/`: csproj (fixtures copied to output), `Fixtures/Skoda/*` (moved), and
  `SkodaFixtures.cs` and `SkodaVehicleResponseTests.cs` (moved, asserting on `VehicleReading`).
- Root `Gleanvolt.Cars.slnx`, `plugins.json` (`skoda`, `Skoda`, `Skoda`, 8101), and the CI build and image matrices.

- [ ] **Step 1: Move the response tests and the fixtures.** Run them. Expected: FAIL to compile.
- [ ] **Step 2: Move the four files and swap the types.** Run the tests. Expected: all 9 response tests pass.
- [ ] **Step 3: Commit.** "MyŠkoda: the client, the parser and the key store (gleanvolt#475)".

### Task 2: `SkodaCar`, the key on the page, and the plugin

**Files:**
- `src/Gleanvolt.Plugin.Skoda/SkodaCar.cs`: the update service's rules on `PluginCar`.
- `SkodaKey.cs`: the old `SkodaApiSignIn`'s check of a pasted key (one `GET ?include=info`, stored only when it
  passes, with a sentence for each refusal).
- `Program.cs` and `Pages/{Home,_Imports}.razor`.
- Tests: `SkodaCarTests.cs`, `SkodaKeyTests.cs`, `MappingTests.cs` (the readings through `VehicleTestHub`).

**Interfaces:**
- `PluginInfo("skoda", "MyŠkoda", "Skoda", Port: 8101)`.
- `public sealed class SkodaCar(CarPollOptions options, TimeProvider time, SkodaApiKeyStore keys, SkodaApiClient client, PluginSettings settings, ILogger<SkodaCar> logger) : PluginCar(options, time)`.
  - `FetchAsync`: the update service's `FetchAsync`, outcome for outcome, using the status codes above.
    - The quota-used-up and `Retry-After` paths return `LastReading` after setting `Problem(rate-limited)`.
    - The minimum spacing is the service's own 1 minute: inside the base's 30 s, return `LastReading` without a request.
  - `public override TimeSpan NextDelay`: infinite while blocked; otherwise the longest of `base.NextDelay`, the
    failure backoff, `Retry-After` / car-declined, and the quota reset when `Remaining <= 3`.
  - A block for a refused key holds only while `keys.Version` is the one it was refused at. `keys.Changed`, from a
    new key or one cleared, calls `Resume()`. So does a VIN saved on the page.
  - Key expiry within 7 days is logged once a day and shown on the page. Gleanvolt's status stays healthy: the car
    layer has no warning status (ruling, with an SDK follow-up).
- `public sealed class SkodaKey(SkodaApiKeyStore keys, SkodaApiClient client, PluginSettings settings)`, with
  `Task<Words> SubmitAsync(string key, CancellationToken)` and `void Forget()`.
- VIN: `settings.Get("vin")` (`Skoda:Vin`, or saved on the page).
- The page: `<CarFrame>` with `Settings`.
  - **The API key:** a paste field, Check and save, its expiry, and Forget.
  - **The car:** the VIN, saved.
  - A line on the quota: requests left this hour, and when it resets.

- [ ] **Step 1: Move `SkodaApiUpdateServiceTests` and `SkodaApiSignInTests` and adapt them** to `SkodaCar` and
  `SkodaKey`, with every case and assertion kept, now on `Status.Code`/`NeedsOwner`/`IsBlocked`/`NextDelay`. Add:
  - `TheClockLeavesTheLastThreeRequestsToPeople`: with `RateLimit-Remaining: 3` and the reset in 20 min,
    `NextDelay` is ≥ 20 min, and a `ReadAsync` (an ask) still sends a request.
  - `AKeyPastedAfterARefusalIsTriedAtOnce`: refused key → blocked `refused`; then `SubmitAsync(newKey)` passes, the
    car is not blocked, and the next read sends a request.
  - `TheVinComesFromConfigurationAndThePageWins`: `Skoda:Vin` set → used; saved on the page → the page's.
  - `MappingTests`: `charging.json` and `connect-cable.json` are accepted by `VehicleTestHub`.
- [ ] **Step 2: Run them.** Expected: FAIL to compile.
- [ ] **Step 3: Implement** `SkodaCar`, `SkodaKey`, `Program.cs`, the page and the texts.
- [ ] **Step 4: Run them.** Expected: PASS. Build the image (`PLUGIN=Skoda`, port 8101) and check the waiting page.
- [ ] **Step 5:** README in the shape of volkswagen's: what it does, the quota, run it, configuration, the data
  directory, and what changed from the built-in. Root README: the table row.
- [ ] **Step 6: Commit, push the branch and open the PR** in gleanvolt-cars: "MyŠkoda as a car plugin (gleanvolt#475)".

## Part 2: The VW Group Data Act portal (PR on branch `vwgroup`, after Part 1 is merged)

### Task 3: The portal client, its parsers and the brands, moved

**Files (in `vwgroup/`):**
- `Gleanvolt.Plugin.VwGroup.slnx`, README, csproj (`gleanvolt-plugin-vwgroup`), launchSettings (8102), resx, `Say.cs`.
- Moved from `src/Gleanvolt.Infrastructure/Vehicles/VwGroup/`, namespace `Gleanvolt.Plugin.VwGroup`:
  - `VwGroupBrands`, `VwGroupFieldNames`, `VwGroupLoginForm`, `VwGroupPortalClient`, `VwGroupPortalException`,
    `VwGroupPortalReader`, `VwGroupReportBundle` and `VwGroupSignIn`;
  - `VwGroupVehicleStateMapper`, now producing `VehicleReading` and keeping its tie-breaks;
  - `VwGroupPortalOptions`, keeping every option, built per read from the settings and the password.
- Tests moved: `VwGroupBrandsTests`, `VwGroupLoginFormTests`, `VwGroupSignInPageTests`, `VwGroupReportBundleTests`,
  `VwGroupVehicleStateMapperTests`, `VwGroupMergedReadTests`, `VwGroupPortalClientTests` and
  `VwGroupLiveCaptureTests`, with `VwGroupFixtures.cs` and `Fixtures/VwGroup/*`.
- Root slnx, `plugins.json` (`vwgroup`, `VwGroup`, `VwGroup`, 8102) and the CI matrices.

- [ ] **Step 1: Move the tests and fixtures.** Run them. Expected: FAIL to compile.
- [ ] **Step 2: Move the files and swap the types.** Run them. Expected: every moved case passes (about 115).
- [ ] **Step 3: Commit.** "Data Act portal: the client, the parsers and the brands (gleanvolt#475)".

### Task 4: `VwGroupCar`, the account on the page, and the plugin

**Interfaces:**
- `PluginInfo("vwgroup", "VW Group Data Act", "VwGroup", Port: 8102)`.
- `public sealed class VwGroupCar(CarPollOptions options, TimeProvider time, VwGroupAccount account, ILogger<VwGroupCar> logger) : PluginCar(options, time)`.
  It holds one `VwGroupPortalClient` and its cookie jar for the life of the process, as `VwGroupUpdateService` did,
  and replaces it when the account changes.
  - `FetchAsync`: the update service's `FetchAsync`, mapped to the status codes above.
  - `public override TimeSpan NextDelay`: infinite while blocked; 15 min after a read or `NoDataAvailable`; and on
    other failures the doubling backoff up to 1 h, which `CarPollOptions`' intervals don't apply to (the portal's
    cadence is the feed's, as before).
  - The session age is logged at each sign-in, as before.
- `public sealed class VwGroupAccount(PluginSettings settings, IProviderSecretStore files)`:
  - `Username`, `Brand`, `ClientId` and `Vin` from settings;
  - `Password` from `password.json`;
  - `Options` builds a `VwGroupPortalOptions`;
  - `IsComplete` (username, password, and a client id from the brand or stated);
  - `Save(...)` raises `Changed`, and the car `Resume()`s on it.
- The page: `<CarFrame>` with `Settings`:
  - **The account:** VW ID, password, brand (a select from `VwGroupBrands`), client id (an "advanced" field), and VIN
    (optional; required when the account sees more than one car).
  - The session's age.

- [ ] **Step 1: Move `VwGroupUpdateServiceTests` and adapt them** to `VwGroupCar`, keeping every case. Add:
  - `ARefusalStopsTheClockAndASavedAccountResumesIt`;
  - `TheSessionIsKeptAndSignInHappensOnlyWhenBounced`: two reads make one sign-in; a bounce makes one more;
  - `TheAccountComesFromConfigurationAndThePageWins` (all four keys);
  - `MappingTests`: the live capture is accepted by `VehicleTestHub`.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run them.** Expected: PASS. Build the image (`PLUGIN=VwGroup`, port 8102) and check the waiting page.
- [ ] **Step 5: Commit.**

### Task 5: Check now, the diagnostics, on the plugin's page

**Files:** `Pages/Diagnostics.razor`, a section the home page renders below the account; its texts in `PageText`.

The content is `src/Gleanvolt.Web/Components/PortalDiagnostics.razor` as it is:
- open when the read failed, collapsed when it worked;
- the delivery (vehicle, snapshot count and span);
- the fields matched;
- the field names nothing knows.

Check now runs `VwGroupPortalReader` once with a fresh jar, as the built-in's on-demand check does. It doesn't touch
`VwGroupCar`'s session or clock.

- [ ] **Step 1: Write the failing tests.** Render through the app with a ticket, as `CarsHostingTests` does, with the
  portal faked:
  - `CheckNowShowsTheDeliveryAndTheUnknownFieldNames`: unknown names listed; the car's `NextDelay` unchanged.
  - `CheckNowWorksWhileTheFeedIsBlocked`.
- [ ] **Step 2: Implement.** Run the tests. Expected: PASS.
- [ ] **Step 3: Write the READMEs** (the plugin's and the root table). Commit, push the branch and open the PR:
  "The VW Group Data Act portal as a car plugin (gleanvolt#475)".
- [ ] **Step 4: Open "Looking for testers" issues** in gleanvolt (as #354 did) for MyŠkoda and Data Act, naming the
  plugins and what a tester sends (the page's status and the log, never the key or the password).
