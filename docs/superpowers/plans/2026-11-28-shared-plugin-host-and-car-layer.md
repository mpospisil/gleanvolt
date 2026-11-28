# Shared plugin host and car layer: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task by task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Steps 1–2 of the car plugins design.
1. The kind-neutral plugin host becomes the package `Gleanvolt.Plugins.Hosting`, and `Gleanvolt.Chargers.Hosting` is its
   charger layer. The charger plugins' code doesn't change.
2. A car layer, `Gleanvolt.Cars.Hosting`, and a vehicle test hub, so `gleanvolt-cars` (step 3) can be written.

**Architecture:** The types and the builder or pipeline code in `Gleanvolt.Chargers.Hosting` that don't depend on a
charger move to `Gleanvolt.Plugins.Hosting` (namespace `Gleanvolt.Plugins`). `PluginWebHost` takes a callback that
registers the kind's provider. The charger and car layers each wrap it in a few lines. The charger package keeps every
public name it has. Two things keep that working:
- a `buildTransitive` props file that adds `global using Gleanvolt.Plugins;`;
- a C# 14 extension property for `PluginSettings.Address`.

**Tech stack:** .NET 10 (C# 14), ASP.NET Core and Blazor Server (Razor SDK), SignalR, xUnit, the provider SDK in
`src/Gleanvolt.Providers.*`.

**Spec:** `docs/superpowers/specs/2026-11-28-car-plugins-design.md`. Steps 3–6 (`gleanvolt-cars`, the ports,
deprecation, removal) each get a plan of their own once the step before is merged.

## Global constraints

- Two PRs, each based on `main`, never stacked. Part 1 is Tasks 0–3, on branch `car-plugins-design` (it already has the
  spec, and this plan goes in with it). Part 2 is Tasks 4–7, on a new branch from `origin/main`, started only after
  Part 1's PR is merged. Check that with `gh pr view <n> --json state`.
- Nothing is released. No tag, and no `release.yml` run. Uploading an SDK preview to nuget.org needs the owner's
  explicit yes first, and then gets a preview version (`1.3.3-preview.N`), never a plain one. Verification uses the
  local feed `~/git/gleanvolt-chargers/artifacts/feed`.
- New packages are MIT: `<GleanvoltMitPackage>true</GleanvoltMitPackage>`,
  `<PackageLicenseExpression>MIT</PackageLicenseExpression>`, an empty `<PackageLicenseFile>`, and a `LICENSE` and
  `README.md` packed, as in `src/Gleanvolt.Chargers.Hosting/Gleanvolt.Chargers.Hosting.csproj`.
- Charger plugin source compatibility: the four plugins in `~/git/gleanvolt-chargers` must build and pass their tests
  with only `GleanvoltSdkVersion` changed.
- Plugin page text is a `Words(En, Cs)` pair in code, as `SharedText` does, never English alone. Owners see "plugin"
  and "Gleanvolt", never "provider" (#470).
- Before each `gh pr create`:
  - add a reverse-chronological entry at the top of `docs/IMPLEMENTATION_LOG.md`, and build the PR body from it;
  - add a record to `docs/DECISIONS.md`;
  - end the PR body with the Claude Code attribution line, and end commits with
    `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Run tests with `DOTNET_USE_POLLING_FILE_WATCHER=true` (the inotify limit makes hosting tests flaky otherwise), and
  run the Release configuration before the PR, as CI does.

## Review focus

1. **A charger plugin rebuilt on the new SDK:** it must compile and run unchanged, with its `using Gleanvolt.Chargers;`
   alone, `settings.Address`, and `new PluginInfo(..., DefaultDevicePort: 502)`. The proof is Task 3, Step 2.
2. **The stylesheet after the move**, now `_content/Gleanvolt.Plugins.Hosting/app.css`: it must be served by a
   published plugin, not only in Development, on both the waiting page and the Blazor page. The tests are in Task 1,
   Step 4 and Task 3, Step 3.
3. **A car plugin that isn't paired:** it still reads the car on its clock, so its page shows the car, and it publishes
   nothing. A car blocked for the owner makes no request at all until `Resume()`. The test is in Task 5.
4. **Three reasons to read at once** (Gleanvolt's refresh, a session opening, Read now) must make one request to the
   car maker, not three: `MinimumTimeBetweenReads`. The test is in Task 5.
5. **A failed read** (an exception) reports `unreachable` and the loop goes on. A stop (cancellation) reports nothing.
   The test is in Task 5.

---

## Part 1: the shared host (PR 1, branch `car-plugins-design`)

### Task 0: GitHub issues

- [ ] **Step 1: Create the umbrella issue and six sub-issues.** Use `gh issue create`. Each body takes its scope from
  the spec's "Order of work", with the spec's path.
  - Umbrella: "Car plugins: every car feed in gleanvolt-cars", modelled on #438.
  - Subs:
    1. "A shared plugin host for every plugin kind"
    2. "The car layer for car plugins, and a vehicle test hub"
    3. "gleanvolt-cars: Volkswagen and the sample"
    4. "Port MyŠkoda and the Data Act portal to gleanvolt-cars"
    5. "Deprecate the built-in car feeds, and ship the car plugins"
    6. "Remove the built-in car feeds"

  Put the sub-issue numbers in the umbrella's body.
- [ ] **Step 2:** Note issue numbers 1 and 2. They go in the commit messages and PR titles below as `(#N)`.

### Task 1: `Gleanvolt.Plugins.Hosting`, the kind-neutral types and the page shell

**Files:**
- Create `src/Gleanvolt.Plugins.Hosting/Gleanvolt.Plugins.Hosting.csproj`:
  - the Razor SDK, `RootNamespace` `Gleanvolt.Plugins`;
  - `PackageId` `Gleanvolt.Plugins.Hosting`, MIT;
  - references `Gleanvolt.Providers.Client` and the framework `Microsoft.AspNetCore.App`;
  - `InternalsVisibleTo` `Gleanvolt.Chargers.Hosting`, `Gleanvolt.Cars.Hosting`, `Gleanvolt.Providers.Testing` and
    `Gleanvolt.Providers.Tests`;
  - the `CultureInvariant.globalconfig`.
- Create `src/Gleanvolt.Plugins.Hosting/LICENSE` (copy) and `README.md`. The README says what the package is for: the
  host every plugin kind shares, used through a kind's layer.
- Move with `git mv` from `src/Gleanvolt.Chargers.Hosting/` to `src/Gleanvolt.Plugins.Hosting/`, changing the
  namespace to `Gleanvolt.Plugins`:
  - `Words.cs`, `ViewerCulture.cs`, `PluginCulture.cs`, `WaitingPage.cs`;
  - `wwwroot/app.css`;
  - `Components/App.razor` and `Components/Routes.razor` (namespace `Gleanvolt.Plugins.Components`).
- Split `SavedSetting.cs`:
  - `SettingSource` and `SavedSetting` move to `src/Gleanvolt.Plugins.Hosting/SavedSetting.cs`;
  - `DeviceAddress` stays in `src/Gleanvolt.Chargers.Hosting/DeviceAddress.cs`.
- Create `src/Gleanvolt.Plugins.Hosting/PluginInfo.cs`, with `PluginInfo` and `PluginSettings` moved out of
  `PluginHost.cs`. `PluginSettings` loses `Address`.
- Create `src/Gleanvolt.Plugins.Hosting/HostText.cs`, holding the kind-neutral texts moved from `SharedText`:
  - the pairing texts used by `WaitingPage`;
  - `TokenFromConfiguration`, `TokenFromPairing`, `Gleanvolt`, `NotConnected`, `Yes`, `No`, `NotSaid`, `Read`,
    `NoReadingYet`.
- Create `src/Gleanvolt.Plugins.Hosting/NetworkAddresses.cs`, holding `OwnAddresses()` moved from `LanScan`.
- Create `src/Gleanvolt.Plugins.Hosting/Components/_Imports.razor`.
- Create `src/Gleanvolt.Chargers.Hosting/PluginSettingsExtensions.cs`.
- Create `src/Gleanvolt.Chargers.Hosting/build/Gleanvolt.Chargers.Hosting.props`, packed to both `build/` and
  `buildTransitive/`.
- Modify `src/Gleanvolt.Chargers.Hosting/Gleanvolt.Chargers.Hosting.csproj`:
  - add a reference to `Gleanvolt.Plugins.Hosting`;
  - pack the props;
  - add a project-local `<Using Include="Gleanvolt.Plugins" />`.
- Modify `SharedText.cs`: each moved entry stays, as `public static readonly Words X = HostText.X;`.
- Modify `LanScan.cs` so `OwnAddresses()` forwards to `NetworkAddresses.OwnAddresses()`.
- Modify `PluginHost.cs`, `Components/_Imports.razor` and `PluginFrame.razor` to use the moved names. Change the
  stylesheet path in `WaitingPage.cs` and `App.razor` to `_content/Gleanvolt.Plugins.Hosting/app.css`.
- Modify `Gleanvolt.slnx` (add the project) and `src/Gleanvolt.Providers.Testing/PluginHarness.cs` (add
  `using Gleanvolt.Plugins;`).
- Tests: `tests/Gleanvolt.Providers.Tests/` `WordsTests.cs`, `ViewerCultureTests.cs`, `PluginCultureTests.cs` and
  `ChargersHostingTests.cs`. Add `using Gleanvolt.Plugins;` to each. In `PluginApp.StartAsync`,
  `--applicationName=Gleanvolt.Chargers.Hosting` becomes `--applicationName=Gleanvolt.Plugins.Hosting`.

**Interfaces:**
- Produces (namespace `Gleanvolt.Plugins`):
  - `public sealed record PluginInfo(string ProviderId, string Name, string Section, int Port, int DefaultDevicePort = 0)`,
    with `Version` and `PageAssembly` as today. `DefaultDevicePort` is documented as "for a plugin that talks to a
    device on the LAN; 0 otherwise".
  - `public sealed class PluginSettings(IProviderSecretStore files, IConfiguration configuration, PluginInfo plugin)`,
    with `SavedSetting Get(string name)`.
  - `SavedSetting`, `SettingSource`, `Words`, `ViewerCulture` and `HostText`, all public.
  - `PluginCulture.UsePluginCulture`, `WaitingPage.Html` and `NetworkAddresses.OwnAddresses()`, all internal.
- Produces (namespace `Gleanvolt.Chargers`):
  `public static class PluginSettingsExtensions { extension(PluginSettings settings) { public SavedSetting Address => settings.Get("host"); } }`.
- Produces, in the props file:
  `<Project><ItemGroup><Using Include="Gleanvolt.Plugins" /></ItemGroup></Project>`.

- [ ] **Step 1: Write the failing test.** In `ChargersHostingTests.cs`:
  `TheStylesheetIsTheSharedHostsOnBothPages`.
  - Start `PluginApp`.
  - `GET /` (the waiting page): the body contains `href="/_content/Gleanvolt.Plugins.Hosting/app.css"`.
  - `GET /_content/Gleanvolt.Plugins.Hosting/app.css` returns 200, with `text/css`.
  - Save credentials (`http://127.0.0.1:9`, `gvp_paired`), then `GET /?ticket=…`: the body contains
    `_content/Gleanvolt.Plugins.Hosting/app.css`.
- [ ] **Step 2: Run it.**
  `DOTNET_USE_POLLING_FILE_WATCHER=true dotnet test tests/Gleanvolt.Providers.Tests --filter TheStylesheetIsTheSharedHostsOnBothPages`.
  Expected: FAIL (the path is still `Gleanvolt.Chargers.Hosting`).
- [ ] **Step 3: Move the files as listed.** Use `git mv`, so the history follows. Keep each moved file's body, and
  change only its namespace and the names it uses.
- [ ] **Step 4: Run the provider tests.** `DOTNET_USE_POLLING_FILE_WATCHER=true dotnet test tests/Gleanvolt.Providers.Tests`.
  Expected: all pass, the new test included. The existing tests for the waiting page, the culture, framing and token
  source pass unchanged apart from their usings.
- [ ] **Step 5: Build the whole solution.** `dotnet build Gleanvolt.slnx -c Release`. Expected: 0 warnings that weren't
  there before, 0 errors.
- [ ] **Step 6: Commit.**
  "Plugins.Hosting: the plugin host's kind-neutral types move out of the charger package (#N1)".

### Task 2: `PluginWebHost`, the shared builder and pipeline, and the shared status reporter

**Files:**
- Create `src/Gleanvolt.Plugins.Hosting/PluginWebHost.cs`. It takes the builder and pipeline bodies of
  `PluginHost.CreateBuilder` and `PluginHost.Build`, without the charger-only lines, plus `PageFraming` and
  `AddressesOrNone`.
- Create `src/Gleanvolt.Plugins.Hosting/PluginStatusReporter.cs`.
- Modify `src/Gleanvolt.Chargers.Hosting/PluginHost.cs`, which becomes the charger layer:
  - `CreateBuilder<TCharger>` calls `PluginWebHost.CreateBuilder`, then registers `DeviceRecorder`,
    `GleanvoltSession`, `RecordingHandler`, the `TCharger`/`PluginCharger` aliases and `StatusReporter`.
  - `Build` calls `PluginWebHost.Build` with the `/recording` endpoint.
- Modify `src/Gleanvolt.Chargers.Hosting/PluginCharger.cs`:
  - `PluginCharger` also implements `IPluginStatus`;
  - `StatusReporter` becomes `public sealed class StatusReporter : PluginStatusReporter<ChargerWelcome>`, with the same
    constructor. It sets `session.Welcome` in its own `Welcomed` handler.
- Test: `tests/Gleanvolt.Providers.Tests/PluginWebHostTests.cs` (new). Its `AMachineWhoseAddressesCannotBeListedShowsNone`
  test moves there from `ChargersHostingTests`.

**Interfaces:**
- Consumes: Task 1's types.
- Produces (namespace `Gleanvolt.Plugins`):
  ```csharp
  public static class PluginWebHost
  {
      // Registers PluginInfo, TimeProvider, the secret store at <Section>:DataDirectory ?? DataDirectory ?? "data",
      // PluginSettings, PageFraming, Razor components, antiforgery without X-Frame-Options and data protection. It
      // then calls addProvider(services, configure), where configure fills in Endpoint (optional), Token, ProviderId,
      // Name, Version and PageUrl exactly as PluginHost does today.
      public static WebApplicationBuilder CreateBuilder(string[] args, PluginInfo plugin,
          Action<IServiceCollection, Action<ProviderOptions>> addProvider);

      // The CSP header on every response, UsePluginCulture, the page guard with the waiting page, the culture cookie,
      // antiforgery and static assets. Then endpoints?.Invoke(app), then MapRazorComponents<App>() with the
      // PageAssembly and extraAssemblies (component libraries with routable pages, none so far).
      public static WebApplication Build(WebApplicationBuilder builder, Action<WebApplication>? endpoints = null,
          params Assembly[] extraAssemblies);

      internal static IReadOnlyList<IPAddress> AddressesOrNone(Func<IReadOnlyList<IPAddress>> read);
  }

  public sealed class PageFraming(ProviderCredentials credentials, string? configured) { public string FrameAncestors(); }

  public interface IPluginStatus { ProviderStatus? Status { get; } event Action? Changed; }

  // Reports source.Status after every hello (forced) and on every Changed that changes it. A failure is logged at
  // Debug and forgotten, as StatusReporter does today.
  public class PluginStatusReporter<TWelcome>(ProviderConnection<TWelcome> connection, IPluginStatus source, ILogger logger)
      : IHostedService where TWelcome : Welcome;
  ```
- `Gleanvolt.Chargers.PluginHost.CreateBuilder<TCharger>(string[] args, PluginInfo plugin)` and
  `PluginHost.Build(WebApplicationBuilder builder)` keep their signatures.

- [ ] **Step 1: Write the failing tests.** In `PluginWebHostTests.cs`, a host built with
  `PluginWebHost.CreateBuilder(args, plugin, (services, configure) => services.AddGleanvoltVehicleProvider<NoCar>(configure))`.
  - `NoCar` is a private `IVehicleSource` returning null.
  - Use `UseTestServer()`, a `NoGleanvolt` finder, and `--applicationName=Gleanvolt.Plugins.Hosting`, as in
    `ChargersHostingTests.PluginApp`.

  The tests:
  - `AnyKindWaitsToBePairedWithoutAToken`: `GET /` returns 200 `text/html`, with the pairing code from
    `ProviderPairing.Status.Code` in the body.
  - `AnyKindsFramePolicyFollowsThePairing`:
    - before pairing: `frame-ancestors 'self' http://*:8090 https://*:8090`;
    - after `credentials.Save(new Uri("http://192.0.2.5:8091"), "gvp_paired")`:
      `frame-ancestors 'self' http://*:8091 https://*:8091`.
  - `TheStatusIsReportedAfterEveryHelloAndOnEachChange`: a `PluginStatusReporter<ChargerWelcome>` over a fake
    `IPluginStatus`, connected to `ChargerTestHub`.
    - After the hello, the hub's `Statuses` has one entry.
    - Raising `Changed` with the same status adds none.
    - Changing `Healthy` to false and raising `Changed` adds one.
- [ ] **Step 2: Run them.** `… --filter PluginWebHostTests`. Expected: FAIL to compile (`PluginWebHost` isn't defined).
- [ ] **Step 3: Implement `PluginWebHost`, `PageFraming`, `IPluginStatus` and `PluginStatusReporter<TWelcome>`** with
  the signatures above. Move the bodies from `PluginHost`; don't rewrite them.
- [ ] **Step 4: Reduce `PluginHost` and `StatusReporter` to the charger layer**, as listed under Files.
- [ ] **Step 5: Run all provider tests.** `DOTNET_USE_POLLING_FILE_WATCHER=true dotnet test tests/Gleanvolt.Providers.Tests`.
  Expected: all pass. That includes `ChargersHostingTests`, `ChargerTestHubTests` and `PairingEndpointTests`,
  unchanged.
- [ ] **Step 6: Commit.**
  "Plugins.Hosting: one builder and pipeline for every plugin kind; the charger host is a layer on it (#N1)".

### Task 3: Packaging, the charger plugins' check, docs and PR 1

**Files:**
- Modify `.github/workflows/release.yml`:
  - the pack loop (about line 181): add `Gleanvolt.Plugins.Hosting`;
  - the `expected=` list (about line 600): add `Plugins.Hosting`;
  - the summary line (about line 742): name it;
  - the nuget.org push loop (about line 818): add `Gleanvolt.Plugins.Hosting` before `Gleanvolt.Chargers.Hosting`.
- Modify `src/Gleanvolt.Chargers.Hosting/README.md`: say it is the charger layer on `Gleanvolt.Plugins.Hosting`.
- Modify `docs/PROVIDERS.md` "Writing one in .NET" (around line 87): name the shared host and say which layer a plugin
  of each kind references (`Gleanvolt.Chargers.Hosting` now, `Gleanvolt.Cars.Hosting` after Part 2).
- Modify `docs/DECISIONS.md` (a new top record) and `docs/IMPLEMENTATION_LOG.md` (a new top entry).

- [ ] **Step 1: Pack to the local feed.**
  `dotnet pack` each of `Gleanvolt.Providers.Contracts`, `Gleanvolt.Providers.Client`, `Gleanvolt.Providers.Testing`,
  `Gleanvolt.Plugins.Hosting`, `Gleanvolt.Chargers.Hosting` and `Gleanvolt.Modbus`, with `-c Release
  -p:Version=1.3.3-local.1 -o ~/git/gleanvolt-chargers/artifacts/feed`.
  Expected: six `.nupkg`. `unzip -l` of the Chargers one lists `build/Gleanvolt.Chargers.Hosting.props` and
  `buildTransitive/Gleanvolt.Chargers.Hosting.props`.
- [ ] **Step 2: Build the charger plugins on it, untouched.**
  In `~/git/gleanvolt-chargers`, without committing, run:
  `dotnet build Gleanvolt.Chargers.slnx -p:GleanvoltSdkVersion=1.3.3-local.1 && dotnet test Gleanvolt.Chargers.slnx -p:GleanvoltSdkVersion=1.3.3-local.1`.
  Expected: build and tests pass with `git -C ~/git/gleanvolt-chargers status` clean. If one fails, fix the
  compatibility in this repo, never in the plugin.
- [ ] **Step 3: Run one published charger plugin.**
  `dotnet publish ~/git/gleanvolt-chargers/solax/src/Gleanvolt.Plugin.Solax -c Release -p:GleanvoltSdkVersion=1.3.3-local.1 -o <scratch>/solax`.
  Start it with `--Urls=http://localhost:8198 --DataDirectory=<scratch>/data` (Production; no Gleanvolt needed). Then
  check:
  - `curl -s localhost:8198/` returns the waiting page;
  - `curl -sI localhost:8198/_content/Gleanvolt.Plugins.Hosting/app.css` returns 200.

  Stop the plugin, and delete the local packages from the feed.
- [ ] **Step 4: Run the full Release test pass.**
  `DOTNET_USE_POLLING_FILE_WATCHER=true dotnet test Gleanvolt.slnx -c Release`. Expected: all pass.
- [ ] **Step 5: Write the docs**, the DECISIONS record and the IMPLEMENTATION_LOG entry. Cover:
  - what moved;
  - the compatibility devices: the global using in the props, the `Address` extension property, and
    `DefaultDevicePort = 0`;
  - the stylesheet path change.
- [ ] **Step 6: Commit, push and open the PR.**
  - Commit "Plugins.Hosting: packaged, released with the SDK, and documented (#N1)".
  - `git push -u origin car-plugins-design`.
  - `gh pr create -t "A shared plugin host for every plugin kind (#N1)"`, with the body from the log entry. It
    mentions that the PR also carries the car plugins design and this plan.

---

## Part 2: the car layer (PR 2, a new branch from `origin/main` after PR 1 is merged)

### Task 4: `VehicleTestHub` and `CarPluginHarness` in `Gleanvolt.Providers.Testing`

**Files:**
- Create `src/Gleanvolt.Providers.Testing/VehicleTestHub.cs`, `VehicleTestHubOptions.cs` and `TestVehicleHub.cs`.
  `TestVehicleHub` is the SignalR face, like `TestChargerHub`: `Hello`, `ReportStatus`, `Publish`, and
  `OnDisconnectedAsync`.
- Create `src/Gleanvolt.Providers.Testing/CarPluginHarness.cs`.
- Modify `src/Gleanvolt.Providers.Testing/ChargerTestHub.cs` only to move code both hubs need (the loopback start,
  token, pairing endpoints and hello rules) into `internal` helpers, and only if that comes out smaller than copying.
- Modify `src/Gleanvolt.Providers.Testing/README.md`.
- `CarPluginHarness` needs `PluginCar`, so Task 5 adds it, together with the project reference to
  `Gleanvolt.Cars.Hosting`. Its interface is defined here so both tasks agree on it.
- Test: `tests/Gleanvolt.Providers.Tests/VehicleTestHubTests.cs`.

**Interfaces (namespace `Gleanvolt.Providers.Testing`):**
```csharp
public sealed class VehicleTestHubOptions
{
    public VehicleInfo Car { get; set; } = new("test-car", "Test car", "Volkswagen", "ID.4", 77, "Europe/Prague");
    public ChargingSessionInfo? Session { get; set; } = ChargingSessionInfo.Closed;
    // Plus the same pairing and token options ChargerTestHubOptions has, with the same names and defaults.
}

public sealed class VehicleTestHub : IAsyncDisposable
{
    public static Task<VehicleTestHub> StartAsync(VehicleTestHubOptions? options = null, CancellationToken cancellationToken = default);
    public Uri Endpoint { get; }
    public string Token { get; }
    public ProviderHello? Hello { get; }
    public bool IsAttached { get; }
    public ProviderStatus? Status { get; }
    public IReadOnlyList<ProviderStatus> Statuses { get; }
    public IReadOnlyList<VehicleReading> Readings { get; }
    public IReadOnlyList<PairingKnock> PairingKnocks { get; }
    public void Configure(ProviderOptions options);
    public Task<ProviderHello> WaitForHelloAsync(CancellationToken cancellationToken = default);
    public Task<ProviderStatus> WaitForStatusAsync(Func<ProviderStatus, bool>? match = null, CancellationToken cancellationToken = default);
    public Task<VehicleReading> WaitForReadingAsync(Func<VehicleReading, bool>? match = null, CancellationToken cancellationToken = default);
    public Task<VehicleReading> RefreshAsync(CancellationToken cancellationToken = default); // RefreshRequested, then the reading with InReplyTo set
    public Task SetCarAsync(VehicleInfo car);                     // VehicleChanged
    public Task SetSessionAsync(ChargingSessionInfo session);     // ChargingSessionChanged
}

public sealed class CarPluginHarness : IAsyncDisposable
{
    public CarPluginHarness(PluginInfo plugin, IDictionary<string, string?>? configuration = null);
    public PluginInfo Plugin { get; }
    public string Directory { get; }
    public IConfiguration Configuration { get; }
    public ProviderSecretStore Files { get; }
    public PluginSettings Settings { get; }
    public VehicleTestHub? Hub { get; }
    public VehicleProviderConnection? Connection { get; }
    public CarPoller? Poller { get; }
    // Starts a hub, then the connection, the poller and the status reporter, wired as CarPluginHost wires them.
    public Task<VehicleTestHub> ConnectAsync(PluginCar car, VehicleTestHubOptions? options = null, bool paired = false);
    public IHttpClientFactory FakeClients(HttpMessageHandler handler);
    public static Task Eventually(Func<bool> condition, string because);
}
```
The hub's welcome and its rules follow the server: refuse a wrong kind or major, and accept vehicle 1.x. Check against
`src/Gleanvolt.Providers.Server`'s vehicle hub, so a plugin that passes here passes against Gleanvolt.

- [ ] **Step 1: Write the failing tests.** In `VehicleTestHubTests.cs`, use a bare `VehicleProviderConnection`
  configured by `hub.Configure` with a fixed `IVehicleSource` (SOC 55, `CapturedAt` fixed):
  - `AHelloIsWelcomedWithTheCarAndTheSession`: after `WaitForHelloAsync`, the connection's `Car.Id` is `"test-car"` and
    its `Session` equals `ChargingSessionInfo.Closed`.
  - `TheFirstReadingFollowsTheHello`: `WaitForReadingAsync` returns SOC 55.
  - `ARefreshIsAnsweredInReplyToIt`: `RefreshAsync` returns a reading whose `InReplyTo` is not null.
  - `ASessionChangeReachesTheConnection`: after `SetSessionAsync(new(true, now, "SolarOnly"))`, `Eventually`
    `connection.Session?.Open == true`.
  - `AWrongMajorIsRefused`: a hello with major 2 is not accepted, and the hub's `IsAttached` stays false.
- [ ] **Step 2: Run them.** `… --filter VehicleTestHubTests`. Expected: FAIL to compile.
- [ ] **Step 3: Implement `VehicleTestHub`, `VehicleTestHubOptions` and `TestVehicleHub`** with the signatures above.
- [ ] **Step 4: Run them.** Expected: PASS. Then run the whole provider test project: still all pass.
- [ ] **Step 5: Commit.** "Providers.Testing: a vehicle test hub (#N2)".
  `CarPluginHarness` is committed with Task 5, since it needs `PluginCar`.

### Task 5: `Gleanvolt.Cars.Hosting`, the car base, the poll loop and the host

**Files:**
- Create `src/Gleanvolt.Cars.Hosting/Gleanvolt.Cars.Hosting.csproj`:
  - the Razor SDK, `RootNamespace` `Gleanvolt.Cars`;
  - `PackageId` `Gleanvolt.Cars.Hosting`, MIT;
  - references `Gleanvolt.Plugins.Hosting`;
  - `<Using Include="Gleanvolt.Plugins" />`, plus a packed `build`/`buildTransitive` props with the same global using,
    as the charger package has;
  - `InternalsVisibleTo` `Gleanvolt.Providers.Testing` and `Gleanvolt.Providers.Tests`.
- Create `src/Gleanvolt.Cars.Hosting/LICENSE` and `README.md`.
- Create `src/Gleanvolt.Cars.Hosting/PluginCar.cs`, `CarPollOptions.cs`, `CarPoller.cs`, `CarPluginHost.cs` and
  `CarText.cs`.
- Modify `Gleanvolt.slnx`.
- Create `src/Gleanvolt.Providers.Testing/CarPluginHarness.cs` (Task 4's interface), and add a reference to
  `Gleanvolt.Cars.Hosting` in `Gleanvolt.Providers.Testing.csproj`.
- Test: `tests/Gleanvolt.Providers.Tests/CarsHostingTests.cs`.

**Interfaces (namespace `Gleanvolt.Cars`):**
```csharp
// Bound from the plugin's section: <Section>:PollInterval, <Section>:IdlePollInterval, <Section>:MinimumTimeBetweenReads.
public sealed class CarPollOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(5);       // a session open, or the car charging
    public TimeSpan IdlePollInterval { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan MinimumTimeBetweenReads { get; set; } = TimeSpan.FromSeconds(30);
}

public abstract class PluginCar : IVehicleSource, IPluginStatus
{
    protected PluginCar(CarPollOptions options, TimeProvider time);
    public event Action? Changed;
    public VehicleReading? LastReading { get; }
    public DateTimeOffset? LastReadAt { get; }
    public ProviderStatus? Status { get; }
    public Words? StatusWords { get; }
    public bool IsBlocked { get; }
    public bool ChargingSessionOpen { get; internal set; }
    // Infinite while blocked; PollInterval while a session is open or LastReading.ChargeState is Charging; IdlePollInterval otherwise.
    public virtual TimeSpan NextDelay { get; }

    // Returns LastReading without calling FetchAsync when blocked or within MinimumTimeBetweenReads of LastReadAt.
    // Otherwise it holds a gate (concurrent callers wait, then see the fresh read), sets LastReadAt, calls FetchAsync,
    // and on a reading keeps it and calls Healthy(). A null reading keeps the last one and leaves the status.
    // An exception that is not the caller's own cancellation goes to OnFetchFailed.
    public Task<VehicleReading?> ReadAsync(VehicleInfo? car, CancellationToken cancellationToken);
    Task<VehicleReading?> IVehicleSource.GetReadingAsync(VehicleInfo car, CancellationToken cancellationToken); // => ReadAsync(car, …)

    // Reads the car from its maker. car is null before Gleanvolt's welcome.
    protected abstract Task<VehicleReading?> FetchAsync(VehicleInfo? car, CancellationToken cancellationToken);
    // Default: Problem(CarText.Unreachable.Format(ex.Message), ProviderStatusCodes.Unreachable, needsOwner: false).
    protected virtual void OnFetchFailed(Exception exception);
    protected void Healthy();
    protected void Problem(Words what, string code, bool needsOwner);
    // Stops all reads until Resume, with NeedsOwner = true and the given code (needs-sign-in, needs-code, not-set-up).
    protected void Block(Words what, string code);
    // Clears the block and LastReadAt, so the next read goes to the maker now.
    public void Resume();
    protected void RaiseChanged();
}

// Generalizes VolkswagenPoller (plugins/Gleanvolt.Plugin.Volkswagen/Volkswagen/VolkswagenFeed.cs).
// The loop: wait NextDelay or a Wake; skip while blocked; PublishNowAsync when attached, else car.ReadAsync(null, …).
// On SessionChanged: set car.ChargingSessionOpen, and Wake when it went from closed to open.
public sealed class CarPoller(VehicleProviderConnection connection, PluginCar car, ILogger<CarPoller> logger) : BackgroundService
{
    public void Wake();
}

public static class CarPluginHost
{
    // PluginWebHost.CreateBuilder with AddGleanvoltVehicleProvider<TCar>. Also registers CarPollOptions bound from
    // plugin.Section; TCar and PluginCar resolved as (TCar)IVehicleSource; CarPoller as a singleton and a hosted
    // service; and PluginStatusReporter<VehicleWelcome> over PluginCar.
    public static WebApplicationBuilder CreateBuilder<TCar>(string[] args, PluginInfo plugin) where TCar : PluginCar;
    public static WebApplication Build(WebApplicationBuilder builder); // PluginWebHost.Build(builder)
}
```
`CarText` is a `public static class` of `Words`. Its copy is in Task 6.

- [ ] **Step 1: Write the failing tests.** In `CarsHostingTests.cs`, use a private `CountingCar : PluginCar`.
  - It counts `FetchAsync` calls, returns a scripted reading, throws, or calls `Block` on demand.
  - Use a `FakeTimeProvider` (Microsoft.Extensions.TimeProvider.Testing, which the test project already references).

  The tests:
  - `ThreeReasonsToReadAtOnceMakeOneRequest`: three concurrent `ReadAsync` calls make one fetch, and all three get the
    same reading.
  - `AReadWithinTheMinimumIsAnsweredFromTheLastOne`: two reads 10 s apart make one fetch. A third, 31 s after the
    first, makes two fetches in all.
  - `ABlockedCarAsksNothingUntilResumed`:
    - after `Block(…, ProviderStatusCodes.NeedsSignIn)`, a read makes no fetch;
    - `Status.NeedsOwner` is true, `Status.Code` is `"needs-sign-in"`, and `NextDelay` is
      `Timeout.InfiniteTimeSpan`;
    - after `Resume()`, a read makes one fetch.
  - `AFailedReadReportsUnreachableAndTheNextOneHeals`: the fetch throws `HttpRequestException("boom")`. The status is
    unhealthy, with code `"unreachable"` and `NeedsOwner` false. The next successful read is healthy.
  - `AStopIsNotAProblem`: a fetch cancelled by the caller's token throws `OperationCanceledException`, and the status
    is unchanged.
  - `TheClockIsFasterWhileASessionIsOpenOrTheCarCharges`: `NextDelay` is:
    - 15 min idle;
    - 5 min with `ChargingSessionOpen`;
    - 5 min with a last reading `ChargeState = Charging`.
  - `AnUnpairedPluginReadsButPublishesNothing`: through `CarPluginHarness` with no hub connected, `poller.Wake()` makes
    one fetch, and `LastReading` is set.
  - `APairedPluginPublishesOnTheClockAndOnASessionOpening`:
    - through `CarPluginHarness.ConnectAsync`, the hub gets a reading after the hello;
    - after `SetSessionAsync(new(true, now, "Targeted"))` and the clock moved past 30 s, the hub gets another reading
      without the poll interval elapsing;
    - the hub's `Statuses` has the healthy status.
- [ ] **Step 2: Run them.** `… --filter CarsHostingTests`. Expected: FAIL to compile.
- [ ] **Step 3: Implement the project and the types** with the signatures above. Port the timing and gate logic from
  `VolkswagenFeed.ReadAsync`, `NextDelay` and `VolkswagenPoller`, generalized. Leave the Volkswagen plugin itself
  alone; it moves in step 3 of the spec.
- [ ] **Step 4: Implement `CarPluginHarness`.**
- [ ] **Step 5: Run them.** Expected: PASS. Then run the whole provider test project: all pass.
- [ ] **Step 6: Commit.** "Cars.Hosting: a car plugin's base, its clock and its host (#N2)".

### Task 6: `CarFrame`, every car plugin's page

**Files:**
- Create `src/Gleanvolt.Cars.Hosting/Components/CarFrame.razor` and `Components/_Imports.razor`.
- Modify `src/Gleanvolt.Cars.Hosting/CarText.cs`.
- Test: `tests/Gleanvolt.Providers.Tests/CarsHostingTests.cs`, plus a test page component
  `tests/Gleanvolt.Providers.Tests/TestCarPage.razor` (`@page "/"`, which renders `<CarFrame />`), as `TestPluginPage`
  does for chargers.

**Interfaces:**
- Consumes: `PluginCar`, `CarPoller`, `VehicleProviderConnection` (`Car`, `Session`, `Welcome`, `Welcomed`,
  `SessionChanged`), `ProviderCredentials.Source` and `PluginInfo`.
- Produces: `<CarFrame>`, with `[Parameter] RenderFragment? ChildContent` (the plugin's own view, under the reading) and
  `[Parameter] RenderFragment? Settings` (the account and sign-in sections).

Layout, mirroring `PluginFrame.razor`:
- `h1` with the plugin name;
- `#status`, with the class `ok` or `error` as in `PluginFrame`;
- a "Gleanvolt" section:
  - `#car` (Name, Make Model) or `#not-connected`;
  - `#session` (open, with its mode, or closed);
  - `#token-source`;
- a "Car" section:
  - `#reading` as a `dl`: SOC, charge state, cable, range in km, time to full, measured by the car, read at.
    `HostText.NotSaid` stands for any null.
  - `#reading-none`;
  - a `#read-now` button that calls `CarPoller.Wake()`;
  - `ChildContent`;
- then `Settings`.

Times are shown in the welcome's `TimeZone`, as `PluginFrame.Local` does. Renders from other threads go through
`ViewerCulture.Wrap`.

The copy for `CarText`, exact, as `Words(En, Cs)`:

| Name | En | Cs |
|---|---|---|
| StatusWaiting | Waiting for the first reading. | Čeká se na první údaje. |
| StatusReading | Reading the car. | Auto se čte. |
| StatusNotReading | Not reading the car. | Auto se nečte. |
| Unreachable | The car's service could not be reached: {0} | Služba auta není dostupná: {0} |
| Car | Car | Auto |
| GleanvoltCar | Gleanvolt's car | Auto v Gleanvoltu |
| SessionOpen | Gleanvolt is charging ({0}). | Gleanvolt nabíjí ({0}). |
| SessionClosed | Gleanvolt is not charging. | Gleanvolt nenabíjí. |
| Soc | State of charge | Stav nabití |
| ChargeState | Charging | Nabíjení |
| ChargeIdle | Not charging | Nenabíjí |
| ChargeCharging | Charging | Nabíjí |
| ChargeComplete | Finished | Dokončeno |
| Cable | Cable | Kabel |
| PlugConnected | Plugged in | Připojen |
| PlugDisconnected | Unplugged | Odpojen |
| Range | Range | Dojezd |
| TimeToFull | Time to full (the car's estimate) | Doba do nabití (odhad auta) |
| CapturedAt | Measured by the car | Změřeno autem |
| ReadNow | Read now | Načíst teď |

- [ ] **Step 1: Write the failing tests.** Use a `CarApp` fixture like `PluginApp`, built with
  `CarPluginHost.CreateBuilder<CountingCar>`, `PageAssembly = typeof(TestCarPage).Assembly`, and
  `--applicationName=Gleanvolt.Plugins.Hosting`. Save credentials to `http://127.0.0.1:9` and request
  `/?ticket=…`, as `ThePageSaysWhereTheTokenCameFrom` does.
  - `TheCarPageShowsTheLastReading`: after a read returning SOC 61.5, `Charging`, `Connected`, range 312000 m and
    remaining 1 h 20 min, the body contains `id="reading"`, `61.5`, `Charging`, `Plugged in`, `312 km` and `1:20`.
  - `TheCarPageIsInCzechForCzech`: with `&culture=cs`, the body contains `Stav nabití` and `Připojen`.
  - `TheCarPageSaysWhereTheTokenCameFrom`: the body contains `HostText.TokenFromPairing.En`.
  - `BeforeAnyReadingThePageSaysSo`: with no read, the body contains `id="reading-none"`.
- [ ] **Step 2: Run them.** `… --filter CarsHostingTests`. Expected: the four new tests FAIL.
- [ ] **Step 3: Implement `CarFrame.razor` and the `CarText` entries** as above.
- [ ] **Step 4: Run them.** Expected: PASS. Then run the whole provider test project: all pass.
- [ ] **Step 5: Commit.** "Cars.Hosting: the car plugin's page (#N2)".

### Task 7: Packaging, docs and PR 2

**Files:**
- Modify `.github/workflows/release.yml`'s four package lists, as in Task 3: add `Gleanvolt.Cars.Hosting` and
  `Cars.Hosting`.
- Modify `docs/PROVIDERS.md` "The vehicle kind" (around line 600): a subsection, "A car plugin on `Gleanvolt.Cars.Hosting`".
  It covers `PluginCar`, `FetchAsync`, `Block`/`Resume`, `CarPollOptions` and its keys, `CarFrame`'s two slots, and
  testing with `CarPluginHarness`.
- Modify `src/Gleanvolt.Providers.Testing/README.md` and `src/Gleanvolt.Cars.Hosting/README.md`.
- Modify `docs/DECISIONS.md` and `docs/IMPLEMENTATION_LOG.md`. Record the calls made here:
  - there's no recording for car plugins, and why;
  - the default clock values, taken from the Volkswagen plugin;
  - a null fetch keeps the last reading.

- [ ] **Step 1: Pack to a scratch feed.** `dotnet pack` the seven MIT packages (Task 3's six plus
  `Gleanvolt.Cars.Hosting`), with `-p:Version=1.3.3-local.2`. Expected: `unzip -l` of `Gleanvolt.Cars.Hosting` lists
  `buildTransitive/Gleanvolt.Cars.Hosting.props` and `staticwebassets`.
- [ ] **Step 2: Run the full Release test pass.** `DOTNET_USE_POLLING_FILE_WATCHER=true dotnet test Gleanvolt.slnx -c Release`.
  Expected: all pass.
- [ ] **Step 3: Write the docs**, the DECISIONS record and the IMPLEMENTATION_LOG entry.
- [ ] **Step 4: Commit, push and open the PR.**
  - Commit "Cars.Hosting: packaged and documented (#N2)".
  - Check that PR 1 is merged (`gh pr view <PR1> --json state` says `MERGED`) and that this branch came from
    `origin/main` after it.
  - Push, then `gh pr create -t "The car layer for car plugins, and a vehicle test hub (#N2)"`.
- [ ] **Step 5: Ask the owner whether to hand-upload a `1.3.3-preview.N` of the seven packages to nuget.org.**
  `gleanvolt-cars` (step 3) needs it for CI. Upload only after an explicit yes.
