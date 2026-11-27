# Settings Reorganization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split Settings into five tabs (Installation, External providers, Forecast & prices, Integrations, System), merge every read-only description into its form, and fold the Installation form's expert fields under "Advanced".

**Architecture:** Each tab is its own routable page component behind `SettingsTabs` links, as today. Forms move between pages without changing what they save. The read-only MQTT and API blocks in `PvSystem.razor` move into `MqttEditorForm` / `ApiEditorForm` and a new `VehicleTelemetrySection`; one shared `RestartPendingBanner` replaces the three copies.

**Tech Stack:** .NET, Blazor InteractiveServer, bUnit + xUnit (`tests/Gleanvolt.Web.Tests`), resx localisation (en + cs), Astro manual (`docs/manual`), screenshot harness (`tests/Gleanvolt.Web.Screenshots`).

**Spec:** `docs/superpowers/specs/2026-11-27-settings-reorganization-design.md`

## Global Constraints

- Tab order and addresses, exactly: Installation `/settings` (+`/pv-system`), External providers `/settings/providers`, Forecast & prices `/settings/forecast-prices`, Integrations `/settings/integrations`, System `/settings/system` (+`/settings/health`, `/health`).
- Czech tab names: Instalace (keep the existing cs value), Externí poskytovatelé (unchanged), Předpověď a ceny, Integrace, Systém.
- Every new or changed UI string in both `<Area>Text.resx` and `<Area>Text.cs.resx`; each page keeps an `Every_string_…_comes_from_a_resource` test (`PseudoLocale`).
- No change to what any setting saves, where, or how it validates.
- Run web tests with `DOTNET_USE_POLLING_FILE_WATCHER=true dotnet test tests/Gleanvolt.Web.Tests -c Release` (CI tests Release; polling avoids the inotify flake).
- Branch `settings-reorganization` (already holds the spec); one PR based on `main`; `docs/IMPLEMENTATION_LOG.md` entry before `gh pr create`.

## Review Focus

1. **Signed out (no login enforced):** after the merge, no API key, broker password or other secret renders anywhere on Integrations; the curl example shows `<key>`. → Task 4, Task 5 tests.
2. **Old bookmarks:** `/health`, `/settings/health`, `/pv-system` and `/settings#solcast-editor`-style links still land on the right content. Hash links can't redirect, so every in-app link must be updated. → Task 1, Task 2 tests.
3. **A pending or edited expert field is not hidden:** Advanced opens itself when one of its fields is pending/edited, or the last save returned problems. → Task 6 tests.
4. **The wizard is unchanged:** with `Sections` set, the form shows no Advanced fold. → Task 6 test.
5. **Setup mode:** the new addresses work while the controller is unconfigured. `SetupGate` allows `/settings*` already; a gate test pins `/settings/system` and `/settings/integrations`. → Task 1.

---

### Task 1: Five tabs, five pages; forms move as they are

**Files:**
- Modify: `src/Gleanvolt.Web/Components/SettingsTab.cs`, `src/Gleanvolt.Web/Components/SettingsTabs.razor`
- Modify: `src/Gleanvolt.Web/Components/Pages/PvSystem.razor` (keep: banner, optional section, `PvSystemEditorForm`, `ChargerProviderForm`, car link; remove everything else)
- Create: `src/Gleanvolt.Web/Components/Pages/ForecastPrices.razor` (`SolcastAccountForm`, `WeatherForm`, `TariffEditorForm`)
- Create: `src/Gleanvolt.Web/Components/Pages/Integrations.razor` (the MQTT form + read-only MQTT block + API form + read-only API block, moved verbatim from `PvSystem.razor`, with its `@code`)
- Modify: `src/Gleanvolt.Web/Components/Pages/Health.razor` → routes `/settings/system`, `/settings/health`, `/health`; add `<ChangePassword />` after the Service section; `SettingsTab.System`
- Modify: `src/Gleanvolt.Web/Resources/SharedText.resx` + `.cs.resx`: `SettingsForecastPrices` ("Forecast & prices" / "Předpověď a ceny"), `SettingsIntegrations` ("Integrations" / "Integrace"), `SettingsSystem` ("System" / "Systém"); delete `SettingsHealth`. `SettingsText`: page titles `ForecastPricesPageTitle`, `IntegrationsPageTitle`, `SystemPageTitle`
- Test: `tests/Gleanvolt.Web.Tests/SettingsNavigationTests.cs`, `PvSystemPageTests.cs` (move the MQTT/API tests to a new `IntegrationsPageTests.cs`), new `ForecastPricesPageTests.cs`, `HealthPageTests.cs`, `WebUiSetupGateTests.cs`

