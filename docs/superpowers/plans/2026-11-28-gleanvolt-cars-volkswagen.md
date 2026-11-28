# gleanvolt-cars: the Volkswagen plugin and the sample, implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task by task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Step 3 of the car plugins design (#474):
- a new public MIT repository, `gleanvolt-cars`, laid out like `gleanvolt-chargers`;
- the Volkswagen plugin and the vehicle sample moved there onto `Gleanvolt.Cars.Hosting`;
- Volkswagen verified on the Pi against the owner's ID.4;
- both plugins removed from this repository.

**Architecture:**
- Each car is a folder with its own `.slnx`, `src/`, `tests/` and README, referencing only nuget.org packages, pinned
  by `GleanvoltSdkVersion`.
- The Volkswagen plugin keeps its volkswagen.de client, sign-in and session store as they are. Its `VolkswagenFeed`
  and `VolkswagenPoller` become one `VolkswagenCar : PluginCar` (the gate, the clock, the status and the log come
  from the car layer), and its page becomes `CarFrame` with the account and sign-in in the `Settings` slot.
- In this repository, Gleanvolt's own tests and the manual's screenshot stop depending on the two plugins.

**Tech stack:** .NET 10, Blazor Server, SignalR, xUnit, the Gleanvolt SDK packages (`Gleanvolt.Cars.Hosting`,
`Gleanvolt.Providers.Testing`), Docker buildx, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-11-28-car-plugins-design.md`, section 3 and the order of work, step 3.

## Global constraints

- **Three steps need the owner's explicit yes, each asked at its own task and not before:**
  - creating the public repository `mpospisil/gleanvolt-cars` and pushing its first commits to `main`;
  - the hand upload of SDK `1.3.3-preview.6` to nuget.org, which the owner does himself;
  - replacing the Pi's running `gleanvolt-volkswagen` container.
- No release: no `volkswagen-v*` tag and no `release.yml` run. Shipping is step 5 (#476).
- Volkswagen keeps:
  - provider id `volkswagen`, section `Volkswagen` and port 8095;
  - its secret files `account.json` and `session.json` in the data directory;
  - the data-protection application name `gleanvolt-plugin-volkswagen`.

  With those, the Pi's volume `gleanvolt-volkswagen` reads unchanged.
- The sample gets provider id `vehicle-sample`, section `Sample` and port 8103. It is not released as an image.
- gleanvolt-cars conventions are gleanvolt-chargers' (`README.md` there, "Layout"):
  - page and status texts in `Resources/PageText.resx` (English) and `Resources/PageText.cs.resx` (Czech), with the
    generated `PageText` class from `Directory.Build.targets`;
  - `TreatWarningsAsErrors`;
  - MIT, with `LICENSE` and `THIRD-PARTY-NOTICES.md`.
- Owners see "plugin" and "Settings → Plugins" (#470); Gleanvolt 1.3.3 or later for pairing.
- In this repository: one PR based on `main`, with an IMPLEMENTATION_LOG entry and a DECISIONS record. UI changes
  update `docs/manual` (en + cs) and its screenshots; run `screenshots:check` before the PR. Run tests with
  `DOTNET_USE_POLLING_FILE_WATCHER=true`.
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. PR bodies end with the Claude Code line.

## Review focus

1. **An existing data directory:** the ported plugin must read the Pi's `account.json` and `session.json` and stay
   signed in, with no new sign-in and no emailed code. The test is in Task 4. The proof is Task 6.
2. **The account changes on the page while a read is on its way:** the car blocks for a sign-in, and the read's answer
   doesn't unblock it. This is the race the car layer's review found. The test is in Task 4.
3. **volkswagen.de unreachable, or answering with nothing usable:** the plugin stops asking (VW's own rule: every
   failure needs the owner, since a retry can trigger another emailed code), says why in both languages, and sends
   nothing until the owner signs in. The test is in Task 4.
4. **A Gleanvolt not reachable yet:** the plugin still reads the car on its clock, and its page shows the reading
   behind the waiting view once paired. The test is in Task 3, for the sample.
5. **Gleanvolt's own tests without the plugins:** the hub tests that used the sample's `SimulatedCar`, and the
   manual's `car-external-page` screenshot, still run and still say the same thing. The check is in Task 7.

---

### Task 1: The SDK preview the new repository builds on

**Files:** none in either repository; packages in `~/git/gleanvolt-cars/artifacts/feed/` (gitignored).

- [ ] **Step 1: Pack from `main` (c2e9d59 or later).** Pack the seven MIT packages
  (`Gleanvolt.Providers.Contracts`, `.Providers.Client`, `.Providers.Testing`, `.Plugins.Hosting`,
  `.Chargers.Hosting`, `.Cars.Hosting`, `.Modbus`) with `-c Release -p:BuildNumber=4 -p:VersionSuffix=preview.6`, as
  memory `gleanvolt-chargers-repo` records for the earlier previews. Output to
  `~/git/gleanvolt-cars/artifacts/feed/` (create the folder).
  Expected: 7 `.nupkg` and 7 `.snupkg`, version `1.3.3-preview.6`.
- [ ] **Step 2: Tell the owner the packages are ready to upload, with their path.** He uploads by hand. CI in the new
  repository stays red until he does; local builds use the feed.

### Task 2: The repository's frame

**Files (in `~/git/gleanvolt-cars`, a new local git repository on `main`):**
- Copy from gleanvolt-chargers, then adapt:
  - `Directory.Build.props`: `GleanvoltSdkVersion` `1.3.3-preview.6`, with a comment saying it is the first preview
    with `Gleanvolt.Cars.Hosting`;
  - `Directory.Build.targets`, `NuGet.config`, `.gitignore`, `.dockerignore`, `LICENSE`, `THIRD-PARTY-NOTICES.md`;
  - `docker/Dockerfile`: the comments and the `org.opencontainers.image.source` label name gleanvolt-cars and car
    plugins; the build is unchanged (`PLUGIN`, `PORT`, folder = lowercase `PLUGIN`).
- Create `plugins.json`:
  `[{ "name": "volkswagen", "project": "Volkswagen", "section": "Volkswagen", "port": 8095 }]`. The sample is
  deliberately absent, since it is never released.
- Create `Gleanvolt.Cars.slnx`, listing every car's projects.
- Create `.github/workflows/ci.yml`: gleanvolt-chargers' file, with the build matrix `volkswagen`/`Volkswagen` and
  `sample`/`Sample` and the image matrix `volkswagen` only.
- Create `.github/workflows/release.yml`: gleanvolt-chargers' file, with "charger" → "car" in the release notes.
- Create `README.md`, modelled on gleanvolt-chargers' README:
  - a table: Volkswagen (volkswagen.de, the VW ID account, image `ghcr.io/mpospisil/gleanvolt-plugin-volkswagen`,
    page 8095) and the sample;
  - "Layout", "Your own car, in your own repository" (pointing at the `Gleanvolt.Cars.Hosting` README) and
    "Building", with the pack loop listing all seven packages;
  - a line that MyŠkoda and the Data Act portal come next (#475).

- [ ] **Step 1: Create the files**, and `git init -b main`.
- [ ] **Step 2: Check `jq . plugins.json`.** Expected: it parses.
- [ ] **Step 3: Commit.** "The car plugins' repository: layout, SDK, CI and release (gleanvolt#474)". Stays local
  until Task 5.

### Task 3: The sample, on the car layer

**Files (in `~/git/gleanvolt-cars/sample/`):**
- `Gleanvolt.Plugin.Sample.slnx`.
- `src/Gleanvolt.Plugin.Sample/` (the project is `Gleanvolt.Plugin.Sample`, so the folder is the lowercase project name, as the Dockerfile expects):
  - `Gleanvolt.Plugin.Sample.csproj`: Web SDK, `RequiresAspNetWebAssets`, `PackageReference`
    `Gleanvolt.Cars.Hosting` `$(GleanvoltSdkVersion)`, `InternalsVisibleTo` the tests.
  - `Program.cs`.
  - `SimulatedCar.cs`.
  - `Pages/Home.razor`, `Pages/_Imports.razor`.
  - `Resources/PageText.resx`, `Resources/PageText.cs.resx`.
- `tests/Gleanvolt.Plugin.Sample.Tests/` (`.csproj` as gleanvolt-chargers' test projects, with
  `Gleanvolt.Providers.Testing`), and `SampleTests.cs`.
- `README.md`.

These are ported from `plugins/Gleanvolt.Plugin.VehicleSample` in this repository (`SimulatedCar`, `SampleLogin`,
`StepPublisher`, `SamplePage`). It is a template for a new car, so it shows each part of the car layer once.

**Interfaces:**
- Consumes: `Gleanvolt.Cars.Hosting` 1.3.3-preview.6 (`PluginCar`, `CarPluginHost`, `CarFrame`, `CarPollOptions`) and
  `Gleanvolt.Providers.Testing` (`CarPluginHarness`, `VehicleTestHub`).
- Produces:
  - `PluginInfo("vehicle-sample", "Vehicle sample", "Sample", Port: 8103)`.
  - `public sealed class SimulatedCar(CarPollOptions options, TimeProvider time, PluginSettings settings) : PluginCar(options, time)`.
    - `FetchAsync` returns a reading whose SOC climbs one percent each read while "charging". It keeps the old
      sample's `StartSocPercent` constant and its plug/charge behaviour.
    - It blocks with `not-set-up` while the setting `name` is empty. The page's Settings section saves the name and
      calls `Resume()`, which stands in for a sign-in.

- [ ] **Step 1: Write the failing tests** in `SampleTests.cs`, through `CarPluginHarness`:
  - `WithoutANameTheOwnerIsNeededAndNothingIsRead`: status `not-set-up`, `NeedsOwner`, no reading.
  - `AfterTheNameItReadsAndPublishes`: save `name`, then `Resume()`. `hub.WaitForReadingAsync()` returns SOC
    `SimulatedCar.StartSocPercent`.
  - `UnpairedItStillReadsOnItsClock`: `harness.StartAsync(car, fakeTime)`, then advance 15 min. Fetches 2,
    `LastReading` set, nothing published.
  - `WhileChargingTheSocClimbs`: two reads, 5 min apart, with a session open. The second SOC is one more.
- [ ] **Step 2: Run them.**
  `dotnet test sample/Gleanvolt.Plugin.Sample.slnx` (with the local feed from Task 1).
  Expected: FAIL to compile.
- [ ] **Step 3: Implement the plugin.**
  - `Program.cs`: `CarPluginHost.CreateBuilder<SimulatedCar>(args, plugin)`, then `Build(...).Run()`.
  - `Home.razor` at `/`: `<CarFrame>` with a Settings section, "Your name", Save.
  - Texts in `PageText` (en + cs).
- [ ] **Step 4: Run them.** Expected: PASS.
- [ ] **Step 5: Run the sample by hand.**
  `dotnet run --project sample/src/... --Urls=http://localhost:8103 --Gleanvolt:Endpoint=http://127.0.0.1:9`.
  `curl -s localhost:8103/` returns the waiting page with "Vehicle sample".
