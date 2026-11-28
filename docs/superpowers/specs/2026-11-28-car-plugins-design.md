# Car plugins: a shared plugin host, and every car feed in gleanvolt-cars

Date: 2026-11-28. Repositories: gleanvolt (hosting SDK, migration, web UI, docs, deploy) and a new public
repository, gleanvolt-cars (MIT).

## Intent

More cars are coming, and each car brand has been a Gleanvolt release. Charger plugins already live outside
Gleanvolt (#438–#445): they start on their own, wait for Gleanvolt, and get their token when the owner clicks
Accept on Settings → Plugins. Car plugins should work the same way and live in their own repository,
`gleanvolt-cars`, laid out like `gleanvolt-chargers`. Weather follows the same path afterwards, and is not part of
this work.

After this, Gleanvolt reads no car itself. The Car page offers **"Another manufacturer — charge only"** (no feed,
targets in kWh) or **a plugin**.

## Where things are today

- **Built-in feeds in Gleanvolt:** volkswagen.de (`Gleanvolt.Infrastructure/Vehicles/VwWebsite`), MyŠkoda
  (`…/Skoda`), and the VW Group Data Act portal (`…/VwGroup`). Chosen by `Vehicle:Website:Enabled`,
  `Vehicle:Skoda:Enabled` and `Vehicle:DataAct:Enabled` (`EvSettingKeys`), wired in
  `GleanvoltHostingExtensions.cs` (about line 825 onwards) into `ConfiguredVehicleFeed`. Their UI is in
  `Gleanvolt.Web`: `CarEditorForm.razor`, `VehicleAccount.razor` (sign-in), `PortalDiagnostics.razor` (Data Act
  check), `Car.razor`. Secrets: `Vehicle:Website:Password` and `Vehicle:DataAct:Password` in Gleanvolt's secret store,
  plus the Škoda API key store.
- **External car feed:** `Vehicle:External:Enabled`, `ExternalVehicleFeed`, contract `vehicle` 1.3 in
  `Gleanvolt.Providers.Contracts/Vehicle.cs`, SDK side `Gleanvolt.Providers.Client/Vehicle/VehicleProvider.cs`.
- **Car plugins in this repo:** `plugins/Gleanvolt.Plugin.Volkswagen` (a copy of the volkswagen.de feed, port 8095)
  and `plugins/Gleanvolt.Plugin.VehicleSample` (simulated, port 8096, which clashes with OpenWeather's 8096). Neither
  is shipped. Each one sets itself up in about 100 lines of `Program.cs`, with Gleanvolt fixed at
  `localhost:8090` and frame-ancestors fixed at startup.
- **The Pi** runs the Volkswagen plugin from a hand-built image `gleanvolt-volkswagen:latest`, started by
  `~/gleanvolt-volkswagen.sh` with a token copied from the controller, on volume `gleanvolt-volkswagen`.
- **The charger host:** `Gleanvolt.Chargers.Hosting` (`PluginHost.CreateBuilder<TCharger>`, `PluginInfo`,
  `PluginSettings`, `SavedSetting`, `PageFraming`, `WaitingPage`, `PluginCulture`/`ViewerCulture`, `Words`,
  `SharedText`, `DeviceRecorder`, `LanScan`, `ModbusDevice`, `PluginCharger`, `StatusReporter`, Components
  `PluginFrame`/`RecordingSection`/`App`/`Routes`). Pairing itself (`ProviderPairing`, `GleanvoltFinder`,
  `ProviderCredentials`) is already in `Gleanvolt.Providers.Client` and works for every kind.

## Goals

- A car plugin behaves like a charger plugin: it starts before Gleanvolt, finds it on the LAN, shows the waiting
  page with the pairing state, is paired by Accept, and its frame permissions follow the pairing.
- One host for every kind of plugin. The charger, car and (later) weather layers are thin layers on top of it.
- `gleanvolt-cars`: one folder per car, each with its own `.slnx`, plugin, tests and README, its own image
  (`ghcr.io/<owner>/gleanvolt-plugin-<name>`, amd64 + arm64), its own self-contained zips (win-x64, linux-x64,
  linux-arm64), and its own tag (`volkswagen-v1.0.0`). A car fix ships without a Gleanvolt release.
- volkswagen.de, MyŠkoda and the Data Act portal are all moved there, and the built-ins are removed.
- An upgraded installation ends up on the matching plugin without the owner having to work out which one.

## Non-goals

- Weather plugins. OpenWeather stays in this repo and is released with Gleanvolt, as now. The shared host makes the
  weather move small later.
- Tariff and solar-forecast plugins (only samples exist).
- Changing the vehicle contract. The plugins use `vehicle` 1.3 as it is.
- New car brands. The repository is set up for them, and the sample is the template.

## Anti-goals

- A second copy of the host (discovery, waiting page, framing, settings) for cars. That's what option B was, and it
  was rejected.
- A breaking change for charger plugin authors that buys them nothing. The charger API stays source-compatible; see
  "Decided".
- Gleanvolt configuring a plugin, or handing it a secret. Each plugin sets up its own account on its own page, as
  with chargers and OpenWeather (#425).
- An upgrade that silently leaves the owner with no car readings and no notice.

## Approaches

- **A. One host for every kind of plugin (chosen).** The kind-neutral part of `PluginHost` moves into a new package
  for any kind of plugin. The charger package becomes its charger layer, and a car layer sits beside it. The cost is
  an SDK release before `gleanvolt-cars` builds from nuget.org.
- **B. A car copy of the host (`Gleanvolt.Cars.Hosting` copied from the charger one).** Quicker, and the charger
  plugins aren't touched. Rejected: two copies drift apart, and weather would make a third.
- **C. No shared host; each car plugin sets itself up,** as Volkswagen does today. Rejected: every new car copies
  about 100 lines and keeps today's gaps (Gleanvolt fixed at `localhost:8090`, framing fixed at startup).

The owner picked A.

## Decided

**Decided with the owner**
- `gleanvolt-cars`: a new public MIT repository, laid out like `gleanvolt-chargers`.
- The three built-in feeds (volkswagen.de, MyŠkoda, VW Group Data Act portal) move there as plugins, and the built-ins
  are removed from Gleanvolt.
- The built-in "Another manufacturer — charge only" stays (it is "no feed", not a feed).
- Approach A.
- Weather comes later, separately.

**Standing rules from earlier work**
- Any official release (Gleanvolt, SDK on nuget.org, a plugin tag) needs the owner's explicit yes first. SDK previews
  for `gleanvolt-chargers`/`gleanvolt-cars` CI are hand-uploaded preview versions, never a plain version.
- Every PR is based on `main`; no stacked PRs.
- Owners see "plugin"; "provider" stays the developer's word (#470).
- Every new UI string goes in both `<Area>Text.resx` and `.cs.resx`. UI changes update `docs/manual` (en + cs) and its
  screenshots.
- Any changed entity or state goes into the table in `docs/HOME_ASSISTANT.md`.
- Record the decisions in `docs/DECISIONS.md`, and log each PR in `docs/IMPLEMENTATION_LOG.md`.

## The design

### 1. The shared host, `Gleanvolt.Plugins.Hosting` (this repo, MIT, released with the SDK)

Everything in `Gleanvolt.Chargers.Hosting` that does not involve a charger moves here:
- the builder: configuration sources, `Urls`/`Gleanvolt:PageUrl`, the data directory
  (`<Section>:DataDirectory` or `DataDirectory`), the secret store, `PluginSettings`/`SavedSetting`, optional
  `Gleanvolt:Endpoint`/`Gleanvolt:Token`, `PageFraming` that follows the pairing, Razor components, antiforgery without
  X-Frame-Options, and data-protection keys in the data directory;
- the pipeline: the CSP header for each request, `UsePluginCulture`, the page guard with the `WaitingPage` for
  unpaired requests, the culture cookie and static assets;
- `PluginInfo` (without `DefaultDevicePort`, which stays in the charger layer), `Words`, the kind-neutral `SharedText`
  entries, `ViewerCulture`, `PluginCulture` and the waiting page.

The builder is generic over the kind's registration. The kind layer passes a callback that registers its provider
(`AddGleanvoltChargerProvider<T>` or `AddGleanvoltVehicleProvider<T>`) with the `ProviderOptions` the host has filled
in. The exact shape (a callback or an abstract kind descriptor) is left to the builder.

What stays charger-only in `Gleanvolt.Chargers.Hosting`: `PluginCharger`, `GleanvoltSession`, the charger
`StatusReporter`, `PluginFrame`, `RecordingSection`/`DeviceRecorder`/`RecordingHandler`, `ModbusDevice`, `LanScan`
and the `/recording` endpoint.

**Compatibility (my call):** `Gleanvolt.Chargers.Hosting` keeps its public names and signatures
(`PluginHost.CreateBuilder<TCharger>(args, PluginInfo)`, `PluginHost.Build`, the `Gleanvolt.Chargers` namespace, and
`PluginInfo` with `DefaultDevicePort`). It forwards to the shared host. The four charger plugins move to the new SDK
with a version bump and no code changes. If keeping `PluginInfo` the same proves awkward, a charger-specific
`PluginInfo` that wraps the shared one is fine, as long as charger plugin code does not change.

### 2. The car layer, `Gleanvolt.Cars.Hosting` (this repo, MIT, released with the SDK)

- `CarPluginHost.CreateBuilder<TCar>(args, PluginInfo)` / `Build`, on the shared host.
- `PluginCar`: an abstract base implementing `IVehicleSource`. It keeps the last reading, the status and the
  `Changed` event, like `PluginCharger`, and it offers a poll loop the plugin may use. The loop reads more often
  while `VehicleProviderConnection.Session` is open (#414), as the built-ins and the Volkswagen plugin do today.
- A car `StatusReporter` that sends the plugin's status to Gleanvolt.
- A `CarFrame` page: the status line, the car Gleanvolt is configured for (from the welcome's `VehicleInfo`), whether
  a charging session is open, the last reading (SOC, charge state, plugged, the car's remaining-time estimate, the
  capture time), a "Read now" button, and slots for the plugin's own view (`ChildContent`) and its own settings
  (`Settings`, where the account and sign-in go).
- No recording for testers at first (my call). The car feeds are cloud APIs, and a recording would hold the VIN,
  account details and possibly location. It can be added later with a redaction rule.

### 3. `gleanvolt-cars` (new repository)

- Layout copied from `gleanvolt-chargers`: `Directory.Build.props` with `GleanvoltSdkVersion`, `NuGet.config` with the
  local `artifacts/feed`, `plugins.json` (name, project, section, port), `docker/Dockerfile`, CI and release workflows,
  LICENSE (MIT), THIRD-PARTY-NOTICES, a README listing the cars and how to add one.
- Folders and default ports (my call):
  - `volkswagen`: `Gleanvolt.Plugin.Volkswagen`, section `Volkswagen`, port 8095. Unchanged so the Pi's
    bookmarks and frame settings keep working.
  - `skoda`: `Gleanvolt.Plugin.Skoda`, section `Skoda`, port 8101.
  - `vwgroup`: `Gleanvolt.Plugin.VwGroup` (the Data Act portal, which covers Cupra and other Group brands), section
    `VwGroup`, port 8102.
  - `sample`: `Gleanvolt.Plugin.VehicleSample`, port 8103 (no longer clashes with OpenWeather's 8096). It is the
    template and isn't released as an image.
- **Ports keep what the built-ins learnt**, field for field and rule for rule: volkswagen.de's sign-in with the
  emailed code and its session store; MyŠkoda's API key; the Data Act portal's brands, field names, batch timing and
  login form. The Data Act "check now" diagnostics (`PortalDiagnostics.razor`) become a section of that plugin's page.
  The volkswagen.de sign-in banner and form (`VehicleAccount.razor`) become the Volkswagen plugin's own page, which
  already has them.
- **The Volkswagen plugin keeps its secret file names** (it already uses the built-in's names), so the Pi's existing
  volume `gleanvolt-volkswagen` still reads with the released image.
- Tests run on `Gleanvolt.Providers.Testing` (`PluginHarness`, the test hub) with recorded HTTP replies. No real
  account in CI.

### 4. Gleanvolt: deprecation, then removal

**Deprecation release (next Gleanvolt minor). It waits until all three plugins are released, and Volkswagen is
verified on the Pi (owner's decision):**
- The car plugins ship: images and zips from `gleanvolt-cars`, compose services under profiles `volkswagen`, `skoda`
  and `vwgroup` in `deploy/docker-compose.yml` and the NAS file, and directories prepared by `gleanvolt-init`, as for
  the charger plugins. MyŠkoda and Data Act are released untested, with "looking for testers" issues.
- **The old setup carries over, apart from passwords (owner's decision).** As compose hands `EV_CHARGER_HOST` to a
  charger plugin, it hands each built-in's non-secret values to the matching plugin:
  - volkswagen.de: VIN and username;
  - MyŠkoda: VIN;
  - Data Act: VIN, username, brand and client id.

  Take the env names from the built-ins' options, including the `VW_*` fallbacks. In each plugin, these are settings
  that come from configuration, and a value saved on the plugin's page wins over them (`PluginSettings`/`SavedSetting`).
  Passwords and the Škoda API key never carry over. The owner enters them on the plugin's page. Values saved only in
  Gleanvolt's overrides file, not in `.env`, don't reach the plugin's container. The Car page's notice lists them, so
  the owner can copy them.
- The Car page labels each built-in feed "deprecated, use the … plugin", with a link to the manual's car page. The
  log warns once at startup.
- Feed selection is unchanged (my call). An owner who switches to "A plugin" early keeps the built-in's saved account
  until the removal release, so there is a way back. This release only adds labels, the warning and the shipped
  plugins.

**Removal release (the minor after):**
- Gone from Gleanvolt: `Gleanvolt.Infrastructure/Vehicles/{VwWebsite,Skoda,VwGroup}` and their options, sign-in,
  stores and clients; their registration in `GleanvoltHostingExtensions`; `VehicleAccount.razor`,
  `PortalDiagnostics.razor` and the built-in parts of `CarEditorForm.razor`/`Car.razor`; their `CarText` strings;
  `EvSettingKeys.Website*`, `Skoda*` and `DataAct*`; and the secrets `Vehicle:Website:Password`,
  `Vehicle:DataAct:Password` and the Škoda key store.
- Kept: `Vehicle:External:Enabled`, `ExternalVehicleFeed`, the car's own fields (capacity, phases, currents), and the
  owner's MQTT telemetry topic (`Vehicle:Telemetry:Topic`). That topic is something the owner publishes to, not a
  manufacturer feed, and it already runs alongside any feed.
- The Car page offers **"Another manufacturer — charge only"** or **"A plugin"**.
- **Migration at startup** (like the weather migration, #425): an installation with any `Vehicle:Website|Skoda|DataAct:Enabled`
  true gets `Vehicle:External:Enabled` = true written to the overrides file. It also gets a "built-in retired" flag
  that keeps a notice on the Car page, naming the plugin to run, until a car plugin connects. There is one warning in
  the log. The old secrets are deleted from Gleanvolt's secret store, whatever feed is selected (including an owner
  already on External), not handed over. The owner enters the password on the plugin's page. The old keys are ignored
  with a warning, not refused, and compose keeps passing their non-secret values to the plugins.
- **Setup documents** that enable a built-in feed are refused with a message naming the plugin, as with
  `Weather:ApiKey`.
- **Deploy script:** with a built-in feed enabled in the configuration or `.env` and no car plugin in
  `GLEANVOLT_PLUGINS`, it adds the matching plugin's profile and says so (as #445 did for chargers). It removes nothing
  the owner added.
- The API, MQTT and Home Assistant states that described the built-in feeds (feed name, sign-in needed) now come from
  the external feed's status. Anything that changes is listed in HTTP_API.md's "Changes that can break a client", and
  in `docs/HOME_ASSISTANT.md`. Check gleanvolt-ha and gleanvolt-mcp for readers.
- **The Pi:** replace the hand-run container (`~/gleanvolt-volkswagen.sh`) with the compose `volkswagen` profile on the
  released image, keeping the `gleanvolt-volkswagen` volume's data. The old script and env file are deleted, with the
  owner's OK.

### 5. Docs

- The manual (en + cs): the car page describes "charge only" or "a plugin", lists the three plugins, and covers
  pairing. Screenshots are regenerated.
- `docs/PROVIDERS.md`: the shared host, the car layer, and how to write a car plugin in your own repository.
- The READMEs in `gleanvolt-cars` say "Settings → Plugins" only once the Gleanvolt release that uses that name is out
  (#470's rule).

## Order of work (each a GitHub issue under one umbrella issue, each PR on `main`)

1. The shared host `Gleanvolt.Plugins.Hosting`, with the charger layer on top and no change to charger plugin code.
   Hand-upload an SDK preview, and check that `gleanvolt-chargers` builds on it with only the version bump.
2. The car layer `Gleanvolt.Cars.Hosting` (`PluginCar`, `CarFrame`, the poll loop), tested with `PluginHarness`.
   Another SDK preview.
3. `gleanvolt-cars`: the repository, with Volkswagen and the sample moved from `plugins/` onto the car layer. They
   leave this repo's `plugins/` and solution. Verify Volkswagen on the Pi against the owner's ID.4.
4. Port MyŠkoda and the Data Act portal into `gleanvolt-cars`. Open "Looking for testers" issues, as #354 did.
5. Deprecation in Gleanvolt, the carry-over of non-secret settings, and ship the car plugins (each plugin tag and the
   Gleanvolt release with the owner's yes).
6. Removal and migration in Gleanvolt (the minor after, with the owner's yes).

Steps 1 → 2 → 3 run in order; 4 needs 2; 5 needs 3 verified and all three plugins released; 6 comes one release
after 5.

## Left to the builder

- The exact API shape between the shared host and a kind layer.
- How `SharedText` and `Words` split between the shared host and the charger layer.
- The poll loop's intervals (start from the Volkswagen plugin's current ones).
- The CI and release workflows' details in `gleanvolt-cars` (copy `gleanvolt-chargers` and adapt).
- Names of the migration types, mirroring `WeatherSourceSelection` and `ExternalWeatherSelectionWatcher`.

## Risks

- Only the volkswagen.de feed can be tested on the owner's car. MyŠkoda and the Data Act portal ship as
  plain ports with a call for testers, like Huawei and go-e.
- An owner who used MyŠkoda or the Data Act portal loses car readings after the removal release until they start the
  plugin and enter the password again. The notice, the log, the deploy script and the release notes all say so.
