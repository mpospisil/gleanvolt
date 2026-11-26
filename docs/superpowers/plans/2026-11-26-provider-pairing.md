# Provider Pairing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A provider plugin starts with no token, finds Gleanvolt when it appears, asks to be paired, and receives the
shared provider token when the owner presses Accept in Gleanvolt, with no restart and no config edit.

**Architecture:** Two unauthenticated HTTP endpoints on Gleanvolt (`/providers/pairing`) hold an in-memory waiting
list (`PairingRequests`); the owner accepts from Settings → External providers or the Wizard's Finish step. On the
plugin side a `ProviderCredentials` singleton replaces the fixed `ProviderOptions.Token`: the connection loop, the
page guard and the frame policy read it, and a `ProviderPairing` background service fills it. Config still wins.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, SignalR, Blazor (server), xUnit, bUnit, `FakeTimeProvider`.

**Spec:** `docs/superpowers/specs/2026-11-26-provider-pairing-design.md`

## Global Constraints

- `TreatWarningsAsErrors` is on: no new warnings, XML docs on every public member (the projects emit docs).
- Every text a person sees exists in English and Czech: `Words(en, cs)` in `Gleanvolt.Chargers.Hosting`, the `.resx`
  pair (`*.resx` + `*.cs.resx`) in `Gleanvolt.Web`.
- `Gleanvolt.Providers.Client` and `Gleanvolt.Chargers.Hosting` must not reference `Gleanvolt.Infrastructure` or
  `Gleanvolt.Core` (they ship to nuget.org for plugin authors).
- The token's order: `Gleanvolt:Token` in config (empty or whitespace counts as unset), then the paired token in the
  data directory, then pairing.
- Pairing code: six digits, shown as `482 193`. Pairing secret: ≥128 random bits, kept in the data directory.
- Waiting list: at most 16 entries (oldest dropped), an entry expires 15 minutes after its last knock or poll, memory
  only.
- Plugin: search backoff 2 s doubling to 1 min; poll every 3 s while waiting; after a reject, knock again only after
  1 hour or a restart. Log at Information on a change of state, Debug otherwise.
- A knock from a loopback address is "this machine" and needs no code check; any other shows the code-check hint.
- Gleanvolt never links to or frames the `PageUrl` of a knock that has not been accepted.
- After pairing the plugin uses only the endpoint it was accepted at; no mDNS on restart.
- A plugin with `Gleanvolt:Token` in config and no endpoint keeps `http://localhost:8090`.

## Review Focus

1. **The owner presses Accept, then "Start Gleanvolt" in the Wizard, before the plugin's next poll.** The list is
   memory only; the plugin's collect gets `Unknown` and must knock again and reappear, not stall. (Task 5 test
   `AnUnknownRequestIsKnockedAgain`.)
2. **The data directory is not writable** (a volume owned by another uid). The paired token stays in use in memory
   for this run, as `SavedSetting` does, and a warning is logged; the plugin must not loop on pairing. (Task 4 test
   `APairedTokenThatCannotBeWrittenIsStillUsed`.)
3. **`Gleanvolt__Token=` set but empty**, as a compose file with an unset variable gives. Counts as no token; the
   plugin pairs. (Task 4 test `AnEmptyConfiguredTokenCountsAsNone`.)
4. **The owner makes a new token in Gleanvolt** while plugins are paired. A paired plugin refused with 401 forgets its
   token and endpoint and pairs again; a configured one keeps its token and retries. (Task 4 tests
   `ARefusedPairedTokenIsForgotten`, `ARefusedConfiguredTokenIsKept`.)
5. **A browser with an old page cookie opens the plugin after it went back to pairing.** It gets the waiting view, not
   a 403 or the settings page. (Task 6 test `AStaleCookieWhileUnpairedSeesTheWaitingView`.)

---

### Task 1: The pairing contract

**Files:**
- Create: `src/Gleanvolt.Providers.Contracts/Pairing.cs`
- Test: `tests/Gleanvolt.Providers.Tests/PairingContractTests.cs`

