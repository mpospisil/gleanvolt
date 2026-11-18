# OpenWeather: the weather as a plugin

OpenWeatherMap as an external weather provider for Gleanvolt (#424): since #425 the only way Gleanvolt
gets OpenWeatherMap's weather, which it no longer calls itself. It began as a copy of the built-in
integration. It reads the current conditions at the site from OpenWeatherMap's free
current-weather endpoint, publishes them to Gleanvolt, and serves its own page for the API key, which
Gleanvolt shows under **Settings → Weather**.

Gleanvolt records the weather on each charging session, when it opens and when it closes. Nothing in
charge control reads it.

> **Shipped with Gleanvolt** (#426): an image, `ghcr.io/mpospisil/gleanvolt-plugin-openweather`, and a
> zip per platform on each release, from the same tag as Gleanvolt. Run the same version as your
> Gleanvolt.

> **Run it with External chosen** under **Weather** on Gleanvolt's Settings. An installation that used
> the built-in has External chosen for it at its first start after #425, and a notice there until this
> connects. The key Gleanvolt kept is deleted, not handed over: enter it on this plugin's page, or pass
> it as `OPENWEATHER_API_KEY` (the Pi's compose file falls back to the old `WEATHER_API_KEY`).

## What it does

- **Answers Gleanvolt** when it asks for the weather: when a session opens or closes, and when
  `GET /api/v1/forecast?weather=true` is called. It also publishes once after every hello.
- **Spends few calls.** It reads OpenWeatherMap at most once every 10 minutes, and answers a sooner
  request with the reading it has. A day of charging costs a handful of calls; the free plan allows a
  thousand.
- **Says what's wrong.** No key, or no location in Gleanvolt: Gleanvolt shows that the owner is needed
  (`not-set-up`). A refused key: the same (`refused`). OpenWeatherMap down or limiting calls: Gleanvolt
  shows the weather isn't being read (`unreachable`, `rate-limited`). Nothing is retried on a clock:
  Gleanvolt's next request is the next try.

Its page shows the plugin's status, the site Gleanvolt gave it, the last reading, and the key: enter it,
**Save and check** (one call), or **Remove** it. The key is never shown back.

## Configuration

| Key | Default | |
| --- | --- | --- |
| `Gleanvolt:Endpoint` | `http://localhost:8090` | Gleanvolt's web address |
| `Gleanvolt:Token` | | The token from Gleanvolt's **Settings → External providers**. Required |
| `Urls` | `http://localhost:8096` | Where the page listens. In a container, `http://0.0.0.0:8096` |
| `Gleanvolt:PageUrl` | `Urls`, with `0.0.0.0` read as `localhost` | The page's address **as the owner's browser reaches it**, e.g. `http://192.168.2.7:8096/` |
| `Gleanvolt:FrameAncestors` | `Gleanvolt:Endpoint`'s origin | Who may show the page in a frame (CSP `frame-ancestors`), when the browser reaches Gleanvolt by another name than the endpoint |
| `OpenWeather:ApiKey` | | A key from the configuration (also read as `OPENWEATHER_API_KEY`). A key saved on the page wins over it |
| `OpenWeather:DataDirectory` | `data` | Where the key is kept |
| `OpenWeather:BaseUrl` | `https://api.openweathermap.org/` | OpenWeatherMap's address |
| `OpenWeather:RequestTimeout` | `00:00:10` | How long one call may take. Gleanvolt waits 15 seconds for an answer |
| `OpenWeather:MinimumTimeBetweenReads` | `00:10:00` | A request sooner than this answers with the last reading |

Any key can be an environment variable (`Gleanvolt__Token`), a command-line argument
(`--Gleanvolt:Token=…`) or a user secret.

The site's location is not configuration: Gleanvolt sends it when the plugin connects.

## The data directory holds the key

`OpenWeather:DataDirectory` holds `api-key`, the OpenWeatherMap key, through the SDK's secret store
(#416). On Linux it is a plain file readable and writable by the plugin's user alone (`0600`), in a
directory only that user can open (`0700`): not encrypted. On Windows it is encrypted with DPAPI for the
user the plugin runs as. Keep the directory out of backups and shares you wouldn't give the key to, and
out of the repository. The key is never logged.

`keys/` beside it holds the page's own ASP.NET Core keys (its Blazor circuits), not the API key.

## Run it while developing

Against a Gleanvolt on the Pi, from a development machine:

1. In Gleanvolt, under **Settings → Weather**, choose **External** and save. No restart is needed.
2. Store the token once, out of the shell history:

   ```sh
   dotnet user-secrets set Gleanvolt:Token gvp_... --project plugins/Gleanvolt.Plugin.OpenWeather
   ```

3. Run it, listening on the LAN so the browser reaches the page:

   ```sh
   dotnet run --project plugins/Gleanvolt.Plugin.OpenWeather -- \
     --Gleanvolt:Endpoint=http://192.168.2.7:8090 \
     --Urls=http://0.0.0.0:8096 \
     --Gleanvolt:PageUrl=http://<this machine's address>:8096/
   ```

4. Open Gleanvolt's **Settings → Weather**. The plugin's page is in the frame: enter the key and press
   **Save and check**.

On a different host than Gleanvolt, over `http`, the browser won't keep the page's cookie inside
Gleanvolt's frame, so a reload inside the frame may answer 403: open Settings again. See
[the known limits](../../docs/PROVIDERS.md#the-providers-own-page).

## Run it from a release

Choose **External** under **Weather** on Gleanvolt's Settings, and have the token from **Settings →
External providers** at hand. On the same machine as Gleanvolt, the token is all it needs: it connects to
`http://localhost:8090`, says its page is `http://localhost:8096/`, and Gleanvolt sends the browser there
under the name the browser opened Gleanvolt at (#426). On another machine, set `Gleanvolt:Endpoint`,
`Gleanvolt:PageUrl` and `Gleanvolt:FrameAncestors` as in [Run it while developing](#run-it-while-developing).

**The Pi, with the deploy scripts.** `GLEANVOLT_PLUGINS=openweather` and `GLEANVOLT_PROVIDER_TOKEN` in
`/opt/gleanvolt/.env`, then deploy: see
[Shipped plugins](../../deploy/README.md#shipped-plugins). The compose service is
`gleanvolt-plugin-openweather` under the `openweather` profile, its data in `plugins/openweather`.

**Docker, by hand:**

```sh
sudo mkdir -p /opt/gleanvolt/plugins/openweather
sudo chown 1654:1654 /opt/gleanvolt/plugins/openweather && sudo chmod 700 /opt/gleanvolt/plugins/openweather

docker run -d --name gleanvolt-plugin-openweather --restart unless-stopped --init \
  --network host \
  -e Gleanvolt__Token=gvp_... \
  -v /opt/gleanvolt/plugins/openweather:/data \
  ghcr.io/mpospisil/gleanvolt-plugin-openweather:latest
```

The image runs as the non-root `app` user (UID 1654), and `/data` is that user's alone. A named volume
takes those permissions over; a bind-mounted host directory must be `chown 1654:1654` and `chmod 700`.
On a bridge network instead of the host's, publish the port (`-p 8096:8096`) and set the three addresses
above, since the plugin no longer shares Gleanvolt's `localhost`.

**Windows, or Linux without Docker.** Download `gleanvolt-plugin-openweather-<version>-<platform>.zip`
from the release, unzip it, and start it in its folder, which then holds `data/`:

```powershell
.\Gleanvolt.Plugin.OpenWeather.exe --Gleanvolt:Token=gvp_...
```

It listens on `localhost` only, so its page opens in a browser on that machine. For another device, add
`--Urls=http://0.0.0.0:8096` and allow it through the firewall. It stops when its terminal closes.

**Building the image yourself**, for a change not yet released:

```sh
docker build --platform linux/arm64 -f plugins/Gleanvolt.Plugin.OpenWeather/Dockerfile -t gleanvolt-plugin-openweather .
docker save gleanvolt-plugin-openweather | ssh martin@192.168.2.7 docker load
```

## What changed from the built-in, and why

The code is a copy of what was `src/Gleanvolt.Infrastructure/OpenWeather/` (`OpenWeatherMapService`,
`WeatherKeyStore`, `WeatherOptions`) and of the key's place on Settings (`WeatherKeyForm`), all removed
from Gleanvolt in #425.

| What | Built-in | Plugin | Why |
| --- | --- | --- | --- |
| Dependencies | `Gleanvolt.Core`, `Gleanvolt.Infrastructure` | `Gleanvolt.Providers.Client` only | A plugin sees only the SDK |
| The reading | `WeatherReading` (`WeatherObservation` + sunrise, sunset) | The contract's `WeatherData`, flat | Gleanvolt maps it back; the contract is one record |
| Missing figures | Cloud cover defaulted to 0 when the answer had none | Left out (`null`) | Absent means unknown in the contract; Gleanvolt's stored figures became nullable for it |
| Units | `Weather:Units`, `metric` by default | Always `metric` | The contract is in SI; there is nothing to choose |
| Location | `PvSystemInfo`, read at start | The welcome's `site`, from Gleanvolt | One place for the location |
| The key | `Weather:ApiKey` / `WEATHER_API_KEY`, or Settings, in Gleanvolt's secret store | `OpenWeather:ApiKey` / `OPENWEATHER_API_KEY`, or the plugin's page, in the SDK's secret store (#416) | A plugin has no Gleanvolt settings or secret store. The same protection: `0600` on Linux, DPAPI on Windows |
| Failures | Logged once, the session recorded without weather | A status Gleanvolt shows (`not-set-up`, `refused`, `rate-limited`, `unreachable`, `service-changed`), in English and Czech | The owner sees a refused key on Settings, not only in a log |
| A call per request | Every session end asks OpenWeatherMap | At most one call per 10 minutes | The SDK asks after every hello too; a reconnecting Gleanvolt must not mean a burst of calls |
| Time limit | 5 s per call (`Weather:RequestTimeout`) | 10 s per call; Gleanvolt waits 15 s for the answer | The answer travels one more hop |
| The key check | On Settings, `Save and check` | On the plugin's page, `Save and check`; a good key also publishes a reading at once | The status in Gleanvolt clears without waiting for the next session |

Not changed: the endpoint (`data/2.5/weather`, the free plan), the field mapping, hPa to Pa, Unix
seconds to UTC, and sunrise or sunset of 0 read as "not reported".
