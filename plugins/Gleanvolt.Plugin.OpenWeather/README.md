# OpenWeather: the weather as a plugin

A copy of Gleanvolt's built-in OpenWeatherMap integration, run outside Gleanvolt as an external weather
provider (#424). It reads the current conditions at the site from OpenWeatherMap's free
current-weather endpoint, publishes them to Gleanvolt, and serves its own page for the API key, which
Gleanvolt shows under **Settings → Weather**.

Gleanvolt records the weather on each charging session, when it opens and when it closes. Nothing in
charge control reads it.

> **Not for the image or the release, yet.** The plugin is built with the solution so a change that
> breaks it is seen. It is not in Gleanvolt's container image or the release packages until #426 ships
> plugins. Then #425 removes the built-in, and this plugin becomes the way to get OpenWeatherMap's
> weather.

> **One source at a time.** Run it with **External** chosen under **Weather** on Gleanvolt's Settings.
> Gleanvolt then doesn't call OpenWeatherMap itself. To go back, choose **OpenWeather** there: the key
> saved in Gleanvolt is still there.

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

## Run it on the Pi

Its own container, outside the compose stack and `deploy.sh`, which never touch it until #426. Build the
image for the Pi on a development machine, from the repository root, and copy it over:

```sh
docker build --platform linux/arm64 -f plugins/Gleanvolt.Plugin.OpenWeather/Dockerfile -t gleanvolt-openweather .
docker save gleanvolt-openweather | ssh martin@192.168.2.7 docker load
```

Then on the Pi:

```sh
docker run -d --name gleanvolt-openweather --restart unless-stopped \
  -p 8096:8096 \
  -v gleanvolt-openweather:/data \
  -e Gleanvolt__Endpoint=http://192.168.2.7:8090 \
  -e Gleanvolt__Token=gvp_... \
  -e Gleanvolt__PageUrl=http://gleanvolt.local:8096/ \
  -e "Gleanvolt__FrameAncestors=http://gleanvolt.local:8090 http://192.168.2.7:8090" \
  gleanvolt-openweather
```

Use the **name you open Gleanvolt at** in the browser for both of the last two, as for the Volkswagen
plugin; see its README for why. `Gleanvolt:Endpoint` stays an address the container can resolve.

The image runs as the non-root `app` user (UID 1654), and `/data` is that user's alone. A named volume
takes those permissions over; a bind-mounted host directory must be `chown 1654:1654` and `chmod 700`.

## What changed from the built-in, and why

The code is a copy of `src/Gleanvolt.Infrastructure/OpenWeather/` (`OpenWeatherMapService`,
`WeatherKeyStore`, `WeatherOptions`) and of the key's place on Settings (`WeatherKeyForm`).

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