**Interfaces:**
- Produces:
  - `public static class PairingPaths { public const string Knock = "/providers/pairing"; public static string Collect(string requestId) => $"/providers/pairing/{requestId}/collect"; }`
  - `public sealed record PairingKnock(string HubPath, string ProviderId, string Name, string Version, Uri? PageUrl, string SecretHash);`
    The hub path (e.g. `/providers/charger`) names the kind, as the hub URL already does; the server resolves it with
    `ExternalProviderRegistry.KindAt`.
  - `public sealed record PairingTicket(string RequestId);`: the knock's answer.
  - `public sealed record PairingCollect(string Secret);`
  - `public enum PairingState { Waiting, Accepted, Rejected, Unknown }`
  - `public sealed record PairingAnswer(PairingState State, string? Token = null);`
  - `public static class PairingSecret { string Create(); string Hash(string secret); string Code(string secretHash); }`

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void ASecretIsLongAndNew() { var a = PairingSecret.Create(); Assert.True(a.Length >= 22); Assert.NotEqual(a, PairingSecret.Create()); }
[Fact] public void TheHashIsStableAndHidesTheSecret() { var s = PairingSecret.Create(); Assert.Equal(PairingSecret.Hash(s), PairingSecret.Hash(s)); Assert.DoesNotContain(s, PairingSecret.Hash(s)); }
[Fact] public void TheCodeIsSixDigitsInTwoGroups() => Assert.Matches(@"^\d{3} \d{3}$", PairingSecret.Code(PairingSecret.Hash(PairingSecret.Create())));
[Fact] public void BothSidesDeriveTheSameCode() { var h = PairingSecret.Hash("fixed-secret"); Assert.Equal(PairingSecret.Code(h), PairingSecret.Code(h)); }
[Fact] public void TheCollectPathCarriesTheRequest() => Assert.Equal("/providers/pairing/abc/collect", PairingPaths.Collect("abc"));
```

- [ ] **Step 2: Run, expect a compile failure**

Run: `dotnet test tests/Gleanvolt.Providers.Tests --filter FullyQualifiedName~PairingContractTests`
Expected: FAIL, `PairingSecret` does not exist.

- [ ] **Step 3: Implement `Pairing.cs`**

`Create`: 32 bytes from `RandomNumberGenerator`, base64url without padding. `Hash`: SHA-256 of the UTF-8 secret,
base64url. `Code`: decode the hash, take the first four bytes as a big-endian `uint`, `% 1_000_000`, format `D6`,
insert a space after the third digit.

- [ ] **Step 4: Run, expect PASS** (same command).

- [ ] **Step 5: Commit**: `git commit -m "Pairing: the contract (knock, collect, the code)"`

---

### Task 2: The waiting list

**Files:**
- Create: `src/Gleanvolt.Providers.Server/PairingRequests.cs`
- Test: `tests/Gleanvolt.Providers.Tests/PairingRequestsTests.cs`

**Interfaces:**
- Consumes: Task 1 records and `PairingSecret.Code`.
- Produces:
  - `internal sealed record PairingEntry(string Id, ProviderKind Kind, PairingKnock Knock, IPAddress Source, string Code, DateTimeOffset Since, DateTimeOffset LastSeen)` with `bool OnThisMachine => IPAddress.IsLoopback(Source)`.
  - `public sealed class PairingRequests(TimeProvider time)` with:
    - `internal PairingEntry? Knock(ProviderKind kind, PairingKnock knock, IPAddress source)`: null when the source is over its rate limit.
    - `internal PairingAnswer Collect(string requestId, string secret)`
    - `internal IReadOnlyList<PairingEntry> Waiting { get; }` (expired entries dropped on read)
    - `internal bool IsDuplicate(PairingEntry entry)`: another waiting entry has the same `ProviderId` from a different source.
    - `internal bool Accept(string requestId, string token)`: false when unknown or duplicate.
    - `internal void Reject(string requestId)`
    - `public event Action? Changed;`
  - Constants: `MaxEntries = 16`, `Expiry = 15 min`, `KnocksPerMinutePerSource = 10`.

- [ ] **Step 1: Write the failing tests** (a `FakeTimeProvider`; `Kind` is a test `ProviderKind`):

```csharp
KnockIsListedWithTheCodeOfItsSecretHash()          // entry.Code == PairingSecret.Code(knock.SecretHash)
ALoopbackKnockIsOnThisMachine()                     // IPAddress.Loopback and IPv6Loopback → OnThisMachine
TheSameProviderFromTheSameAddressReplacesItsEntry() // two knocks, new hash → Waiting.Count == 1, newer hash kept
TheSameProviderFromTwoAddressesIsADuplicate()       // both listed, IsDuplicate both true, Accept returns false
AWaitingCollectSaysWaiting()                        // Collect(id, secret).State == Waiting
TheWrongSecretIsUnknown()                           // Collect(id, "other").State == Unknown, entry still listed
AnAcceptedCollectGetsTheTokenOnceAndIsGone()        // Accepted + token, then Unknown, Waiting empty
ARejectIsToldOnceAndIsGone()                        // Rejected, then Unknown
AnEntryExpiresFifteenMinutesAfterItWasLastSeen()    // Advance 14 min + collect keeps it; then 15 min idle drops it
TheSeventeenthEntryDropsTheOldest()
TheEleventhKnockInAMinuteFromOneAddressIsRefused()  // returns null; after Advance(1 min) accepted again
```

- [ ] **Step 2: Run, expect FAIL**: `dotnet test tests/Gleanvolt.Providers.Tests --filter FullyQualifiedName~PairingRequestsTests`

- [ ] **Step 3: Implement `PairingRequests`**, a `lock` around a list; ids from `Guid.NewGuid().ToString("N")`;
  `Collect` compares `PairingSecret.Hash(secret)` with the knock's hash using `CryptographicOperations.FixedTimeEquals`
  and refreshes `LastSeen` when it matches; a per-source sliding window of knock times for the rate limit.

- [ ] **Step 4: Run, expect PASS.**

- [ ] **Step 5: Commit**: `git commit -m "Pairing: Gleanvolt's waiting list"`

---

### Task 3: Gleanvolt's pairing endpoints and Accept/Reject

**Files:**
- Modify: `src/Gleanvolt.Providers.Server/ExternalProviderExtensions.cs` (register `PairingRequests` in `AddShared`; add `MapProviderPairing`)
- Modify: `src/Gleanvolt.Providers.Server/ExternalProviderRegistry.cs` (pairing members)
- Modify: `src/Gleanvolt.Core/Interfaces/IExternalProviders.cs` (pairing members, view record)
- Modify: `src/Gleanvolt.Hosting/GleanvoltHostingExtensions.cs:1197-1213` (map pairing also in setup mode)
- Modify: `tests/Gleanvolt.Providers.Tests/TestHost.cs` (map pairing; `PostAsync` helper with a source address)
- Modify: every fake of `IExternalProviders` under `tests/` (compile)
- Test: `tests/Gleanvolt.Providers.Tests/PairingEndpointTests.cs`, `tests/Gleanvolt.Hosting.Tests` (setup-mode mapping)

**Interfaces:**
- Consumes: Task 2.
- Produces (in `Gleanvolt.Core.Interfaces`):
  - `public enum PairingBlock { None, Duplicate, KindOff, AnotherProvider }`
  - `public sealed record PairingRequestView(string Id, string Kind, string ProviderId, string ProviderName, string Version, string Source, bool OnThisMachine, string Code, DateTimeOffset Since, PairingBlock Block);`
  - On `IExternalProviders`: `IReadOnlyList<PairingRequestView> Pairing { get; }`, `Task<bool> AcceptPairingAsync(string id)`, `void RejectPairing(string id)`. `Changed` is also raised when the list changes.
- `ExternalProviderRegistry.AcceptPairingAsync`: false when `Block != None`; the token is `Tokens.Read()`, or `CreateTokenAsync()` when none exists yet.
- `Block`: `Duplicate` from `IsDuplicate`; `KindOff` when `!IsEnabled(kind)`; `AnotherProvider` when `IRememberedProviders.Find(kind.Id)` is set and differs from the knock's provider id.
- `public static IEndpointRouteBuilder MapProviderPairing(this IEndpointRouteBuilder endpoints)`:
  - `POST /providers/pairing`, body `PairingKnock` → 200 `PairingTicket`; 400 when the hub path is no kind's, the ids
    fail `^[a-z0-9-]{1,64}$`, `Name` > 100 or `Version` > 64 characters, or `PageUrl` is not absolute http(s); 429 when
    `Knock` returns null.
  - `POST /providers/pairing/{id}/collect`, body `PairingCollect` → 200 `PairingAnswer`.
  - Both `AllowAnonymous`; request bodies over 4 KiB are refused (413).

- [ ] **Step 1: Write the failing tests** (`TestHost`, knocks posted with `from` addresses):

```csharp
AKnockIsListedForTheOwner()                         // Registry.Pairing has one view, Code matches, OnThisMachine for 127.0.0.1
AnAcceptedPluginCollectsATokenThatOpensTheHub()     // AcceptPairingAsync → collect Accepted; ConnectAsync(token) + Hello accepted
AcceptCreatesTheTokenWhenThereIsNone()              // host started without CreateTokenAsync; accept → Tokens.Exists
AKnockForAKindThatIsOffIsBlocked()                  // Enabled = false → Block == KindOff, Accept false
AKnockForAnUnknownHubIsRefused()                    // 400
ALongNameIsRefused()                                // 400
AnOversizedBodyIsRefused()                          // 413
TheEleventhKnockInAMinuteIsTooMany()                // 429
```

And in `Gleanvolt.Hosting.Tests`: `PairingIsAnsweredInSetupMode` (POST knock → 200 while `SetupMode` is true).

- [ ] **Step 2: Run, expect FAIL**: `dotnet test tests/Gleanvolt.Providers.Tests --filter FullyQualifiedName~PairingEndpointTests`

- [ ] **Step 3: Implement.** In `GleanvoltHostingExtensions`, call `app.MapProviderPairing()` wherever the registry is
  registered and web or API is enabled, outside the `!SetupMode` condition; the hubs stay inside it.

- [ ] **Step 4: Run the whole provider, hosting and web test projects, expect PASS.**

Run: `dotnet test tests/Gleanvolt.Providers.Tests && dotnet test tests/Gleanvolt.Hosting.Tests && dotnet test tests/Gleanvolt.Web.Tests`

- [ ] **Step 5: Commit**: `git commit -m "Pairing: Gleanvolt's endpoints, Accept and Reject"`

---

### Task 4: The plugin's credentials

**Files:**
- Create: `src/Gleanvolt.Providers.Client/ProviderCredentials.cs`
- Modify: `src/Gleanvolt.Providers.Client/ProviderOptions.cs` (doc: Token and Endpoint optional)
- Modify: `src/Gleanvolt.Providers.Client/ProviderConnection.cs` (read credentials; wait for a token; forget on 401)
- Modify: `src/Gleanvolt.Providers.Client/ProviderServiceCollectionExtensions.cs` and every `AddGleanvolt*Provider` (register credentials)
- Test: `tests/Gleanvolt.Providers.Tests/ProviderCredentialsTests.cs`

**Interfaces:**
- Produces:
  - `public enum CredentialSource { None, Configuration, Paired }`
  - `public sealed class ProviderCredentials`:
    - `public ProviderCredentials(IOptions<ProviderOptions> options, IProviderSecretStore? store = null)`
    - `public Uri Endpoint { get; }`: the paired endpoint when paired, else `ProviderOptions.Endpoint`, else `http://localhost:8090`.
    - `public string? Token { get; }`, `public CredentialSource Source { get; }`
    - `public bool Save(Uri endpoint, string token)`: in use either way; false when the store would not write it.
    - `public void ForgetPaired()`: no-op when `Source == Configuration`.
    - `public event Action? Changed;`
    - `public Task WaitForTokenAsync(CancellationToken cancellationToken)`
  - Store file names: `gleanvolt-token`, `gleanvolt-endpoint`.
  - `ProviderConnection<TWelcome>` ctor gains a trailing optional `ProviderCredentials? credentials = null` (built from
    the options alone when null); subclasses in the SDK pass the registered one. `Validate()` no longer requires
    `Endpoint` or `Token`.
  - Registration: `services.TryAddSingleton<ProviderCredentials>()` using `IProviderSecretStore` when registered.

