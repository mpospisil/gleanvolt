# Provider pairing: a plugin that runs before Gleanvolt, and gets its token without the owner copying it

Date: 2026-11-26. Repositories: gleanvolt (contracts, server, client SDK, hosting SDK, web UI) and
gleanvolt-chargers (SDK bump, READMEs).

## Intent

Today a charger plugin will not start without `Gleanvolt:Token`
(`Gleanvolt.Chargers.Hosting/PluginHost.cs`), and the owner gets that token by installing Gleanvolt, opening
Settings → External providers, revealing it and copying it into the plugin's environment or config. So the order
of installation is forced (Gleanvolt first), and the owner handles a secret by hand.

We want:

1. A charger plugin that **starts and keeps running with no token**, before Gleanvolt is installed.
2. The plugin **gets the token while running**, with no restart and no config edit, once the owner says so in
   Gleanvolt.

Order must not matter: plugin first, or Gleanvolt first.

## Deployments it must cover

- Gleanvolt and plugins on one device (about 99% of owners): directly on a PC or Raspberry Pi (zip, service),
  in separate containers with host networking (`deploy/docker-compose*.yml`, the plugin READMEs), or a mix.
- Plugins on another device than Gleanvolt.
- Plugin containers on a bridge network (the plugin reaches Gleanvolt only by a configured address). Pairing
  must work here too. That a bridge network breaks the charger *search* is a separate, known limit and not
  solved here.

## Goals

- A plugin with no token runs, finds Gleanvolt when it appears, and asks to be paired.
- The owner accepts in Gleanvolt (Settings → External providers, and the setup Wizard). Gleanvolt hands the token
  over; the plugin keeps it in its data directory and connects as today.
- One path for every deployment above: the same knock, collect and token handling. Same-machine changes only how
  much the owner has to check before Accept (see "Telling a real plugin from a copy").
- `Gleanvolt:Token` in config keeps working and wins over pairing (scripted installs, the 1% on another device
  who prefer it).

## Non-goals

