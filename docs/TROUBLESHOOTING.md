# Troubleshooting

Most of these fail quietly rather than loudly, which is what makes them worth listing: the service
looks healthy while doing the wrong thing. Problems specific to the Docker deployment from your
computer are in [its own troubleshooting](../deploy/README.md#troubleshooting), and NAS ones in
[Things that go wrong on a NAS](NAS.md#things-that-go-wrong-on-a-nas).

## Setup and the web UI

| Symptom | Cause | Fix |
|---|---|---|
| The UI asks you to set a password, or every page goes to the setup wizard | Setup mode: a required setting is missing. Nothing is polled or controlled until it is set | Finish the wizard, then **Start Gleanvolt**. The log's `SETUP MODE` line lists what is missing; see [Setup mode](WEB_UI.md#setup-mode) |
| Wizard finished, but still in setup mode | Gleanvolt has not been restarted since. With the Windows zip, winget or `dotnet run`, nothing restarts it | **Start Gleanvolt**, or stop and start it yourself |
| The API answers `503` "Gleanvolt is not configured" | Setup mode | As above |
| Forgot the web UI password | — | **Forgot your password?** on the sign-in page, or `set-password` on the host; see [Forgot your password](WEB_UI.md#forgot-your-password) |
| Nothing on `http://<host>:8090` | The UI is on by default, so this is a fault, not a missing setting | Look for `Web UI enabled; listening on port 8090` in the log. No such line means `Web:Enabled=false` is set. Check the port with `ss -ltnp \| grep 8090` |
| Startup fails with "RequireAuthentication is true but PasswordHash is not set" | `Web:RequireAuthentication=true` with no password hash — nobody could ever sign in | Set a password hash, or delete the `RequireAuthentication` setting |
| The wizard's device search finds nothing in a container | It searches the container's own network, not your LAN | Type your LAN's range into the search, or run the container with `--network host` |
| `setup --file` refuses the file as readable by every user | It holds secrets | `chmod 600` the file, then run it again |
| On the Linux install, a command says to run it as `gleanvolt` | The service could not read what another user wrote | `sudo -u gleanvolt /opt/gleanvolt/Gleanvolt.Worker …` |

## Devices and readings

| Symptom | Cause | Fix |
|---|---|---|
| Battery SOC frozen, solar and grid at 0, charger *Unknown* | The inverter and charger addresses are swapped. Both answer on port 502, so it connects fine | Swap them on **Settings → Installation**. The wizard warns when a saved address looks swapped |
| All readings zero, or the log repeats connection errors | Wrong address, or Modbus TCP not enabled on the device | From the machine running Gleanvolt: `nc -vz <address> 502`. See [Finding your devices](DISCOVERY.md) |
| *Port open, no Modbus reply* | Something else holds the device's only Modbus connection — Home Assistant's SolaX or `huawei_solar` integration, evcc, or a second Gleanvolt | Stop the other client; a device serves one or very few |
| Numbers look implausible rather than absent | The register map doesn't match your firmware | Keep charging **Off** and compare with the device's own display before enabling anything that writes. See [Supported PV systems](SUPPORTED_PV_SYSTEMS.md) |
| The house appears to use all the sun every afternoon, and no solar mode starts the car | The grid meter misses a phase | See [Unmetered grid phase](UNMETERED_GRID_PHASE.md) |
| Forecast figures stay empty | No Solcast account, or Solcast refused it | **Settings → Installation → Solar forecast** says which; see [Solcast](CONFIGURATION.md#solcast-solar-forecast) |
| Sessions filed under the wrong day | The controller is running in UTC | Set the time zone in the wizard or on **Settings → Installation**. On Windows, .NET ignores `TZ` |
| The controller stops at startup naming a time zone | An id this host can't resolve | Correct the id. On the Windows container use a Windows id, e.g. `Central Europe Standard Time` |

## Stopping, restarting and logs

| Symptom | Cause | Fix |
|---|---|---|
| Not running, and the log ends with `stopped cleanly at the request of…` | Somebody pressed **Stop service**. It stays down by design | Docker: `docker compose start gleanvolt-controller` (or `docker start`). Linux install: `sudo systemctl start gleanvolt` |
| It comes straight back after **Stop service** | The container's restart policy is `unless-stopped` or `always` | Use `restart: on-failure`, which reads the exit code |
| Gone, with **no** `stopped cleanly` line at the end of the log | The process died — killed, out of memory, or a power cut | That missing line *is* the diagnosis. Docker: `docker inspect --format '{{.State.ExitCode}} {{.State.OOMKilled}}' gleanvolt-controller` |
| `Gave up pausing the charger on shutdown after 00:00:10` | The charger stopped answering while the service shut down | **A car may still be drawing** at the last current. Check the charger |
| Running, but no log files appear (Docker) | The host's `logs/` or `data/` directory isn't writable by uid 1654, the image's user | `sudo chown -R 1654:1654` on both |
| Version reads `0.0.0-dev` | A local build, not a published release | Install a release, or pull the image |
| Entities missing in Home Assistant | Discovery messages don't arrive | See [Gleanvolt with Home Assistant](HOME_ASSISTANT.md), and its troubleshooting |

## Reading the log

The first line names the running build, e.g. `Gleanvolt 1.1.0 (31bf347) starting.` — the same string as
on **Settings → Health** and Home Assistant's device page. If they differ, you are not looking at the
instance you think you are.

A run that ends properly says how on its last line: `stopped cleanly at the request of <surface>`
(stays down) or `stopped cleanly after a termination signal` (comes back). **A log that ends without
either is a run that died.**

Where the log is:

| Install | Log |
|---|---|
| Linux (`.deb`) | `/var/log/gleanvolt/`, and `journalctl -u gleanvolt` |
| Docker | `logs/` on the host, and `docker logs gleanvolt-controller` |
| Windows | `logs\` beside the program |

```bash
# how the last few runs began and ended (Linux install)
grep -h -E "starting\.|stopped cleanly" /var/log/gleanvolt/*.log | tail
```