- [ ] **Step 1: Write the failing tests** (a temp directory with `ProviderSecretStore`; `TestHost` for the connection ones):

```csharp
AConfiguredTokenWins()                              // config "t1" + saved "t2" → Token "t1", Source Configuration
AnEmptyConfiguredTokenCountsAsNone()                // Token = "  " → Source None
APairedTokenSurvivesARestart()                      // Save(endpoint, "t") → new instance on same dir → Paired, Endpoint == saved
APairedTokenThatCannotBeWrittenIsStillUsed()        // store whose Write returns false → Save false, Token "t", Changed raised
ForgettingClearsTokenAndEndpoint()                  // files gone; Endpoint back to the default
AConnectionWithoutATokenWaitsAndThenAttaches()      // start the echo connection with no token: no throw; Save(host, Token) → IsAttached eventually
ARefusedPairedTokenIsForgotten()                    // Save(host, "wrong") → connect gets 401 → Source None
ARefusedConfiguredTokenIsKept()                     // config "wrong" → 401 → Source Configuration, still retrying
```

- [ ] **Step 2: Run, expect FAIL**: `dotnet test tests/Gleanvolt.Providers.Tests --filter FullyQualifiedName~ProviderCredentialsTests`

- [ ] **Step 3: Implement.** In `ExecuteAsync`, `await credentials.WaitForTokenAsync(stoppingToken)` before each
  session; build the hub URI from `credentials.Endpoint` each session; an `HttpRequestException` with
  `StatusCode == Unauthorized` while `Source == Paired` calls `ForgetPaired()` and logs once at Warning.

