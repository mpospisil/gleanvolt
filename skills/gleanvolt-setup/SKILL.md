---
name: gleanvolt-setup
description: Guide the user through setting up a freshly installed Gleanvolt solar EV-charging controller. Covers preparing the inverter and charger (Modbus TCP, fixed addresses), the web UI's password page and its six-step setup wizard, and a final health check. Use when the user has installed Gleanvolt and asks to set it up or configure it, or when the wizard can't find the devices, or the readings are zero, frozen or missing after setup.
compatibility: The user clicks through Gleanvolt's web UI themselves. Optional shell access to the Gleanvolt host or the local network, for read-only checks.
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

Your job is to walk the user through the setup in the browser: **you explain each choice, the user
clicks.** At the end, check with them that Gleanvolt reads their devices.

The user manual is the source of truth. Each step below links to its page. If the manual and this
skill disagree, follow the manual and tell the user that the skill is out of date. Czech manual:
replace `/en/` with `/cs/` in any link.

## Rules

- **Never type, ask for, repeat or store the password, the recovery key, or any API key or account
  sign-in** (Solcast, the car's account, Home Assistant). The user types them into the web page. If the
  user pastes one into the chat, tell them it is now in the chat history and suggest changing it.
- **One wizard step at a time.** Say what the step asks and what to choose, then wait for the user to
  tell you what they see.
- **Each step needs Save.** Next only moves on; what isn't saved is lost.
- **Don't connect to the charger yourself.** Many chargers accept only one Modbus client. While
  Gleanvolt is connected, a second connection, including a scan or a test from your shell, is dropped
  or knocks Gleanvolt off. Only the wizard's own search talks to the devices.
- **Gleanvolt starts with charging Off** and doesn't touch the charger until the user picks a mode.
  Setting up is safe while the car is plugged in.

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

Then two more things:

- **Only one controller per device.** If Home Assistant's SolaX or Huawei Solar integration, evcc, or the
  charger app's own solar or scheduled charging uses these devices, the user switches it off for them.
  At the least, stop it while the wizard searches.
- **Fixed addresses.** In the router, reserve the current address of the inverter and of the charger
  (**DHCP reservation** or **static lease**). After a power cut the router may hand out new ones, and
  Gleanvolt would lose the devices. The router's list of connected devices shows the current addresses.
  Note them down. They are useful in step 3.

## 2. Open Gleanvolt and set the password

Manual: [The setup wizard](https://mpospisil.github.io/gleanvolt/latest/en/setting-up/setup-wizard/).

1. The user opens **`http://gleanvolt.local:8090`** on a phone or computer on the same network. If that
   doesn't open, they use `http://<the host's address>:8090`.
2. They type a password of at least 8 characters, twice, and press **Set the password**. It is one
   password for the household. It stops a guest's phone from changing how the car charges.
3. Gleanvolt shows a **recovery key**, **only once**. The user copies it into their password manager,
   prints it, or writes it down. Don't ask them for it.
4. They press **I have saved it, sign in** and sign in.

## 3. The wizard

Six steps, listed at the top of the page. Only **Devices** is required. Everything after it can be
skipped and set later on **Settings**.

### Step 1: Devices (required)

Manual: [Finding your devices](https://mpospisil.github.io/gleanvolt/latest/en/setting-up/finding-devices/).

1. The user presses **Search** and waits a minute or two.
2. In the results, they press **Use as inverter** on the inverter's row and **Use as charger** on the
   charger's row. They choose by the **Identified as** column, not by the order of the rows. Ask them to
   read you both rows if unsure.
3. They check that the form below now has both addresses, then press **Save**. Gleanvolt reads one
   value from each device to check its role.
4. **Next.**

**The inverter and the charger swapped** is the mistake that matters here. Both answer Modbus on port
502, so a swapped pair connects without an error. It only shows up later as a **battery level that never
changes and solar showing 0**. Gleanvolt warns at Save when a device answers like the other role. Tell
the user not to override that warning without checking the addresses against the router's list.

If a device doesn't answer at Save (a charger switched off, for example), Gleanvolt asks first. **Save
anyway** only if the user is sure of the address.

When the search finds nothing, go through this list, then search again:

| What the user sees | Cause | Fix |
|---|---|---|
| Nothing, or only "not identified" rows | Modbus TCP (go-e: local API) is off | Step 1 of this skill |
| "Port open, no Modbus reply" | Another client holds the device: Home Assistant, evcc, a second Gleanvolt | Stop it, search again |
| It searched a `172.x` network | Gleanvolt runs in Docker without `--network host`, so it can't see the LAN | Type the home range in **Network**, e.g. `192.168.1.0/24`, or recreate the container with `--network host` (the `gleanvolt-install` skill) |
| Devices are on a separate IoT network | The search looks only where it is told | Type that range in **Network** |

If the user knows the addresses, they can type them into the form instead. Brand, **Host**, **Port** 502
(go-e: 80), **Unit id** usually 1. A Huawei inverter behind an EMMA needs **Connected through: EMMA**
and the EMMA's address.

### Step 2: Site

Ask for, and explain:

- **Latitude and longitude** of the home, for the weather, sunrise and sunset. Right-click the house in
  an online map to see them. It's fine to help the user find them if they name their town. Don't ask for
  the street address.
- **Time zone**, e.g. `Europe/Prague`. Without it, charges are listed under the wrong day.
- **Usable capacity (kWh)** of the home battery, from its data sheet or the inverter app. The
  Forecasted and Targeted modes plan with it.

**Save**, then **Next**.

### Step 3: Forecast (optional)

Manual: [Solar forecast](https://mpospisil.github.io/gleanvolt/latest/en/setting-up/solar-forecast/).

A free **Solcast** account gives the forecast that the Forecasted and Targeted modes plan with. Without
it, Gleanvolt charges from the sunshine there is right now. If the user has an account, they paste the
**API key** and the **Rooftop site resource id** themselves, then press **Save and fetch**. Otherwise
they press **Next (or skip)**.

**A Solcast "429" or "limit reached" is the daily quota, not a fault.** The free plan allows about ten
calls a day. Gleanvolt uses one every 12 hours and one at every start, so a few restarts during setup
can use the day up. Nothing to fix: the forecast arrives the next day. Don't suggest restarting to
retry, because each restart spends another call.

### Step 4: Car (optional)

Manual: [Your car and its battery level](https://mpospisil.github.io/gleanvolt/latest/en/setting-up/car/).

The car's battery size and charging limits let the user ask for "80 % by 7:00" instead of kWh. Fill in
what they know. A car account sign-in is typed by the user only. **Save**, then **Next (or skip)**.

### Step 5: Extras (optional)

- **Tariff**: the cheap grid hours. See [Grid tariff](https://mpospisil.github.io/gleanvolt/latest/en/setting-up/grid-tariff/).
- **Weather**: records the weather during each charge.
- **Home Assistant (MQTT)** and **API access**: only if the user connects other software. Otherwise
  leave them off.

The whole step can be skipped.

### Step 6: Finish

The step lists anything required that is missing, with a link to the step where it is set. When it is
complete, the user presses **Restart** next to **Start Gleanvolt**, then **Yes, restart it**. After
about half a minute the **Overview** opens.

On Windows nothing restarts Gleanvolt for it. If the page doesn't come back, the user types `gleanvolt`
in the terminal again.

## 4. Health check

Manual: [The Overview](https://mpospisil.github.io/gleanvolt/latest/en/using/overview/).

Ask the user to read the Overview to you, and check:

| Check | Good | If not |
|---|---|---|
| **LIVE** next to the title | shown | the page lost its connection: reload |
| **Battery** level | a plausible %, changing over minutes when the battery is charging or discharging | frozen, with Solar 0 in daylight: **inverter and charger swapped.** Swap them on Settings, Save, restart |
| **Solar** | plausible for the time of day, close to the inverter's own app | everything 0: Gleanvolt can't reach the inverter. Check the address and Modbus TCP |
| **Car** | "not plugged in" or "plugged in" matching reality, about 0 kW | charger unreachable, or the charger is held by another client |
| **Solar forecast** | today and tomorrow in kWh, if Solcast was set up | empty: **Settings → Solar forecast** says why (wrong key, unknown site, or the daily limit) |
| **Settings → Health → Last poll** | a few seconds ago | Gleanvolt isn't polling: read the log |

Ask the user to compare the figures with the inverter's and the charger's own app. If they are clearly
wrong rather than missing, charging stays **Off** until they agree. Suggest reporting it on
[GitHub](https://github.com/mpospisil/gleanvolt/issues) with the device models.

If the afternoon house load looks as if it eats all the sun and no solar mode ever starts, the grid
meter may miss a phase: see
[When the grid meter misses a phase](https://mpospisil.github.io/gleanvolt/latest/en/how-it-works/unmetered-phase/).

Everything else is in [Troubleshooting](https://mpospisil.github.io/gleanvolt/latest/en/troubleshooting/).

## What to report at the end

- which devices Gleanvolt found, and at which addresses (remind the user to reserve them in the router
  if they haven't);
- which optional steps were skipped, and that they can be set later on **Settings**;
- that charging is **Off** until they choose a mode. Point them to
  [Which charging mode?](https://mpospisil.github.io/gleanvolt/latest/en/using/modes/choosing/).
