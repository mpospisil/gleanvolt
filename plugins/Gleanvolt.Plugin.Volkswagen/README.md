# Volkswagen: the volkswagen.de car feed as a plugin

A copy of Gleanvolt's built-in volkswagen.de feed (`Vehicle:Website`), run outside Gleanvolt as an
external car provider (#410). It reads one car from volkswagen.de, publishes what the car said to
Gleanvolt, and serves its own page for the account, the sign-in and the emailed code, which Gleanvolt
shows on its **Car** page.

It exists to find out whether car integrations can leave Gleanvolt and live as plugins, and what the
provider SDK lacks before anything is moved for real. **The built-in feed stays as it is.**

> **Not for the image or the release.** The plugin is built with the solution so a change that breaks
> it is seen, and it is never packed, never in Gleanvolt's container image and never in the release
> packages.

> **One feed at a time.** Run it only with **An external provider** chosen on Gleanvolt's Car page.
> Gleanvolt then doesn't start its own volkswagen.de feed, so the two never use the VW account at the
> same time. Afterwards, choose volkswagen.de again on the Car page.

## What it does

- **Reads the car** from volkswagen.de's `charging/status`, every 5 minutes while the car says it is
  charging and every 15 minutes otherwise, and publishes each reading to Gleanvolt with the car's own
  capture time.
- **Answers *Ask the car*** at once. A second ask within 30 seconds answers with the reading it has.
- **Stops for you on any failure**, as the built-in does: a code wanted, an answer it can't read, no
  answer at all. It then reads nothing, tells Gleanvolt it needs you (the band on every page, and
  `NeedsOwner` on the Home Assistant **Car feed** entity), and **never signs in again on a clock**: a
  replayed login is how an account gets locked. Signing in on its page puts it back on its clock
  without a restart.
- **Remembers the session** across restarts, so a restart doesn't ask for another code.

Its page shows the feed's status, the last reading and when the car captured it, the sign-in (with the
one-time code when Volkswagen asks for one) and **Forget the session**, and the account: VW ID,
password and VIN. The password is never shown back.

## Configuration

| Key | Default | |
| --- | --- | --- |
| `Gleanvolt:Endpoint` | `http://localhost:8090` | Gleanvolt's web address |
| `Gleanvolt:Token` | | The token from Gleanvolt's **Settings → External providers**. Required |
| `Urls` | `http://localhost:8095` | Where the page listens. In a container, `http://0.0.0.0:8095` |
| `Gleanvolt:PageUrl` | `Urls`, with `0.0.0.0` read as `localhost` | The page's address **as the owner's browser reaches it**, e.g. `http://192.168.2.7:8095/` |
| `Gleanvolt:FrameAncestors` | `Gleanvolt:Endpoint`'s origin | Who may show the page in a frame (CSP `frame-ancestors`), e.g. `http://gleanvolt.local:8090` when the browser reaches Gleanvolt by another name than the endpoint |
| `Volkswagen:DataDirectory` | `data` | Where the account and the session are kept |
| `Volkswagen:PollInterval` | `00:05:00` | How often to read while Gleanvolt has a charging session open, or the car says it is charging |
| `Volkswagen:IdlePollInterval` | `00:15:00` | How often to read otherwise |
| `Volkswagen:MinimumTimeBetweenReads` | `00:00:30` | A read asked for sooner answers with the last reading |
| `Volkswagen:MinimumTimeBetweenLogins` | `00:05:00` | The floor between credential sign-ins |

Any key can be an environment variable (`Gleanvolt__Token`), a command-line argument
(`--Gleanvolt:Token=…`) or a user secret.

The account is not configuration: you enter it on the page.

## The data directory holds passwords

`Volkswagen:DataDirectory` holds two files:

- `account.json`: the VW ID, **the password** and the VIN.
- `session.json`: the signed-in session's cookies. **Whoever has them is signed in as you.**