- [ ] **Step 4: Run the whole provider test project, expect PASS**: `dotnet test tests/Gleanvolt.Providers.Tests`

- [ ] **Step 5: Commit**: `git commit -m "Providers: credentials that can arrive while the provider runs"`

---

### Task 5: The plugin finds Gleanvolt and pairs

**Files:**
- Create: `src/Gleanvolt.Providers.Client/GleanvoltFinder.cs` (`IGleanvoltFinder` and the default)
- Create: `src/Gleanvolt.Providers.Client/MdnsBrowser.cs` (internal)
- Create: `src/Gleanvolt.Providers.Client/ProviderPairing.cs`
- Modify: the `AddGleanvolt*Provider` registrations (add `ProviderPairing` as a hosted service, and the finder)
- Modify: `tests/Gleanvolt.Providers.Tests/Gleanvolt.Providers.Tests.csproj` (reference `Gleanvolt.Infrastructure` for encoding test DNS answers)
- Test: `tests/Gleanvolt.Providers.Tests/ProviderPairingTests.cs`, `tests/Gleanvolt.Providers.Tests/MdnsBrowserTests.cs`

**Interfaces:**
- Consumes: Task 1, Task 4 (`ProviderCredentials`), the connection's hub path and `ProviderOptions`.
- Produces:
  - `public interface IGleanvoltFinder { IAsyncEnumerable<Uri> CandidatesAsync(CancellationToken cancellationToken); }`:
    the default yields `ProviderOptions.Endpoint` if set, `http://localhost:8090`, then each `_gleanvolt._tcp` answer.
  - `internal static class MdnsBrowser { Task<IReadOnlyList<Uri>> BrowseAsync(TimeSpan timeout, CancellationToken ct); internal static IReadOnlyList<Uri> Parse(ReadOnlySpan<byte> response); }`:
    one PTR query for `_gleanvolt._tcp.local` to 224.0.0.251:5353, then SRV (port, target) and A (address) from
    answers and additional records. Timeout 2 s. A small self-contained parser, not a copy of Infrastructure's.
  - `public enum PairingPhase { NotNeeded, Searching, Waiting, Rejected }`
  - `public sealed record PairingStatus(PairingPhase Phase, string Code, Uri? Gleanvolt);`
  - `public sealed class ProviderPairing : BackgroundService { public PairingStatus Status { get; } public event Action? Changed; }`
    with ctor `(ProviderOptions, ProviderCredentials, IGleanvoltFinder, IProviderSecretStore?, IHttpClientFactory?, TimeProvider, ILogger<ProviderPairing>, string hubPath)`.
    The registration passes the kind connection's hub path.
  - Store file: `gleanvolt-pairing-secret`, deleted after a successful pairing.