- [ ] **Step 6: Commit.** "The vehicle sample, on Gleanvolt.Cars.Hosting (gleanvolt#474)".

### Task 4: The Volkswagen plugin, on the car layer

**Files (in `~/git/gleanvolt-cars/volkswagen/`):**
- `Gleanvolt.Plugin.Volkswagen.slnx` and `README.md`. The README is the old one, with its run sections now naming the
  released image, the compose profile coming in step 5 and Settings → Plugins, and "What changed" now covering the car
  layer.
- `src/Gleanvolt.Plugin.Volkswagen/`:
  - csproj as the sample's, `UserSecretsId` `gleanvolt-plugin-volkswagen`;
  - `Program.cs`;
  - `VolkswagenCar.cs` (new; replaces `VolkswagenFeed.cs`, poller included);
  - moved unchanged apart from namespaces and `PageText` lookups: `VolkswagenAccount.cs`, `VolkswagenOptions.cs`,
    `VwWebsiteClient.cs`, `VwWebsiteSignIn.cs`, `VwWebsiteLoginPage.cs`, `VwWebsiteSessionStore.cs`,
    `VwWebsiteChargingStatus.cs`;
  - `Pages/Home.razor` and `Pages/_Imports.razor`;
  - `Resources/PageText.resx` and `PageText.cs.resx`, which replace `PageText.cs`;
  - `wwwroot/` only if the page needs styles beyond the shared `app.css`.
  - The plugin's own `Words.cs` is deleted; it uses `Gleanvolt.Plugins.Words`.
