# Shelly TopAC recordings

The recordings `ShellyTopAcConformanceTests` replays (issue #429). Each is an HTTP dump: the body of every request
the driver makes, by path and query. Settings → charger → **Save register snapshot** writes the same format for a
TopAC.

| File | State | Used for |
|---|---|---|
| `shelly-idle.json` | No car (`charger_free`), not started, 16 A limit | Idle reads Available and 0 W; not started is not "allowed" |
| `shelly-plugged-in.json` | A car waiting (`charger_wait`), started | Plugged in reads Preparing, 0 W |
| `shelly-charging.json` | Charging at 16 A on three phases, `total_power` 11.04 (kW), started | Charging, 11 040 W, 16 A, allowed |
| `shelly-other.json` | Nothing recorded, so every request is answered 404 | The probe refuses another Shelly or another web server |

**Every file is synthetic.** They were written from Shelly's API documentation
([TopAC Portable EV Charger](https://shelly-api-docs.shelly.cloud/gen2/Devices/ShellyX/XT1/TopACPortableEVCharger)):
the components `work_state`, `phase_info`, `current_limit` and `start_charging` under the owner `service:0`, read
with a GET that answers `{"value": …, "source": …, "last_update_ts": …}`, and `Service.GetConfig`. Their `note` says
so. Shelly documents only `charger_free` among the work states, and shows `phase_info` with zeros; the other state
names and kilowatts for `total_power` are evcc's reading of the device.

A tester's snapshot has to confirm:

- the work state with a car waiting, charging, paused by `start_charging` off, and finished;
- that `total_power` is kilowatts (the driver checks it against volts × amps either way);
- that `start_charging` reads back what was written, and stays true across a session's end;
- whether `current_limit` takes as many writes as the control loop makes.

None of the requests carries the device id, the MAC address or the Wi-Fi settings, so there is nothing to redact.
