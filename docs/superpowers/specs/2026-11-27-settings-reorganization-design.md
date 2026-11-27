# Settings reorganization: one tab per kind of thing, nothing said twice

Date: 2026-11-27. Repository: gleanvolt (web UI, user manual).

## Intent

The Installation tab of Settings (`/settings`, `Components/Pages/PvSystem.razor`) has grown into about ten
large sections on one scrolling page: the installation form, charger, solar forecast, weather, tariff, change
password, the MQTT form and its read-only description, the API form and its read-only description. Several
things are said twice, and two kinds of save (restart to apply, applies on save) sit side by side.

Settings is used for **one-off setup and rarely revisited**. So it is organised by *what a thing is*, not
by how often it is used: an owner coming back months later should find the one thing they want by its tab,
and each tab should be one or two screens long.

## What is said twice today

1. **API keys.** `ApiEditorForm` has a Keys table (Reveal, Copy, Remove, Create); the read-only API section
   below it has a second Keys table, plus base URL, OpenAPI link and an example `curl`.
2. **MQTT broker.** `MqttEditorForm` edits the broker; the read-only MQTT section repeats the connection
   (`MqttConnection`) and adds the discovery prefix, device id, topic prefix, status interval and topic table.
3. **Stale `.env` instructions.** When MQTT, vehicle telemetry or the API is off, the read-only sections say
   to set `HomeAssistant__Enabled=true`, `Api__Keys__<name>=…` and so on in `.env`, although the form directly
   above now does it.
4. **Restart banners.** Installation, MQTT and API forms each show their own "N changes saved… restart on
   Health" banner.
5. **Optional-settings checklist.** `OptionalSettingsSection` is on both Installation and Health.

## Goals

- Settings split into five tabs, each one kind of thing, each one or two screens.
- Every fact appears once: each read-only description merges into its form.
- Each tab saves one way where possible, so the owner knows whether a restart is needed.
- The restart lives on a tab whose name says so ("System", not "Health").
- A newcomer coming back to the Installation form sees the essential fields first; expert fields are folded.

## Non-goals

- No change to what any setting does, where it is stored, or how it is validated.
- No change to the setup wizard's steps (Inverter, Site, Finish). Its links follow the new addresses.
- Running without a charger is a separate design (part 1 of this work), not this one.
- No new settings.

## Anti-goals

- An old bookmark or manual link that stops working (`/settings`, `/pv-system`, `/settings/health`, `/health`,
  `/settings/providers`, and the `#…-editor` anchors the optional checklist uses).
- A useful fact dropped while removing duplication (API base URL, OpenAPI link, `curl` example, MQTT topic
  list, retiring-topics notice, "both links share one broker" note).
- English-only text: every new or moved string is in both `<Area>Text.resx` and `.cs.resx`.

## Approaches considered

- **A. More tabs, one per kind of thing (chosen).** Follows #463, which gave External providers its own tab for
  the same reason. Each tab is a real URL the manual can link to. Costs two more tabs and a manual update.
- **B. One page of collapsed sections with a summary line each.** Shortest page, but every summary line is new
  text in two languages that must stay true, and it does not separate the two kinds of save.
- **C. Only remove duplication and reorder.** Cheapest, but Installation and Tariff alone keep the page long.

## The design

### Tabs

```
Settings:  Installation | Data sources | Integrations | External providers | System
```