- `tests/Gleanvolt.Plugin.Volkswagen.Tests/`:
  - `MappingTests.cs` and `VolkswagenCarTests.cs`, moved from this repository's
    `tests/Gleanvolt.Hosting.Tests/VolkswagenPluginTests.cs`;
  - `Fixtures/VwWebsite/charging-status.json`, copied from `tests/Gleanvolt.Hosting.Tests/Fixtures/VwWebsite/`.

**Interfaces:**
- Produces `PluginInfo("volkswagen", "Volkswagen", "Volkswagen", Port: 8095)`.
- `public sealed class VolkswagenCar : PluginCar`, with the constructor
  `(CarPollOptions options, TimeProvider time, AccountStore account, VwWebsiteClient client, ILogger<VolkswagenCar> logger)`.
  - `FetchAsync` asks `client.GetReadingAsync`. A null answer calls
    `Block(AwaitingCode ? WantsCode : NoReading, needs-sign-in)`.
  - `OnFetchFailed` is overridden to `Block(Unreachable.Format(message), needs-sign-in)`. Every failure needs the
    owner, as `VolkswagenFeed.Stop` decided.
  - Each block's words end with `ResumesAfterSignIn`, as `Stop` did.
  - An incomplete account blocks with `not-set-up` at construction and on every account change.
  - A change of username or VIN blocks for a sign-in. A new password alone doesn't (today's
    `Another_account_stops_the_feed...` rule).
  - A successful sign-in on the page calls `Resume()`.