- [ ] **Step 1: Write the failing tests** (`TestHost` as Gleanvolt, a fake finder yielding its base address, the
  `TestServer` handler as the HTTP client, `FakeTimeProvider`):

```csharp
APluginWithoutATokenKnocksWithItsCode()             // Registry.Pairing[0].Code == pairing.Status.Code; Phase Waiting
AcceptingPairsAndConnects()                         // AcceptPairingAsync → credentials.Source Paired, Endpoint == host; connection IsAttached
APluginWithAConfiguredTokenDoesNotKnock()           // Phase NotNeeded; Registry.Pairing empty
TheCodeStaysAcrossARestart()                        // new ProviderPairing on the same store → same Code
ARejectedPluginWaitsAnHour()                        // RejectPairing → Phase Rejected; Advance 59 min → no knock; 61 min → knocks
AnUnknownRequestIsKnockedAgain()                    // restart TestHost between knock and accept → plugin reappears in the new Registry.Pairing
NoGleanvoltYetKeepsSearching()                      // finder yields nothing → Phase Searching, no throw; later yields host → Waiting
AForgottenTokenPairsAgain()                         // credentials.ForgetPaired() after pairing → Phase Waiting, a new knock
```

`MdnsBrowserTests.ParsesTheServiceGleanvoltAdvertises`: encode a response with the PTR, SRV (port 8090, target
`gleanvolt.local`) and A (192.168.1.5) records using `Gleanvolt.Infrastructure.Mdns.DnsMessage`, then
`Parse(...)` == `[http://192.168.1.5:8090/]`.