That account also unlocks and locates the car. The plugin has no secret store: both files are plain
JSON, written readable and writable by the plugin's user alone (`0600`) in a directory only that user
can open (`0700`). That is what Gleanvolt's own secret store does on Linux, but nothing is encrypted.
What protects them is those permissions, and who can read the disk or the Docker volume: anyone with
root on the machine, or the SD card in their hand, has them. Never put the directory in a backup or a
share you wouldn't give the password to, and never in the repository. Neither file is ever logged.

`keys/` beside them holds the page's own ASP.NET Core keys (its Blazor circuits), not your account.

## Run it while developing

Against a Gleanvolt on the Pi, from a development machine:

1. In Gleanvolt, on the **Car** page, choose **An external provider**, save and restart.
2. Store the token once, out of the shell history:

   ```sh
   dotnet user-secrets set Gleanvolt:Token gvp_... --project plugins/Gleanvolt.Plugin.Volkswagen
   ```

3. Run it, listening on the LAN so the browser reaches the page:

   ```sh
   dotnet run --project plugins/Gleanvolt.Plugin.Volkswagen -- \
     --Gleanvolt:Endpoint=http://192.168.2.7:8090 \
     --Urls=http://0.0.0.0:8095 \
     --Gleanvolt:PageUrl=http://<this machine's address>:8095/
   ```

4. Open Gleanvolt's **Car** page. Under **External provider**, the plugin's page is in the frame: enter
   the account, save, press **Sign in**, and enter the code Volkswagen emails.