**Interfaces:**
- Produces: `enum SettingsTab { Installation, ExternalProviders, ForecastPrices, Integrations, System }`; pages `ForecastPrices` and `Integrations` in `Gleanvolt.Web.Components.Pages`.

- [ ] **Step 1: Rewrite the navigation tests**

```csharp
[Fact] public void Forecast_and_prices_is_a_settings_tab() =>
    Assert.Equal(["/settings/forecast-prices"], Routes<ForecastPrices>());
[Fact] public void Integrations_is_a_settings_tab() =>
    Assert.Equal(["/settings/integrations"], Routes<Integrations>());
[Fact] public void System_is_a_settings_tab_and_keeps_the_health_addresses() =>
    Assert.Equal(["/settings/system", "/settings/health", "/health"], Routes<Health>());
// Marks_the_open_tab: InlineData for all five (ForecastPrices → "/settings/forecast-prices", Integrations → "/settings/integrations", System → "/settings/system")
[Fact] public void The_tabs_follow_the_order_of_setup() // hrefs, in order:
    // ["/settings", "/settings/providers", "/settings/forecast-prices", "/settings/integrations", "/settings/system"]
```

Plus: `PvSystemPageTests.The_installation_tab_holds_only_the_installation`: the rendered page contains `#pv-editor` and `#charger-editor`; it does not contain `#solcast-editor`, `#weather-editor`, `#tariff-editor`, `#mqtt-editor`, `#api-editor`. `ForecastPricesPageTests.Holds_the_forecast_weather_and_tariff`: contains the three. `HealthPageTests.The_system_tab_offers_the_password_change`: contains the ChangePassword section. `WebUiSetupGateTests`: `/settings/system` and `/settings/integrations` are allowed in setup mode.