| Tab | Address | Contents | Saves |
|---|---|---|---|
| Installation | `/settings` (also `/pv-system`) | Optional-settings checklist; installation form (PV system, battery, inverter); charger; link to the Car page | restart |
| Data sources | `/settings/data-sources` | Solar forecast (Solcast), weather, tariff | on save |
| Integrations | `/settings/integrations` | Home Assistant (MQTT), car telemetry (MQTT), REST API | restart |
| External providers | `/settings/providers` | unchanged (pairing requests, token, each kind's session) | on save |
| System | `/settings/system` (also `/settings/health`, `/health`) | today's Health facts; Service (stop, restart); Change password | — |

The charger stays on Installation: it is part of the installation, even though its panel is an external
provider's.

### Integrations: each one section

```
┌ Home Assistant (MQTT) ─────────────────────────────┐
│ [x] Publish to Home Assistant                      │
│ Broker, port, user, password   [Test connection]   │
│ ▸ Topics in use                     (collapsed)    │
├ Car telemetry (MQTT) ──────────────────────────────┤
│ Subscribed topic, stale after, retry every         │
├ REST API ──────────────────────────────────────────┤
│ [x] Turn the API on                                │
│ Address · OpenAPI document                         │
│ Keys: name · •••• Reveal Copy · Remove             │
│ New key for [________] [Create key]                │
│ ▸ Example call (curl)               (collapsed)    │
└────────────────────────────────────────────────────┘
```

- **Home Assistant:** the form plus, collapsed, the discovery prefix, device id, topic prefix, status
  interval and the topic table. The retiring-topics notice stays visible when it applies: it reports a state.
- **Car telemetry:** the running subscription facts (topic, stale after, retry every) and the "no topic" /
  "one broker" notes. The broker is the same kind of setting as Home Assistant's, so it stays here, not on
  the Car page.
- **REST API:** the form's keys table is the only one. Base URL (with the implicit-port note) and the OpenAPI
  link sit above it; the `curl` example is collapsed below it.
- **Dropped:** the second keys table, the repeated broker connection, and every "set `…` in `.env`"
  sentence for something the form does. When a link is off, the form's own checkbox says so.
- **One restart banner** at the top of the tab, counting the pending changes of both forms, linking to System.

### Installation form: essentials first, the rest under "Advanced"

Visible: Name, Address; Latitude, Longitude, Time zone, Azimuth, Tilt; Peak DC capacity, Inverter capacity;
Usable battery capacity; Inverter brand, model, host, port, unit id.

Under a collapsed **Advanced** group: Id, Commissioned, Loss factor, Charge efficiency, Unmetered grid phase,
"What is happening now" and Save register snapshot.

The group opens on its own when one of its fields has a validation problem or a pending change, so nothing
that needs attention is hidden. The wizard, which shows chosen sections only, is unaffected.

### Optional-settings checklist

- Shown on Installation only (the first tab), not on System.
- **Solar forecast first, marked recommended**: it is what makes Forecasted and Targeted useful. The rest
  keep their order.
- Links point at the new addresses (`/settings/data-sources#solcast-editor`, `/settings/integrations#mqtt-editor`,
  …). `NotConfiguredCard` uses the same links, so Forecast and Charging plan follow.

### Restart wording

Every banner and the wizard's Finish say "restart on **System**" and link to `/settings/system`.

## Constraints

- Interactive Blazor (InteractiveServer), as today; tabs are links (`SettingsTabs`), one page component per tab.
- Every new UI string in both `SettingsText.resx`/`SharedText.resx`/`SetupText.resx` and their `.cs.resx`. Czech
  tab names: Instalace, Zdroje dat, Integrace, Externí poskytovatelé (unchanged), Systém.
- User manual (`docs/manual`, en and cs): `setting-up/settings.mdx` and every page that says
  "Settings → …" follow the new tabs; screenshots regenerate through `tests/Gleanvolt.Web.Screenshots`
  (`SettingsScreenshots.cs` renders `PvSystem` for every section today and must render the new pages).
- `docs/IMPLEMENTATION_LOG.md` entry before the PR.

## Left to the builder

- How the Integrations tab totals pending changes for its one banner (shared state, or each form reporting up).
- Component names for the two new pages, and whether `PvSystem.razor` keeps its name.
- Whether `/settings/health` and `/health` redirect or keep a second `@page` route (either keeps bookmarks working).
- Whether the `Health` enum value is renamed to `System`.