- `CarPollOptions` come from section `Volkswagen`. The key names `PollInterval`, `IdlePollInterval` and
  `MinimumTimeBetweenReads` are `VolkswagenOptions`' own, so existing configuration keeps working. Remove those three
  from `VolkswagenOptions`.
- `VolkswagenOptions.DataDirectory` goes. The shared host reads `Volkswagen:DataDirectory`, or `DataDirectory`.
  `AccountStore` and `VwWebsiteSessionStore` keep their file names.

- [ ] **Step 1: Move the tests and adapt them.**
  - Every `VolkswagenPluginMappingTests` case stays. "Passes Gleanvolt's validator" becomes "`VehicleTestHub` accepts
    it", through `CarPluginHarness.ConnectAsync`.
  - Every `VolkswagenPluginFeedTests` case keeps its name and assertion, now against `VolkswagenCar` and the
    harness's `CarPoller`.
  - Add:
    - `AnExistingDataDirectoryStaysSignedIn`: write an `account.json` and a `session.json` in the old format into the
      harness's directory. The car is not blocked, and the first read calls the fake volkswagen.de with the session's
      cookies.
    - `AnAccountChangedWhileAReadIsOnItsWayBlocksForASignIn`: hold the fake's answer, change the username, then
      release it. `IsBlocked`, and code `needs-sign-in`.
- [ ] **Step 2: Run them.** `dotnet test volkswagen/Gleanvolt.Plugin.Volkswagen.slnx`. Expected: FAIL to compile.
- [ ] **Step 3: Implement.**
  - `VolkswagenCar`, from `VolkswagenFeed`'s rules, as above.
  - `Program.cs` on `CarPluginHost`, adding `AccountStore`, `VwWebsiteSessionStore`, `VwWebsiteClient` and
    `VwWebsiteSignIn` as today.
  - `Home.razor` at `/`: `<CarFrame>` with today's account form, sign-in and code sections in `Settings`, and today's
    extra reading lines in `ChildContent`, if any aren't in `CarFrame`.
  - The texts move from `PageText.cs` to the resx pair, keys unchanged.
- [ ] **Step 4: Run them.** Expected: all pass, the 23 moved cases included.
- [ ] **Step 5: Build the image locally.**
  `docker buildx build --platform linux/amd64 -f docker/Dockerfile --build-arg PLUGIN=Volkswagen --build-arg PORT=8095 -t gleanvolt-plugin-volkswagen:local --load .`
  Run it with `-e Gleanvolt__Endpoint=http://127.0.0.1:9` and a scratch volume. `curl localhost:8095/` shows the
  waiting page. Do the same for the sample with `PLUGIN=Sample` and port 8103.
- [ ] **Step 6: Commit.** "The Volkswagen plugin, on Gleanvolt.Cars.Hosting (gleanvolt#474)".

### Task 5: Publish the repository (needs the owner's yes)

- [ ] **Step 1: Ask the owner** whether to create the public repository `mpospisil/gleanvolt-cars` and push `main`.
  Show the commit list.
- [ ] **Step 2: On yes:** `gh repo create mpospisil/gleanvolt-cars --public --source ~/git/gleanvolt-cars --push`.
  Then set its description: "Car plugins for Gleanvolt: Volkswagen (volkswagen.de), with MyŠkoda and the VW Group
  Data Act portal to come. MIT."
- [ ] **Step 3: Check CI.** `gh run list -R mpospisil/gleanvolt-cars`. Expected: red on restore until the owner has
  uploaded preview.6, then green. Report which.

### Task 6: Volkswagen on the Pi (needs the owner's yes)

The Pi runs `gleanvolt-volkswagen` from `~/gleanvolt-volkswagen.sh`. It uses image `gleanvolt-volkswagen:latest`,
volume `gleanvolt-volkswagen`, env file `~/gleanvolt-volkswagen.env` (Endpoint, Token, PageUrl, FrameAncestors) and
port 8095.