On a different host than Gleanvolt, over `http`, the browser won't keep the page's cookie inside
Gleanvolt's frame, so a reload inside the frame may answer 403: open the Car page again. See
[the known limits](../../docs/PROVIDERS.md#the-providers-own-page).

## Run it on the Pi

Its own container, outside the compose stack and `deploy.sh`, which never touch it. Build the image
for the Pi on a development machine, from the repository root, and copy it over:

```sh
docker build --platform linux/arm64 -f plugins/Gleanvolt.Plugin.Volkswagen/Dockerfile -t gleanvolt-volkswagen .
docker save gleanvolt-volkswagen | ssh martin@192.168.2.7 docker load
```

Then on the Pi:

```sh
docker run -d --name gleanvolt-volkswagen --restart unless-stopped \
  -p 8095:8095 \
  -v gleanvolt-volkswagen:/data \
  -e Gleanvolt__Endpoint=http://192.168.2.7:8090 \
  -e Gleanvolt__Token=gvp_... \
  -e Gleanvolt__PageUrl=http://gleanvolt.local:8095/ \
  -e "Gleanvolt__FrameAncestors=http://gleanvolt.local:8090 http://192.168.2.7:8090" \
  gleanvolt-volkswagen
```

Use the **name you open Gleanvolt at** in the browser for both of the last two, not the address the
plugin connects to:

- `Gleanvolt:FrameAncestors` lists every address the browser opens Gleanvolt at. One left out shows
  "refused to connect" in the frame: the page allows only these to frame it.
- `Gleanvolt:PageUrl` uses the same host name as Gleanvolt. On another host (`192.168.2.7` while
  Gleanvolt is `gleanvolt.local`), the browser treats the page as a third party in the frame and drops
  its cookie over http, so a reload inside the frame answers 403.

`Gleanvolt:Endpoint` stays an address the container can resolve: a `.local` name usually isn't one.

The image runs as the non-root `app` user (UID 1654), and `/data` is that user's alone. A named volume
takes those permissions over; a bind-mounted host directory must be `chown 1654:1654` and `chmod 700`.
The token is in the container's environment, so `docker inspect` shows it to whoever can run Docker on
the Pi, as it does for Gleanvolt's own `.env`.

To update it, build and load again, then `docker rm -f gleanvolt-volkswagen` and run it again. The
volume keeps the account and the session, so no new code is asked for.

## What changed from the built-in, and why

The code is a copy of `src/Gleanvolt.Infrastructure/Vehicles/VwWebsite/`. This list is the main
result of #410: what moving a car feed into a plugin costs.

| What | Built-in | Plugin | Why |
| --- | --- | --- | --- |
| Dependencies | `Gleanvolt.Core`, `Gleanvolt.Infrastructure` | `Gleanvolt.Providers.Client` only | A plugin sees only the SDK |
| The reading | `VehicleState`, with a source id | The contract's `VehicleReading`; no source id | Gleanvolt names a provider's reading after the provider |
| Time to full over 48 hours | Kept, up to 7 days | Left out; the rest of the reading is sent | Gleanvolt refuses a whole reading over 48 hours; the built-in's holder never checked |
| The car's target SOC (`TargetSocPercent`) | Parsed, unused | Not copied | Nothing used it, and the contract has no field for it |
| Account | `Vehicle:Website:Username`, `:Password`, `:Vin`, on the Car page, in the secret store | Entered on the plugin's page, in `account.json` | A plugin has no Gleanvolt settings or secret store. A new VW ID or VIN forgets the session; a new password alone doesn't |
| Session cookies | `ISecretStore` (`0600` files on Linux, DPAPI on Windows) | `session.json`, `0600` | **Gap: the SDK has no secret store.** On Windows, nothing like DPAPI |
| Sign-in states and the page | `VehicleSignInState` with phrases, in English and Czech | A small record of its own; every text in English and Czech in `PageText` (`Words`), chosen by the `culture` Gleanvolt opens the page with (#415) | Gleanvolt's phrases and resources are its own. A culture cookie carries the language to Blazor's circuit, whose requests have no query |
| Status messages | `VehicleSourceHealth` with phrases | `ProviderStatus` with a code (`not-set-up`, `needs-sign-in`), which Gleanvolt words itself, and the detail in English and Czech (#415) | The English `message` remains for a Gleanvolt older than vehicle 1.3 |
| How often to read | 5 min while Gleanvolt's charge control has a session open (`ChargeControlStatusHolder`), 15 otherwise | 5 min while Gleanvolt has a session open (vehicle 1.2's `ChargingSessionChanged`, #414) **or** the car last said it is charging, 15 otherwise; a read at once when a session opens | The SDK carries the session since #414. The car's own state also counts, so a charge started from the car or at a public charger is followed, as the built-in Škoda feed does. With a Gleanvolt older than 1.2, only the car's state decides |
| Back on the clock after a sign-in | The worker watches the client's sign-in counter every minute | The page calls `Resume` and wakes the reader | The page and the reads share one process |
| What stops it | A code wanted, no reading, an HTTP failure or a timeout | **Any** exception too | Otherwise the clock would carry on, and an unexpected failure could become a sign-in on a clock |
| A changed account | Takes a restart | Stops reading until you sign in | No credentials are replayed that you didn't ask to use |
| Reads close together | Each *Ask the car* reads | A read within 30 s answers with the last reading | The SDK asks the source after every hello and every refresh; a reconnecting Gleanvolt must not mean a burst at VW |
| Where you sign in | Gleanvolt's Car page | The plugin's page, in a frame on the Car page | The protocol carries no sign-in; see [the provider's own page](../../docs/PROVIDERS.md#the-providers-own-page) |
| Name in Gleanvolt | volkswagen.de | Volkswagen ("via Volkswagen" on the Overview) | The provider's name |
| `VwWebsiteLoginPage`, `VwWebsiteClient`'s login chain | | Unchanged, but for the account and log lines naming the plugin's page | |

Not changed: the three-rung sign-in, the single cookie jar, `rememberBrowser`, the per-car routing
lookup, the 5-minute floor between credential sign-ins, and the rule that a failure stops the feed
until you sign in.

## Not done here

- A secret store in the SDK, so the account and the session aren't plain `0600` files on Windows: #416.
- Tested against the real ID.4 through a charge on 2026-11-16 (see #410's comments); nothing left from that.