- [ ] **Step 2: Run; expect compile failures / FAIL** (`ForecastPrices`, `Integrations`, `SettingsTab.System` don't exist).

- [ ] **Step 3: Implement.** Move the markup and update the components listed. Move the existing MQTT/API tests from `PvSystemPageTests` to `IntegrationsPageTests` with the same names, rendering `Integrations`. Split the resource test: each page gets its own `Every_string_on_the_<tab>_page_comes_from_a_resource`, and the allowed untranslated words go with the content that uses them.

- [ ] **Step 4: Run the web tests; expect PASS.**

- [ ] **Step 5: Commit** — "Settings: five tabs in setup order; forms move to Forecast & prices, Integrations and System".

### Task 2: Every link points at its new tab; the restart is "on System"

**Files:**
- Modify: `src/Gleanvolt.Web/Components/OptionalSettings.cs` (`Where`): Solcast → `/settings/forecast-prices#solcast-editor`, Tariff → `/settings/forecast-prices#tariff-editor`, Weather → `/settings/forecast-prices#weather-editor`, Mqtt → `/settings/integrations#mqtt-editor`, Ev unchanged
- Modify: `ExternalProvidersForm.razor:209-213` (tariff, solar-forecast and weather go to `/settings/forecast-prices#…`; charger stays `/settings#charger-editor`), `Plan/TargetedTab.razor:215`, `Plan/LowTariffOption.razor:29`, `Pages/Forecast.razor:36`
- Modify: every `href="/settings/health"` → `/settings/system` (`PvSystemEditorForm`, `MqttEditorForm`, `ApiEditorForm`, `Wizard.razor:132`)
- Resources: rename the key `RestartOnHealth` → `RestartOnSystem` ("Restart on System" / "Restartovat na stránce Systém"); `SetupText.FinishRunning_0` → "System" / "Systém"
- Modify: `Pages/Health.razor`: remove `<OptionalSettingsSection />` (the checklist stays on Installation only)
- Modify: `OptionalChecklist.razor`: Solcast's item gets a "recommended" mark (new `SetupText.Recommended`: "recommended" / "doporučeno"), shown after the name
- Test: `OptionalSettingsTests.cs`, `HealthPageTests.cs`, `ExternalProvidersFormTests.cs`, `WizardTests.cs`, `ManualLinksTests.cs` if it asserts hrefs

- [ ] **Step 1: Tests.** `OptionalSettingsTests`: a theory over all five `OptionalSetting` values asserting the exact `Where` strings above; `The_solar_forecast_is_marked_recommended` (the Solcast `li` contains the resource text `SetupText.Recommended`, the others don't); `Settings_lists_every_missing_optional_setting` (rename of `Health_and_settings_…`; asserts that Health does **not** render `#optional-settings`). `No_link_still_points_at_the_old_health_tab`: render PvSystem, Integrations and Wizard (finish step, running), and assert no `a[href='/settings/health']`.
- [ ] **Step 2: Run; expect FAIL.**
- [ ] **Step 3: Implement** the edits listed.
- [ ] **Step 4: Run; expect PASS.** Also `grep -rn '"/settings#\(solcast\|tariff\|weather\|mqtt\)\|/settings/health"' src` → only the `@page` route in `Health.razor`.
- [ ] **Step 5: Commit** — "Settings: links follow the new tabs; restart lives on System".

### Task 3: One restart banner, shared

**Files:**
- Create: `src/Gleanvolt.Web/Components/RestartPendingBanner.razor`
- Modify: `PvSystemEditorForm.razor` (replace its inline banner, keep `id="pv-pending"`), `MqttEditorForm.razor`, `ApiEditorForm.razor` (remove their banners; add `[Parameter] public EventCallback OnSaved` invoked after every successful save, create, remove, toggle or revert), `Pages/Integrations.razor`
- Test: new `RestartPendingBannerTests.cs`; `IntegrationsFormTests.cs`; `IntegrationsPageTests.cs`

**Interfaces:**
- Produces: `RestartPendingBanner` with `[Parameter, EditorRequired] int Count`, `[Parameter, EditorRequired] string Id`. Renders nothing when `Count <= 0`; otherwise the existing `restart-banner` markup with the `ChangesSaved_*` plural and a link to `/settings/system` with the text `RestartOnSystem`.
- Integrations page: `Pending = Editor.Read()` pending count of the MQTT keys + `MqttPassword` + `ApiEnabled` + pending API keys (the same sums the two forms compute today), re-read in the forms' `OnSaved`; banner `Id="integrations-pending"` at the top of the page.

- [ ] **Step 1: Tests.** Banner: `Count=0` → empty markup; `Count=2` → `#x` contains the "2" plural text and `a[href='/settings/system']`. Integrations page: with a fake editor holding one pending MQTT field and one pending API key → one `#integrations-pending` saying 2; no `#mqtt-pending` / `#api-pending` exist. After the API form creates a key (fake editor marks it pending), the banner count goes up without a reload.
- [ ] **Step 2: Run; expect FAIL.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run; expect PASS.**
- [ ] **Step 5: Commit** — "Settings: one restart banner per tab".

### Task 4: Home Assistant and car telemetry, each one section

**Files:**
- Modify: `MqttEditorForm.razor`: inject `MqttDisplayOptions`; under the form, when `Mqtt.HomeAssistant.Connection.Enabled`, a `<details id="ha-topics">` with summary `SettingsText.TopicsInUse` ("Topics in use" / "Používaná témata") holding the discovery prefix, device id, topic prefix, status interval facts, the topic table and the existing HA (i) tip. The retiring notice (`#ha-mqtt-retiring`) stays outside the `<details>`, always visible. Do not render `MqttConnection` (the form already shows the broker).
- Create: `VehicleTelemetrySection.razor` (`id="vehicle-telemetry"`): heading `VehicleTelemetry` plus the vehicle facts, `#vehicle-mqtt-no-topic` and `#mqtt-one-broker` moved from the page. Keep `MqttConnection` here: the vehicle broker is not edited on this tab. When off, a single line `SettingsText.VehicleTelemetryOff` ("Not subscribed: no car telemetry topic is set." + a link to `/car`), replacing the `.env` sentence.
- Modify: `Pages/Integrations.razor`: remove the moved read-only MQTT block and the `ha-mqtt-off` paragraph
- Resources: delete `HaOff` and `VehicleMqttOff` (both languages); add `TopicsInUse` and `VehicleTelemetryOff`
- Test: `IntegrationsPageTests.cs` (the moved MQTT tests now assert on the new places)

- [ ] **Step 1: Tests.** `The_broker_is_shown_once`: with HA enabled, `MqttConnection`'s client-id fact appears zero times inside `#mqtt-editor`. `Topics_are_folded_until_asked_for`: `#ha-topics` is a `details` without `open`. `The_retiring_notice_stays_visible`: `#ha-mqtt-retiring` is not inside `details`. `No_tab_tells_the_owner_to_edit_env_for_what_the_form_does`: the markup contains none of `HomeAssistant__Enabled`, `Vehicle__Enabled`, `Api__Enabled`. Keep (as ported in Task 1): `No_broker_password_reaches_an_unauthenticated_page`, `Says_when_the_two_links_share_one_broker_…`, `Shows_the_topic_the_vehicle_feed_actually_subscribed_to`.
- [ ] **Step 2: Run; expect FAIL.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run; expect PASS.**
- [ ] **Step 5: Commit** — "Integrations: Home Assistant and car telemetry each say everything once".

### Task 5: REST API, one section

**Files:**
- Modify: `ApiEditorForm.razor`: inject `ApiDisplayOptions` and `NavigationManager`. When `Api.Enabled` (running), above Keys: a `dl.facts` with the base URL (and the implicit-port note) and the OpenAPI link, moved from the page. Below the create-key controls: `<details id="api-example">` with summary `CallingIt`, holding the curl line. Use the first key the form lists: with `Secret` when not `ReadOnly`, otherwise `<key>`. `CurlPrefix`/`CurlSuffix`/`_api` move here.
- Modify: `Pages/Integrations.razor`: delete the read-only API block and its `@code`
- Resources: delete `ApiOff`, `KeysWithheld`, `ApiTip` if unused afterwards (both languages)
- Test: `IntegrationsPageTests.cs`, `IntegrationsFormTests.cs`

- [ ] **Step 1: Tests.** `One_keys_table`: exactly one `table` under `#api-editor` whose header contains `SettingsText.Key`. `Shows_where_the_api_is_and_links_to_what_answers_without_a_key` (ported): asserts inside `#api-editor`. `The_example_call_carries_this_installations_own_address` (ported): `#api-example` contains `http://localhost/api/v1/status`, is a closed `details`, and the raw key is not in its text. `No_api_key_reaches_an_unauthenticated_page` (ported): signed out, no key value anywhere and the example shows `<key>`. `With_the_api_off_shows_no_address`: no `#api-example`, no base URL.
- [ ] **Step 2: Run; expect FAIL.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run; expect PASS.**
- [ ] **Step 5: Commit** — "Integrations: the API is one section with one keys table".

### Task 6: Installation form — essentials first, "Advanced" folded

**Files:**
- Modify: `PvSystemEditorForm.razor`
- Resources: `SettingsText.GroupAdvanced` ("Advanced" / "Pokročilé")
- Test: `PvSystemEditorFormTests.cs`

**Decisions:**
- Advanced keys: `Id`, `InstallDate`, `LossFactor`, `HomeBatteryChargeEfficiency`, `InverterUnmeteredGridPhase`, plus `DeviceSnapshotPanel` ("What is happening now" + Save register snapshot).
- Only when `Sections is null` (Settings). With `Sections` set (the wizard), every field renders in its group as today and there is no fold.
- The groups render as today minus the advanced keys. Then comes `<details id="pv-advanced">` with summary `GroupAdvanced`, holding those keys in the same field markup (extract the per-field markup into a `RenderFragment<string> Field` so it isn't duplicated), then the snapshot panel when `OfferSnapshot && !ReadOnly`.
- `open` when any advanced key `IsPending || IsEdited`, or `_problems.Count > 0` after a save.

- [ ] **Step 1: Tests.** `Advanced_fields_are_folded_on_settings`: `#pv-advanced` is a `details` without `open` containing `#pv-Pv-Id` (use the real id scheme `"pv-" + key.Replace(':','-')`) and `#pv-Pv-LossFactor`. The essential fields (`Name`, `InverterHost`) are not inside it. `A_pending_advanced_field_opens_the_fold`: a fake with `LossFactor` pending → `open`. `A_refused_save_opens_the_fold`. `The_wizard_has_no_fold`: `Sections=[Identity, Capacity]` → no `#pv-advanced`, and `#pv-Pv-Id` is present.
- [ ] **Step 2: Run; expect FAIL.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run; expect PASS.**
- [ ] **Step 5: Commit** — "Installation: expert fields under Advanced".

### Task 7: Screenshots, manual, reference docs, log, PR

**Files:**
- Modify: `tests/Gleanvolt.Web.Screenshots/SettingsScreenshots.cs`: Solcast, weather and tariff captures render `ForecastPrices` at `/settings/forecast-prices`; api renders `Integrations`; add `settings-integrations` (`#mqtt-editor`); `settings-health` → `settings-system` (render `Health` at `/settings/system`); `settings-installation` recaptured with the fold closed
- Modify: manual `docs/manual/src/content/docs/{en,cs}/setting-up/settings.mdx`: five tabs in order, a section per tab, Advanced described, Health → System. Every "Settings → Health" or "Settings → …" mention in the files listed by `grep -rln 'Health\|Settings' docs/manual/src/content/docs` (setting-up/*, troubleshooting, installation/*, using/modes/targeted, privacy, how-it-works/unmetered-phase), both languages
- Modify: `docs/WEB_UI.md`, `docs/TROUBLESHOOTING.md`, `docs/CONFIGURATION.md`, `docs/HOME_ASSISTANT.md` wherever they name a Settings place
- Modify: `docs/IMPLEMENTATION_LOG.md` (new top entry)

- [ ] **Step 1:** `cd docs/manual && npm run screenshots`; open each regenerated `settings-*.png` and check it shows the intended section.
- [ ] **Step 2:** Edit the manual and docs. `npm run build` in `docs/manual` must pass with no broken-link errors.
- [ ] **Step 3:** Full check: `DOTNET_USE_POLLING_FILE_WATCHER=true dotnet test -c Release`. Expect all green.
- [ ] **Step 4:** Write the IMPLEMENTATION_LOG entry (tabs and why this order, merged sections, Advanced rule, links that changed, old routes kept). Commit — "Docs: Settings in five tabs".
- [ ] **Step 5:** Check that `main` hasn't moved under the branch (rebase if it has), push, and `gh pr create` with a body drawn from the log entry and ending with the Claude Code attribution line.