- [ ] **Step 2: Run, expect FAIL**: `dotnet test tests/Gleanvolt.Providers.Tests --filter "FullyQualifiedName~ProviderPairingTests|FullyQualifiedName~MdnsBrowserTests"`

- [ ] **Step 3: Implement.** The loop: `NotNeeded` while `credentials.Token` is set (wake on `Changed`); otherwise
  search, knock, poll every 3 s; `Accepted` → `credentials.Save(candidate, token)`, delete the secret; `Rejected` →
  wait 1 h; `Unknown` → knock again; a failed request → search again with the 2 s→1 min backoff.

- [ ] **Step 4: Run the whole provider test project, expect PASS.**

- [ ] **Step 5: Commit**: `git commit -m "Providers: find Gleanvolt and ask to be paired"`

---

### Task 6: The page guard while unpaired

**Files:**
- Modify: `src/Gleanvolt.Providers.Client/ProviderPageMiddleware.cs`
- Test: `tests/Gleanvolt.Providers.Tests/ProviderPageTests.cs` (add)

**Interfaces:**
- Consumes: Task 4 `ProviderCredentials`, Task 5 `ProviderPairing.Status`.
- Produces: `ProviderPageOptions.Unpaired` (`RequestDelegate?`): answers `GET /` while `credentials.Token` is null.
  The default writes `text/plain` with the plugin's name, the phase and the code. The middleware reads the token
  from `ProviderCredentials` per request instead of `ProviderOptions.Token`.

- [ ] **Step 1: Write the failing tests:**

```csharp
UnpairedTheRootShowsTheWaitingView()                // GET / → 200, body contains Status.Code
UnpairedEverythingElseIsRefused()                   // POST /, GET /recording → 403
UnpairedTheFrameworkIsStillServed()                 // GET /_framework/blazor.web.js passes as today
AStaleCookieWhileUnpairedSeesTheWaitingView()       // cookie signed with the old token, then ForgetPaired → GET / → waiting view
OncePairedATicketSignedWithTheNewTokenOpensThePage()
```

- [ ] **Step 2: Run, expect FAIL**: `dotnet test tests/Gleanvolt.Providers.Tests --filter FullyQualifiedName~ProviderPageTests`
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit**: `git commit -m "Providers: the page shows only the pairing state until paired"`

---

### Task 7: The charger plugin host

**Files:**
- Modify: `src/Gleanvolt.Chargers.Hosting/PluginHost.cs` (no throw; credentials; frame policy per request; waiting view)
- Create: `src/Gleanvolt.Chargers.Hosting/WaitingPage.cs` (the HTML of the waiting view)
- Modify: `src/Gleanvolt.Chargers.Hosting/SharedText.cs` (texts)
- Modify: `src/Gleanvolt.Chargers.Hosting/Components/PluginFrame.razor` (where the token came from)
- Modify: `src/Gleanvolt.Chargers.Hosting/README.md`
- Test: `tests/Gleanvolt.Providers.Tests/ChargersHostingTests.cs` (add)