- [ ] **Step 1: Cross-build the image for the Pi.**
  `docker buildx build --platform linux/arm64 -f docker/Dockerfile --build-arg PLUGIN=Volkswagen --build-arg PORT=8095 --build-arg VERSION=1.0.0-local --build-arg SOURCE_REVISION=$(git rev-parse --short HEAD) -t gleanvolt-plugin-volkswagen:local --output type=docker,dest=<scratchpad>/vw.tar .`
- [ ] **Step 2: Ask the owner** whether to replace the running container. Say:
  - the volume stays;
  - the old image stays tagged, for a rollback with `~/gleanvolt-volkswagen.sh`.
- [ ] **Step 3: On yes:**
  - `ssh martin@192.168.2.7 'docker load' < vw.tar`;
  - then on the Pi: `docker rm -f gleanvolt-volkswagen && docker run -d --name gleanvolt-volkswagen --restart unless-stopped -p 8095:8095 -v gleanvolt-volkswagen:/data --env-file ~/gleanvolt-volkswagen.env gleanvolt-plugin-volkswagen:local`.
- [ ] **Step 4: Verify.**
  - `docker logs gleanvolt-volkswagen` shows the hello accepted and a reading published.
  - `docker logs gleanvolt-controller --since 5m | grep -i vehicle` shows the reading accepted.
  - The plugin's log has no sign-in block.
  - `curl -s http://192.168.2.7:8095/` answers.

  Report the reading's SOC and capture time. If it is blocked, report the status and roll back only on the owner's
  word.

### Task 7: This repository without the two plugins (one PR)

**Files:**
- Delete `plugins/Gleanvolt.Plugin.Volkswagen/` and `plugins/Gleanvolt.Plugin.VehicleSample/`, and their entries in
  `Gleanvolt.slnx`.
- `tests/Gleanvolt.Hosting.Tests/`:
  - delete `VolkswagenPluginTests.cs`;
  - drop both `ProjectReference`s from the csproj;
  - `ExternalVehicleHubTests.cs` replaces `SimulatedCar` with a private `IVehicleSource` in the test, starting at the
    same SOC (keep its value as a constant there);
  - the `Fixtures/VwWebsite/` file stays, since the built-in feed's tests use it until step 6.
- `tests/Gleanvolt.Web.Screenshots/`:
  - `ProviderPageScreenshot.Volkswagen` becomes `ProviderPageScreenshot.CarPlugin(now)`: a page rendered with bUnit
    from `Gleanvolt.Cars.Hosting`'s `CarFrame` and a stand-in `PluginCar` with the same reading as today;
  - its `Settings` fragment is a sign-in section shaped like Volkswagen's (VW ID, password, Sign in), with the CSS
    from `src/Gleanvolt.Plugins.Hosting/wwwroot/app.css`;
  - the csproj references `Gleanvolt.Cars.Hosting` instead of the plugin;
  - `SettingsScreenshots.The_external_car_providers_page` calls it.
- `docs/manual/src/content/docs/{en,cs}/setting-up/car.mdx`: the `car-external-page` alt text says "A car plugin's
  own page", and the plugin paragraph links to gleanvolt-cars. Regenerate the screenshots
  (`npm run screenshots` or the repo's script) and update `fingerprints.json`.
- `docs/VEHICLE.md`, `docs/SUPPORTED_EVS.md` and `docs/PROVIDERS.md`: the Volkswagen plugin and the sample are in
  gleanvolt-cars.
- `docs/DECISIONS.md` and `docs/IMPLEMENTATION_LOG.md`.
- This plan.

- [ ] **Step 1: Make the test changes, delete the plugins, and build.**
  `dotnet build Gleanvolt.slnx -c Release`. Expected: 0 errors.
- [ ] **Step 2: Run the hub and screenshot tests.**
  `DOTNET_USE_POLLING_FILE_WATCHER=true dotnet test tests/Gleanvolt.Hosting.Tests --filter ExternalVehicleHubTests`
  and the Screenshots project. Expected: pass.
- [ ] **Step 3: Regenerate the screenshots and run `screenshots:check`.** Expected: only `car-external-page` changed.
  Look at it: the reading and a sign-in section are there.
- [ ] **Step 4: Run the full Release test pass.** Expected: green.
- [ ] **Step 5: Write the docs, the DECISIONS record and the log entry.** Commit, push the branch, and
  `gh pr create -t "The Volkswagen plugin and the vehicle sample move to gleanvolt-cars (#474)"`.
- [ ] **Step 6: Update memory.**
  - A `gleanvolt-cars-repo` memory: location, layout, ports, SDK pin, and that the user uploads previews.
  - Update `mcp-container-on-the-pi`-style notes, if the Pi's Volkswagen container now runs a local image.