- A per-plugin token. The shared token (#406) stays; pairing delivers it.
- Configuring the charger (address, search) before pairing. Before pairing the plugin page shows only the
  pairing state.
- Fixing device search on bridge networks.

## Anti-goals (would be a failure even if it works)

- Anything on the plugin page that changes behaviour (address, token, settings) reachable on the LAN without
  Gleanvolt's ticket.
- A stranger's program on the LAN getting the token without the owner accepting it, or the owner being unable to
  tell a real plugin's request from a copy.
- A plugin that crashes, exits or spams logs while Gleanvolt is absent.

## Approaches considered

- **A. The plugin knocks, the owner approves in Gleanvolt (chosen).** The plugin finds Gleanvolt and sends an
  unauthenticated pairing request carrying a pairing code; Gleanvolt lists it; the owner checks the code and
  accepts; the plugin collects the token. Works across machines and bridge networks, never opens the plugin page,
  and the owner never sees a token. Costs a new pairing endpoint and a waiting list in the UI.
- **B. Gleanvolt finds the plugin (mDNS) and pushes the token.** Needs an open endpoint on the plugin that
  accepts a token, which anyone could hit first, so a code is needed anyway; mDNS does not leave a bridge network.
  More parts, less coverage.
- **C. The owner types the token into the plugin page, guarded by a setup code from the plugin's log.** The
  owner digs a code out of `docker logs` or a service log and still copies the token. Worst first-run.

The owner picked A.

## The design

### Flow

1. **Plugin starts with no token.** No `Gleanvolt:Token` and no paired token in its data directory. It does not
   throw (today's `InvalidOperationException` goes). It generates a **pairing secret** (random, ≥128 bits) and
   keeps it in its data directory, so it is stable across restarts. The **pairing code** shown to people is six
   digits derived from it (e.g. a hash of the secret, mod 10^6, shown as `482 193`).
2. **Plugin looks for Gleanvolt**, in order: `Gleanvolt:Endpoint` if configured; `http://localhost:8090`; the
   `_gleanvolt._tcp` service browsed over mDNS (host and port from the record; `WebAdvertiser` already announces
   it). Retries with backoff to about a minute, at log level Information once and Debug after, so a plugin that
   waits for days is quiet.
3. **Plugin knocks**: an unauthenticated request to Gleanvolt with its kind, provider id, name, version, page URL,
   and a hash of the pairing secret. Gleanvolt keeps it in a **waiting list** (memory only) with the source
   address, whether it came from this machine (loopback), and the code it derives the same way.
4. **Plugin page while waiting**: answers without a ticket, but only a read-only view: "Waiting for Gleanvolt" or
   "Waiting for the owner to accept in Gleanvolt", the pairing code, and this plugin's address. Every other route
   (the settings, the recording, the Blazor interactive page) answers 403 as today. The code and status are also
   logged.
5. **Owner accepts** in Gleanvolt: Settings → External providers shows "SolaX charger (solax-charger) at
   192.168.1.20 [this machine], code 482 193, [Accept] [Reject]". The Wizard's Finish step (where the token is
   shown today; the Wizard has no provider step of its own) shows the same list.
   If no shared token exists yet, accepting creates it (`ExternalProviderRegistry.CreateTokenAsync`), so a
   first-run owner never sees the token step at all when every plugin pairs.
6. **Plugin collects the token** by polling its request with the pairing secret itself (only the holder of the
   secret whose hash was sent gets the token). It saves the token in its data directory through the SDK's
   `IProviderSecretStore`, forgets the pairing secret, and the existing connection loop starts with it. From here
   the page guard, ticket and cookie work exactly as today.

### Transport (my call; the builder may change it)

Plain HTTP on Gleanvolt, outside the provider token's authentication:

- `POST /providers/pairing` with the knock; answers a request id. Same provider id + same secret hash replaces
  the earlier entry instead of adding one.
- `POST /providers/pairing/{id}/collect` with the pairing secret in the body; answers `waiting`, `accepted` +
  token + endpoint to use, `rejected`, or `unknown` (Gleanvolt restarted: knock again). Polled every few
  seconds while waiting.

Not SignalR: no long-lived unauthenticated connections, and it works before the plugin knows the hub.

Protection of the unauthenticated endpoints: a cap on the waiting list (e.g. 16 entries, oldest dropped),
entries expire (e.g. 15 minutes without a poll), a per-source rate limit, and size limits on every field.

### Telling a real plugin from a copy

**The owner chose: the code check only for plugins on another machine.** A knock from loopback is marked "this
machine" and one click accepts it; nothing on the LAN can send a loopback knock, and a rogue program already on
the machine is past what pairing could stop. With host networking (every documented deployment), plugins on the
same device all knock from loopback, so the 99% case is one click. A knock from any other address shows its code
and the hint "open the plugin's page at its own address, or its log, and check the code matches" before Accept.

For knocks from another address:

The plugin page shows its code while waiting, so a program on the LAN can read it and knock with the same
provider id and code (but not the same secret, which it never sees). Gleanvolt:

- shows the source address and "this machine" for each entry, and the plugin page shows its own address, so the
  owner compares code **and** address;
- when two entries share a provider id, shows both with a warning and accepts neither in one click.

Without Gleanvolt's (optional) login, anyone who can open Gleanvolt can accept a waiting plugin, on Settings and in the
Wizard. The owner chose this so that an install without a password can pair, although Settings will not show the token
itself without a login. Setting a password closes it.

Gleanvolt never links to or frames the page URL sent in a knock before it is accepted: a copy could send its own
page, showing its own code, and the check would confirm itself. The owner reaches a waiting plugin's page by its
own address, or reads its log.

Which address each side shows (my call): Gleanvolt shows "this machine" for a loopback knock and the source address
otherwise; the plugin's waiting view lists its own LAN addresses. On a bridge network these won't match (the Docker
gateway against a 172.x address); the code is what counts there.

### Duplicates and knocks Gleanvolt would refuse (my calls)

- Same provider id **and** same source address: the newer knock replaces the older (a container recreated without
  its `/data` volume gets a new secret on each start).
- Same provider id from different addresses: both listed with a warning; Accept is disabled on both until the
  owner rejects one.
- A knock for a kind that is turned off, or a charger knock when Gleanvolt remembers another charger plugin
  (#440): listed with the reason and Accept disabled, since the shared token would be handed over for a session
  that is then refused.

### Which Gleanvolt the plugin talks to after pairing (my calls)

- The collect answer carries no endpoint. The plugin saves the endpoint it knocked on and was accepted at, next
  to the token, and uses only that one from then on: no mDNS browse on restart, so the token is never sent to
  whatever else answers as `_gleanvolt._tcp`.
- The search order (config, localhost, mDNS) is used only for pairing. A plugin with `Gleanvolt:Token` in config
  and no endpoint keeps today's default, `http://localhost:8090`.
- The page's frame-ancestors policy (`PageFraming`) comes from the endpoint in use, read per request, not fixed at
  startup, because pairing can set the endpoint while the plugin runs.
- A paired token being refused clears the saved endpoint too, so re-pairing searches again.

### Reject, new token, forget

- **Reject**: the entry is dropped; the plugin's poll gets `rejected`, its page says so, and it knocks again only
  after a long delay (e.g. an hour) or a restart.
- **New token** in Gleanvolt (`CreateTokenAsync`, which today closes every session): a plugin whose token came
  from pairing, refused with "not the provider token", deletes its saved token and goes back to waiting. Every
  paired plugin re-pairs; the owner accepts each. A plugin with `Gleanvolt:Token` from config does not; it logs
  and retries as today.
- **Forget** (charger: the remembered plugin, #440) is unchanged; it does not unpair.

### Where the token comes from, in order

1. `Gleanvolt:Token` in config (environment, command line, user secrets).
2. The paired token in the data directory.
3. Neither: pair.

The page shows which (as `SavedSetting.Source` does for the address).

### What changes where

**gleanvolt**

- `Gleanvolt.Providers.Contracts`: the pairing request and answer records, the code derivation (shared so both
  sides compute the same code), and their paths.
- `Gleanvolt.Providers.Server`: the waiting list, the two endpoints, accept/reject, create-token-on-accept.
- `Gleanvolt.Providers.Client`: `ProviderOptions.Token` optional; the connection loop waits for a token; the
  discovery order; the pairing loop; token kept and dropped in the secret store; the page middleware's
  "no token yet" mode (waiting view only, everything else 403).
- `Gleanvolt.Chargers.Hosting`: no throw on a missing token; the waiting view in place of `PluginFrame` until
  paired; the mDNS browse for `_gleanvolt._tcp` (the SDK must not depend on `Gleanvolt.Infrastructure`, so a
  small query of its own, or move the DNS message code into a package both can use).
- `Gleanvolt.Web`: the waiting list with Accept/Reject on External providers and in the Wizard; Czech and English
  texts.
- `docs/PROVIDERS.md`: pairing in the contract.

The pairing lives in the shared provider client and server, so tariff, weather, vehicle and solar-forecast
plugins get it too. The charger is the one this design is tested and documented for (my call).

**gleanvolt-chargers**

- Move `GleanvoltSdkVersion` to the release with pairing.
- READMEs: `Gleanvolt__Token` becomes optional; "start the plugin, then accept it in Gleanvolt".
- No plugin code changes expected.

## Left to the builder

- Exact paths, record names, poll interval, backoff, cap and expiry values.
- How the six-digit code is derived, as long as both sides compute it from the secret's hash the same way.
- Whether the mDNS browse is new code in the SDK or shared code moved out of `Gleanvolt.Infrastructure`.
- The waiting view's markup, inside the shared frame's look.
- Tests: `PluginHarness` / `ChargerTestHub` gain a pairing path; a plugin started with no token reaches
  "connected" after an accept.
