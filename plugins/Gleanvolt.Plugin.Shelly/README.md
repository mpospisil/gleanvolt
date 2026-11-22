# Shelly TopAC: a charger as a plugin

The Shelly TopAC Portable EV Charger (EVE01-11R: 6–16 A, three phases, 11 kW, Type 2) for Gleanvolt
(#429), and the first charger driver that runs outside Gleanvolt (#435). Gleanvolt drives the charger
through this plugin: it asks for a reading every poll and sends two commands, set the current and allow
or stop charging. The plugin reads and writes the charger over its local Gen2 RPC API, and serves its own
page for the charger's address, which Gleanvolt links from **Settings → External providers**.

> **Tier: Expected.** Everything is written from Shelly's API documentation, and every test recording is
> synthetic, until a tester with the charger confirms it.

> **Not shipped.** Built with the solution and run by hand; there is no image or release zip. Run it
> from the same version of the source as your Gleanvolt.

## Set up

1. **On the charger**: a fixed address in your router; **authentication off** in the Shelly app (the
   plugin sends no password yet); **Auto balance off** if you use Shelly EM load management.
2. **In Gleanvolt**, under **Settings → Installation**, choose **External plugin** as the charger's brand
   and save. The address fields go away. Restart Gleanvolt.
3. Under **Settings → External providers**, **Make a token** if there is none, and copy it.
4. **Start the plugin** with the token (below), open its page from **Settings → External providers**, enter
   the charger's address and **Save and check**.

Gleanvolt now shows the charger as any other. Try `ChargeControl:DryRun` first: Gleanvolt then sends no
command, and the plugin's page says so.

## What it does

- **Reads** four components under owner `service:0` on every request: `work_state` (the status),
  `phase_info` (the power), `current_limit` (the setpoint) and `start_charging` (the switch).
- **Writes** only `current_limit` and `start_charging`, and the limit only when it differs from what the
  charger last said: whether it is written to flash is not documented.
- **Pauses with the switch.** The TopAC takes nothing below 6 A, and Gleanvolt pauses with 0 A. So a pause
  turns `start_charging` off, and the plugin remembers it: readings say "allowed, 0 A", and the next
  setpoint of 6 A or more writes the limit and turns the switch back on. A restart of the plugin forgets a
  pause; the charger then reads as stopped.
- **Maps the states.** `charger_free` is available, `charger_insert` preparing, `charger_charging`
  charging, `charger_error` faulted. Waiting, complete and ended are the car's doing only while the switch
  is on (preparing, suspended by the car, finishing); with it off, the charger is holding the car, which
  is never read as the car being full.
- **Checks the power's unit.** Shelly documents `total_power` only with zeros, and evcc reads it as kW.
  The plugin compares it with V × I on the three phases once the car draws over 500 W, keeps the unit
  that matches, and says which on its page. Until then the power is V × I.
- **Says what's wrong**, as Gleanvolt shows it under External providers: no address, or a password
  (`not-set-up`, `refused`; both need the owner); a device that isn't a TopAC (`service-changed`); the
  charger not answering (`unreachable`); Auto balance on (needs the owner).

A read the charger doesn't answer is not published: Gleanvolt's read times out, and it shows the charger
as unknown rather than guess.

## Configuration

| Key | Default | |
| --- | --- | --- |
| `Gleanvolt:Endpoint` | `http://localhost:8090` | Gleanvolt's web address |
| `Gleanvolt:Token` | | The token from Gleanvolt's **Settings → External providers**. Required |
| `Urls` | `http://localhost:8097` | Where the page listens. In a container, `http://0.0.0.0:8097` |
| `Gleanvolt:PageUrl` | `Urls`, with `0.0.0.0` read as `localhost` | The page's address **as the owner's browser reaches it** |
| `Gleanvolt:FrameAncestors` | `Gleanvolt:Endpoint`'s origin | Who may show the page in a frame, when the browser reaches Gleanvolt by another name |
| `Shelly:Host` | | The charger's address from the configuration. An address saved on the page wins over it |
| `Shelly:DataDirectory` | `data` | Where the address and the page's keys are kept |
| `Shelly:RequestTimeout` | `00:00:01.500` | How long one RPC call may take. A read is four calls, and Gleanvolt waits 5 seconds |
| `Shelly:MaxCurrentAmps` | `16` | The highest current the charger takes; Gleanvolt never sends more |
| `Shelly:OwnerSettingsInterval` | `00:05:00` | How often the Shelly app's own settings are read for the Auto balance warning |

Any key can be an environment variable (`Gleanvolt__Token`), a command-line argument
(`--Gleanvolt:Token=…`) or a user secret.

## Run it

From the repository, on the machine Gleanvolt runs on, or one on the same LAN as the charger:

```sh
dotnet run --project plugins/Gleanvolt.Plugin.Shelly -- \
  --Gleanvolt:Endpoint=http://gleanvolt.local:8090 \
  --Gleanvolt:Token=<the token>
```

On another machine than Gleanvolt's, set `Urls=http://0.0.0.0:8097` and `Gleanvolt:PageUrl` to the
address your browser reaches it at, e.g. `http://192.168.2.20:8097/`.

## Not yet

- **A password.** The charger's HTTP client sends no credentials; a 401 asks the owner to turn
  authentication off.
- **Search.** Gleanvolt's device search doesn't find the charger behind a plugin; enter the address on the
  page.
- **Shipping.** No image, compose profile or release zip: only a plugin that stands in for a built-in
  ships (#426).
