---
name: gleanvolt-setup
description: Configure a freshly installed Gleanvolt solar EV-charging controller. Prepares the inverter and charger (Modbus TCP, fixed addresses), finds them with Gleanvolt's own device search, writes a setup file with the site's details and applies it with Gleanvolt's command line, then checks the readings. Falls back to the browser setup wizard on a NAS. Use when the user has installed Gleanvolt and asks to set it up or configure it, or when the readings are zero, frozen or missing after setup.
compatibility: Best with a shell on the machine Gleanvolt runs on (Linux package, Docker or Windows). Without one, the user clicks through the browser wizard.
metadata:
  version: "1.3.0"
  gleanvolt-checked-against: "1.3"
  homepage: https://github.com/mpospisil/gleanvolt
  manual: https://mpospisil.github.io/gleanvolt/latest/en/
---

# Set up Gleanvolt

Gleanvolt is installed and its web UI answers on port 8090. If it isn't installed yet, use the
`gleanvolt-install` skill first, or the manual's
[Installation](https://mpospisil.github.io/gleanvolt/latest/en/installation/choosing/) chapter.

You collect what Gleanvolt needs, find the devices, write it all into one **setup file**, and Gleanvolt
applies it with its own command line (`setup --file`). The command checks and saves exactly what the
web wizard would. After one restart the controller runs. The user then finishes the few things that
belong in the browser.

The user manual is the source of truth: the
[setup wizard](https://mpospisil.github.io/gleanvolt/latest/en/setting-up/setup-wizard/) page covers the
same settings. If the manual and this skill disagree, follow the manual and tell the user that the skill
is out of date. Czech manual: replace `/en/` with `/cs/` in any link.

## Rules

- **The web UI password is never yours.** The user types it into `set-password` in their own terminal,
  and that command prints the recovery key there. Never ask for either. Don't let anyone paste them into
  the chat.
- **The Solcast key only with the user's agreement.** Ask whether they want to give
  them to you. Say that a key typed into the chat stays in the conversation history and passes through
  the AI service, and that the alternative is to paste it on **Settings** in the browser afterwards. If
  they decline, leave the key out of the file.
- **Not in the file:** the car, Home Assistant/MQTT and API access. The user sets the car up on the
  **Car** page afterwards. The other two are left off.
- **Say what you will run before you run it.** When `sudo` asks for a password you can't type, give
  the user the commands, complete, to run in a terminal of their own, and wait. Don't suggest your own
  shell passthrough (such as a `!` prefix): sudo has no terminal there.
- **Don't talk to the devices yourself.** Only Gleanvolt's own `discover` talks to them. Many chargers
  accept only one Modbus client, so a second connection is dropped or knocks off the one there.
- **Gleanvolt starts with charging Off,** and doesn't touch the charger until the user picks a mode.
  Setting up is safe while the car is plugged in.

## 0. Which route

| Gleanvolt runs on | Route |
|---|---|
| Linux package (Raspberry Pi, Debian, Ubuntu) and you have a shell there | **The setup file**, with the Linux commands |
| Docker, and you have a shell on the Docker host | **The setup file**, with the Docker commands |
| Windows, and you are running on that PC | **The setup file**, with the Windows commands |
| A NAS, or you have no shell on that machine, or the user prefers clicking | **The browser wizard**: section 7 |

Check that it is a fresh install: `curl -s -o /dev/null -w '%{http_code} %{redirect_url}\n'
http://localhost:8090/` gives `302 …/setup` or `302 …/wizard`. `302 …/login` means it is already set up.
Then change things on **Settings**, not with this skill, unless the user wants to start over.

## 1. Prepare the devices

Manual: [Preparing your inverter and charger](https://mpospisil.github.io/gleanvolt/latest/en/installation/preparing-devices/).

Ask which inverter and which charger the user has, then check with them:

| Device | Turn on | Where |
|---|---|---|
| SolaX inverter | Modbus TCP | the inverter's settings (may be behind the installer password) |
| SolaX charger (HAC, EVC) | Modbus TCP | the charger's settings. Unlink it from the inverter's "Green"/"ECO" charging. |
| Huawei SUN2000 | Modbus TCP, **Enable (Unrestricted)** | FusionSolar app, as the installer (SDongle or EMMA page) |
| Huawei SCharger | Modbus TCP | FusionSolar app, the charger's commissioning page |
| go-e (hardware v3+) | **Local HTTP API v2** | go-e app → Internet → Advanced settings. Switch off its "Eco" mode. |

A Huawei inverter also needs a Huawei smart meter (DTSU666-H/DDSU666-H) or an EMMA.

- **Only one controller per device.** If Home Assistant's SolaX or Huawei Solar integration, evcc, or
  the charger app's own solar or scheduled charging uses these devices, the user switches it off for
  them. At the least, stop it while you search.
- **Fixed addresses.** Ask the user to reserve the inverter's and the charger's current addresses in the
  router (**DHCP reservation** or **static lease**). After a power cut the router may hand out new ones,
  and Gleanvolt would lose the devices.

## 2. Ask for the site

Ask in one go, and explain why each is needed:

| Setting | Why | Notes |
|---|---|---|
| **Latitude, longitude** | weather, sunrise and sunset, the forecast | Right-click the house in an online map. Two decimals are enough. Don't ask for the street address. |
| **Time zone** | when each day starts | Offer the host's: `timedatectl show -p Timezone --value`. If that is UTC, ask. |
| **Usable capacity of the home battery (kWh)** | the Forecasted and Targeted modes plan with it | from the data sheet or the inverter app |
| **A name for the site** (optional) | shown in the UI | e.g. "Home" |
| **Solcast** (optional) | the solar forecast those two modes plan with | Does the user have a free [Solcast](https://mpospisil.github.io/gleanvolt/latest/en/setting-up/solar-forecast/) account? Its **API key** and **Rooftop site resource id**, if they agree to give them to you (Rules). |
| **Weather** (optional) | records the weather during each charge | Only whether they want it. It comes from the OpenWeather plugin, which takes its own key on its own page, later. See [Weather](https://mpospisil.github.io/gleanvolt/latest/en/setting-up/weather/). |

## 3. Find the devices

Gleanvolt's own search, read-only. It takes a minute or two.

```bash
/opt/gleanvolt/Gleanvolt.Worker discover --json                               # Linux package
docker run --rm --network host ghcr.io/mpospisil/gleanvolt:latest discover --json   # Docker
gleanvolt discover --json                                                      # Windows
```

If the devices are on another network than this machine's (a separate IoT Wi-Fi, say), add
`--subnet 192.168.1.0/24` with that network's range. If the user already knows the addresses, skip the
search, and ask for each device's brand, address, port and unit id instead.

In the JSON, `devices` lists what answered. For each one: `host`, `port`, `unitId`, `role`
(`Inverter` or `Charger`), `model`, `driver`, `topology` and `note`. `warnings` says anything about the
scan itself.

- Pick **the inverter by its `role`**, not by the order. Show the user its model and address, and ask
  them to confirm. A charger in the list is only there to tell the two apart: Gleanvolt's setup doesn't
  configure the charger. It is a plugin, added afterwards (section 6).
- **Swapped addresses are the mistake that matters.** Both answer Modbus on port 502, so a swapped pair
  connects without an error. It shows up later only as a battery level that never changes and solar
  showing 0. If a role is missing or doubtful, ask, and compare with the router's device list.
- Rows that weren't identified (a router, a printer) are not candidates.

When it finds nothing useful:

| What you see | Cause | Fix |
|---|---|---|
| No devices, or only unidentified ones | Modbus TCP (go-e: local API) is off | Step 1 |
| `note` says port open, no Modbus reply | Another client holds the device: Home Assistant, evcc, a second Gleanvolt | The user stops it, then search again |
| It scanned a `172.x` network | Docker without `--network host` can't see the LAN | Add `--subnet` with the LAN's range. The container itself needs `--network host` too: the `gleanvolt-install` skill. |

## 4. Write the setup file

Write it **private from the start**: it may hold keys.

```bash
umask 077
cat > ~/gleanvolt-site.json <<'EOF'
{
  "Pv": {
    "Id": "home",
    "Name": "Home",
    "Latitude": 50.08,
    "Longitude": 14.44,
    "Inverter": { "Driver": "solax", "Host": "192.168.1.10", "Port": 502, "UnitId": 1 }
  },
  "Controller": { "TimeZone": "Europe/Prague" },
  "ChargeControl": { "Forecast": { "BatteryCapacityKWh": 9 } },
  "Solcast": { "ApiKey": "…", "ResourceId": "…" },
  "Weather": { "Source": "External" }
}
EOF
```

- **No `Chargers` block.** The charger isn't part of setup: `setup --file` refuses its keys with a message.
- `Driver`, `Host`, `Port`, `UnitId` come from the inverter's search result. If it has a `topology`
  (a Huawei inverter behind an EMMA), add `"Topology": "emma"` to the inverter.
- `Id` is lowercase letters, digits, `-` or `_`.
- `Weather:Source` is `External` when the user wants the weather, or `NotProvided` when not. Gleanvolt
  has no weather key: the OpenWeather plugin is started and given its key afterwards (below).
- Leave out what the user didn't give: the `Solcast` and `Weather` blocks, the battery, the name. Never
  write an empty placeholder: an empty value means "remove the saved one".
- Every key the file may hold: `/opt/gleanvolt/Gleanvolt.Worker setup --print-schema`. Anything else is
  refused by name.

Then check it. This writes nothing:

```bash
/opt/gleanvolt/Gleanvolt.Worker setup --validate ~/gleanvolt-site.json      # Linux package
gleanvolt setup --validate gleanvolt-site.json                               # Windows
```

With Docker, it runs in the apply step below. Fix every problem it lists, then show the user the file
**with the keys masked** and say what it will set.

## 5. Apply it, set the password, restart

Explain first: the command saves the settings, `set-password` asks for the web UI password twice and
prints a **recovery key** that is shown only once. They keep it in a password manager or on paper, and
don't paste it here.

### Linux package

`setup` must run as the service user, `gleanvolt`, or the service couldn't read what it writes. So the
user runs this block in their own terminal:

```bash
sudo install -o gleanvolt -g gleanvolt -m 600 ~/gleanvolt-site.json /var/lib/gleanvolt/site.json
sudo -u gleanvolt /opt/gleanvolt/Gleanvolt.Worker setup --file /var/lib/gleanvolt/site.json
sudo -u gleanvolt /opt/gleanvolt/Gleanvolt.Worker set-password
sudo rm /var/lib/gleanvolt/site.json
sudo systemctl restart gleanvolt
```

### Docker

```bash
sudo install -o 1654 -g 1654 -m 600 ~/gleanvolt-site.json /opt/gleanvolt/data/site.json
docker run --rm -v /opt/gleanvolt/data:/app/data -v /opt/gleanvolt/logs:/app/logs \
  ghcr.io/mpospisil/gleanvolt:latest setup --file /app/data/site.json
docker run --rm -it -v /opt/gleanvolt/data:/app/data -v /opt/gleanvolt/logs:/app/logs \
  ghcr.io/mpospisil/gleanvolt:latest set-password
sudo rm /opt/gleanvolt/data/site.json
docker restart gleanvolt-controller
```

Use the data folder the container was started with, if it isn't `/opt/gleanvolt/data`
(`docker inspect gleanvolt-controller --format '{{.HostConfig.Binds}}'`).

### Windows

You can apply the file yourself, as the same Windows user Gleanvolt runs as. The keys are sealed to that
user.

```powershell
gleanvolt setup --file gleanvolt-site.json
```

Then the user, in their own terminal: `gleanvolt set-password`. Then they close Gleanvolt's window and
start `gleanvolt` again.

### After the block

If `setup --file` printed problems, nothing was saved: fix the file and give them the block again.
Otherwise, **delete `~/gleanvolt-site.json`** (or `gleanvolt-site.json` on Windows). The keys are in
Gleanvolt's secret store now.

## 6. Check it

About 20 seconds after the restart:

```bash
curl -s -o /dev/null -w '%{http_code} %{redirect_url}\n' http://localhost:8090/
```

- `302 …/login`: the configuration is complete and the controller is running.
- `302 …/wizard`: something required is still missing. Run `setup --validate` on what was applied, or
  ask the user to open the web UI, which says what is missing.

The log isn't readable without sudo. Ask the user to run `sudo journalctl -u gleanvolt -n 40 --no-pager`
(Docker: `docker logs --tail 40 gleanvolt-controller`) and paste it. `Timed out connecting to Modbus
device at <address>` means that address is wrong, or Modbus TCP is off there.

Then the user opens `http://<this machine's address>:8090` (`hostname -I | awk '{print $1}'`), signs in,
and reads the **Overview** to you.
Manual: [The Overview](https://mpospisil.github.io/gleanvolt/latest/en/using/overview/).

| Check | Good | If not |
|---|---|---|
| **LIVE** next to the title | shown | reload the page |
| **Battery** | a plausible %, moving over minutes while it charges or discharges | frozen, with Solar 0 in daylight: **inverter and charger swapped.** Swap them on Settings, Save, restart |
| **Solar** | plausible for the time of day, close to the inverter's own app | everything 0: Gleanvolt can't reach the inverter |
| **Car** | plugged in or not, as it really is, about 0 kW | charger unreachable, or another client holds it |
| **Solar forecast** | today and tomorrow in kWh, if Solcast was set | empty: **Settings → Solar forecast** says why |
| **Settings → Health → Last poll** | a few seconds ago | Gleanvolt isn't polling: read the log |

**A Solcast "429" or "limit reached" is the daily quota, not a fault.** The free plan allows about ten
calls a day, and Gleanvolt uses one at every start and one every 12 hours. Don't restart to retry: each
start spends another call. The forecast arrives the next day.

If the figures are clearly wrong rather than missing, charging stays **Off** until they agree with the
devices' own apps. Suggest reporting it on [GitHub](https://github.com/mpospisil/gleanvolt/issues) with
the models. If the house seems to eat all the sun every afternoon and no solar mode starts, the grid
meter may miss a phase: see
[When the grid meter misses a phase](https://mpospisil.github.io/gleanvolt/latest/en/how-it-works/unmetered-phase/).
Everything else: [Troubleshooting](https://mpospisil.github.io/gleanvolt/latest/en/troubleshooting/).

**What is left for the browser,** on **Settings** and the **Car** page:

- **The charger:** its plugin, started with the provider token from **Settings → External providers**
  (**Make the token** if there is none), and the charger's address entered on the plugin's own page.
  Until it connects, no charging mode can start. See
  [Your charger](https://mpospisil.github.io/gleanvolt/latest/en/setting-up/charger/).
- **The car:** battery size, phases, currents, and where its battery level comes from. See
  [Your car and its battery level](https://mpospisil.github.io/gleanvolt/latest/en/setting-up/car/).
- Any Solcast key the user kept to themselves.
- **The weather,** if wanted: the OpenWeather plugin, started with the provider token, and its
  OpenWeatherMap key entered on its page under **Settings → Weather**. See
  [Run the OpenWeather plugin](https://mpospisil.github.io/gleanvolt/latest/en/setting-up/weather/#run-the-openweather-plugin).
- Optionally, the [grid tariff](https://mpospisil.github.io/gleanvolt/latest/en/setting-up/grid-tariff/).

## 7. The browser wizard (NAS, or no shell)

Manual: [The setup wizard](https://mpospisil.github.io/gleanvolt/latest/en/setting-up/setup-wizard/).
Steps 1 and 2 of this skill still apply. Then walk the user through it, one step at a time:

1. **Password.** The user opens `http://gleanvolt.local:8090` (or the machine's address with `:8090`),
   sets a password of at least 8 characters, and saves the recovery key it shows once.
2. **Inverter.** **Search**, then **Use as inverter** on the row the **Identified as** column calls the
   inverter, then **Save** and **Next**. The table in step 3 covers a search that finds nothing. In a NAS
   container, the network must be **host**.
3. **Site.** Latitude, longitude, time zone and the battery's usable kWh. **Save**, **Next**.
4. **Finish.** **Make the token** under **Your charger** for the charger's plugin, then **Restart** and
   **Yes, restart it**. The Overview opens after about half a minute. The forecast, the car, the tariff
   and the weather are on **Settings** and the **Car** page, linked from Finish.

**Each step needs Save.** Next only moves on. Then check it as in step 6.

## What to report at the end

- which inverter Gleanvolt uses, at which address, and a reminder to reserve it and the charger's in the
  router;
- that the charger is added through its plugin, with the provider token (section 6);
- what was set, and what is left for the browser (the car, any keys not given);
- that charging is **Off** until the user chooses a mode. See
  [Which charging mode?](https://mpospisil.github.io/gleanvolt/latest/en/using/modes/choosing/).
