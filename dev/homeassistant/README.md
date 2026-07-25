# Local Home Assistant + MQTT dev stack

A self-contained environment for developing and testing the Home Assistant integration
([issue #17](https://github.com/mpospisil/solax-controller/issues/17)) — an MQTT broker
(Mosquitto) and a real Home Assistant instance in Docker. The controller runs on the host and
talks to the broker; HA auto-discovers its entities.

## Prerequisites

- Docker + Docker Compose
- The controller run from the host with `dotnet run`

## Bring it up

```bash
cd dev/homeassistant
docker compose up -d
```

- Home Assistant UI: <http://localhost:8123>
- MQTT broker: `localhost:1883` (from the host), `mosquitto:1883` (from HA's container)

## First-time setup

1. Open <http://localhost:8123> and complete Home Assistant onboarding (create a local account).
2. **Settings → Devices & Services → Add Integration → MQTT.**
   - Broker: `mosquitto`
   - Port: `1883`
   - Leave username/password empty (the dev broker allows anonymous connections).
3. Point the controller at the broker (per issue #17's config) — host `localhost`, port `1883` —
   and run it. On connect it publishes MQTT discovery configs and HA creates the device/entities.

## Networking cheat sheet

| Who connects | Broker address |
|---|---|
| Controller (host, `dotnet run`) | `localhost:1883` |
| Home Assistant (container) | `mosquitto:1883` |

## Watch the traffic (debugging without the HA UI)

See exactly what the controller publishes, straight from the broker:

```bash
docker exec -it solax-dev-mosquitto mosquitto_sub -t 'homeassistant/#' -t 'solax/#' -v
```

## Tear down

```bash
docker compose down          # stop containers, keep HA config + broker data
docker compose down -v       # also remove anything in named volumes (none here)
```

To start completely fresh, delete the gitignored runtime dirs:

```bash
rm -rf mosquitto/data/* homeassistant/config/*
```

## Notes

- **Development only.** `allow_anonymous true` in `mosquitto/config/mosquitto.conf` is fine on a
  local machine but must not be used in production. See that file for enabling password auth.
- `mosquitto/data/` and `homeassistant/config/` hold runtime state and are gitignored.