**Interfaces:**
- Consumes: Tasks 4–6.
- Produces:
  - `PageFraming` becomes `public sealed class PageFraming(ProviderCredentials credentials, string? configured)` with
    `string FrameAncestors()` computed per call from `credentials.Endpoint` through `ProviderPageFraming.Sources`.
    `Build` sets `ContentSecurityFrameAncestorsPolicy = null` and adds a middleware that writes
    `Content-Security-Policy: frame-ancestors <FrameAncestors()>` on every response.
  - `internal static class WaitingPage { static string Html(PluginInfo plugin, PairingStatus status, IEnumerable<IPAddress> addresses, CultureInfo culture); }`,
    installed as `ProviderPageOptions.Unpaired`. It shows the plugin name, the phase, the code, the plugin's LAN
    addresses (`LanScan`'s interface enumeration, IPv4, not loopback), and `<meta http-equiv="refresh" content="10">`
    so it moves on by itself once paired.
  - `SharedText`: `PairingSearching` ("Looking for Gleanvolt on this network." / "Hledá se Gleanvolt v této síti."),
    `PairingWaiting` ("Waiting for the owner to accept this plugin in Gleanvolt: Settings → External providers." /
    "Čeká se, až vlastník tento plugin přijme v Gleanvoltu: Nastavení → Externí poskytovatelé."), `PairingRejected`
    ("Gleanvolt rejected this plugin. It asks again in an hour, or when restarted." / "Gleanvolt tento plugin
    odmítl. Znovu se zeptá za hodinu nebo po restartu."), `PairingCode` ("Pairing code" / "Kód pro spárování"),
    `PairingAddresses` ("This plugin's addresses" / "Adresy tohoto pluginu"), `TokenFromConfiguration` ("Connected
    with the token from the configuration." / "Připojeno tokenem z konfigurace."), `TokenFromPairing` ("Connected
    with the token from pairing." / "Připojeno tokenem ze spárování.").

- [ ] **Step 1: Write the failing tests** (start a plugin app on `TestServer` with no token):

```csharp
APluginStartsWithoutAToken()                        // CreateBuilder + Build without Gleanvolt:Token → no throw; GET / → 200 waiting HTML with the code
TheWaitingViewIsInCzechForCzech()                   // ?culture=cs → contains "Kód pro spárování"
TheFramePolicyFollowsThePairedEndpoint()            // credentials.Save(http://192.168.1.5:8090) → CSP frame-ancestors contains "http://192.168.1.5:8090"
AConfiguredEndpointStillFramesAsBefore()            // existing ThePageMayBeFramedByGleanvolt… cases keep passing
ThePageSaysWhereTheTokenCameFrom()                  // paired, GET / with a valid ticket → HTML contains TokenFromPairing's English text
```

- [ ] **Step 2: Run, expect FAIL**: `dotnet test tests/Gleanvolt.Providers.Tests --filter FullyQualifiedName~ChargersHostingTests`
- [ ] **Step 3: Implement**, and update the README's "Gleanvolt:Token" section: optional; "start the plugin, then accept it in Gleanvolt".
- [ ] **Step 4: Run the whole provider test project, expect PASS.**
- [ ] **Step 5: Commit**: `git commit -m "Chargers.Hosting: a plugin runs before Gleanvolt and pairs"`

---

### Task 8: The testing package pairs too

**Files:**
- Modify: `src/Gleanvolt.Providers.Testing/ChargerTestHub.cs`, `ChargerTestHubOptions.cs`, `PluginHarness.cs`, `README.md`
- Test: `tests/Gleanvolt.Providers.Tests/ChargerTestHubTests.cs` (add)

**Interfaces:**
- Consumes: Tasks 1, 4, 5.
- Produces: `ChargerTestHubOptions.AcceptPairing` (`bool`, default `true`): the test hub answers the two pairing
  endpoints and accepts every knock with its own token. `PluginHarness.ConnectAsync(..., bool paired = false)`:
  with `paired: true` the plugin starts with no token and gets it by pairing.

- [ ] **Step 1: Write the failing test**: `APluginWithoutATokenPairsWithTheTestHubAndIsRead()`: harness with
  `paired: true` → the hub receives a reading.
- [ ] **Step 2: Run, expect FAIL**: `dotnet test tests/Gleanvolt.Providers.Tests --filter FullyQualifiedName~ChargerTestHubTests`
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit**: `git commit -m "Providers.Testing: pairing in the test hub and the harness"`

---

### Task 9: Accept and Reject in Gleanvolt's UI

**Files:**
- Create: `src/Gleanvolt.Web/Components/PairingRequestsList.razor`
- Modify: `src/Gleanvolt.Web/Components/Pages/ExternalProvidersSettings.razor` (the list above the providers)
- Modify: `src/Gleanvolt.Web/Components/Pages/Wizard.razor:90-113` (the list first; the token block inside a `<details>` "Connect a plugin by hand")
- Modify: the `SettingsText`/`SetupText` `.resx` pairs (English and Czech)
- Test: `tests/Gleanvolt.Web.Tests/PairingRequestsListTests.cs`, `tests/Gleanvolt.Web.Tests/WizardTests.cs` (add)

**Interfaces:**
- Consumes: Task 3 (`IExternalProviders.Pairing`, `AcceptPairingAsync`, `RejectPairing`, `PairingBlock`).
- Produces: `<PairingRequestsList />` with no parameters; it re-renders on `IExternalProviders.Changed` and renders
  nothing when the list is empty. Each row: provider name and id, "This machine" or the source address, the code,
  Accept, Reject. A row not on this machine shows: "Open the plugin's page at its own address, or its log, and check
  that the code matches." A blocked row has Accept disabled and says why: `Duplicate` "Two plugins ask with this
  name. Reject the one you don't know."; `KindOff` "This kind of provider is turned off."; `AnotherProvider` "Gleanvolt
  already uses another charger plugin. Forget it first." (Czech alongside, in the `.cs.resx`).

- [ ] **Step 1: Write the failing tests** (bUnit with the existing `IExternalProviders` fake):

```csharp
AWaitingPluginIsListedWithItsCode()
APluginOnThisMachineSaysSoAndHasNoHint()
APluginElsewhereShowsTheCodeHint()
AcceptCallsTheRegistry()                            // fake records AcceptPairingAsync(id)
RejectCallsTheRegistry()
ABlockedRequestCannotBeAcceptedAndSaysWhy()         // one case per PairingBlock value
NothingIsShownWhenNobodyWaits()
TheWizardListsWaitingPlugins()                      // WizardTests: Finish step contains the list
TheWizardKeepsTheTokenForConnectingByHand()         // token still rendered, inside <details>
```

- [ ] **Step 2: Run, expect FAIL**: `dotnet test tests/Gleanvolt.Web.Tests --filter "FullyQualifiedName~PairingRequestsListTests|FullyQualifiedName~WizardTests"`
- [ ] **Step 3: Implement.** `ResourceTests` checks that every key has a Czech value; keep it passing.
- [ ] **Step 4: Run the whole web test project, expect PASS**: `dotnet test tests/Gleanvolt.Web.Tests`
- [ ] **Step 5: Commit**: `git commit -m "Settings and Wizard: accept a plugin that asks to be paired"`

---

### Task 10: Documentation in Gleanvolt

**Files:**
- Modify: `docs/PROVIDERS.md` (a "Pairing" section: the two endpoints, the records, the code, the rules from Global Constraints)
- Modify: `src/Gleanvolt.Providers.Client/README.md` (`Token` optional, `ProviderCredentials`, `ProviderPairing`)
- Modify: `docs/manual/src/content/docs/en/setting-up/charger.mdx` and `setup-wizard.mdx`, and their `cs` twins (start the plugin, accept in Gleanvolt)

- [ ] **Step 1: Write the docs.**
- [ ] **Step 2: Build everything, expect no warnings**: `dotnet build -c Release` (from the repo root)
- [ ] **Step 3: Run every test**: `dotnet test`, expect PASS.
- [ ] **Step 4: Commit**: `git commit -m "Docs: provider pairing"`

---

### Task 11: gleanvolt-chargers picks it up

Repository: `/home/martin/git/gleanvolt-chargers`, a branch `provider-pairing`. Needs Tasks 1–10 packed.

**Files:**
- Modify: `Directory.Build.props` (`GleanvoltSdkVersion` → the new preview, and its comment)
- Modify: `solax/README.md`, `huawei/README.md`, `goe/README.md`, `shelly/README.md`, `README.md`

- [ ] **Step 1: Pack the SDK into the feed** (from the gleanvolt repo, the loop in this repo's README, with the new
  version, e.g. `-p:Version=1.3.3-preview.2`).
- [ ] **Step 2: Bump `GleanvoltSdkVersion`** and run `dotnet test Gleanvolt.Chargers.slnx`, expect PASS with no plugin code change.
- [ ] **Step 3: READMEs**: drop `-e Gleanvolt__Token=<the provider token>` from the `docker run` examples and say
  "Gleanvolt__Token is optional: without it the plugin asks to be paired; accept it on Gleanvolt's Settings →
  External providers"; same for the zip instructions. The root README: "Gleanvolt 1.3.3 or later for pairing".
- [ ] **Step 4: Commit**: `git commit -m "Build on the SDK with pairing; the token is optional"`
