#!/usr/bin/env bash
#
# Deploy the SolaX stack to the Raspberry Pi (issue #26).
#
# Copies the committed stack files to the Pi, then pulls the image from GHCR and restarts. It never
# copies secrets: /opt/solax/.env is created once, by hand, on the Pi.
#
#   ./deploy/deploy.sh                          # deploy whatever .env pins (default: latest)
#   IMAGE_TAG=sha-abc1234 ./deploy/deploy.sh    # deploy/roll back to a specific build
#   PI_HOST=pi@192.168.2.7 ./deploy/deploy.sh   # non-default host
#
# First-time setup of the Pi is documented in deploy/README.md and is deliberately not automated
# here -- it needs sudo, and a deploy should never be the thing that creates or chowns directories.

set -euo pipefail

PI_HOST="${PI_HOST:-pi@192.168.2.7}"
REMOTE_DIR="${REMOTE_DIR:-/opt/solax}"
SSH_OPTS="${SSH_OPTS:-}"

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# shellcheck disable=SC2086  # SSH_OPTS is intentionally word-split
ssh_pi() { ssh $SSH_OPTS "$PI_HOST" "$@"; }

say() { printf '\n\033[1m==> %s\033[0m\n' "$*"; }

say "Checking $PI_HOST:$REMOTE_DIR"
if ! ssh_pi "test -d '$REMOTE_DIR'"; then
    cat >&2 <<EOF
error: $REMOTE_DIR does not exist on $PI_HOST.

This is first-time setup -- see deploy/README.md ("Prepare the Pi"). In short, on the Pi:

    sudo mkdir -p $REMOTE_DIR/{mosquitto/config,mosquitto/data,homeassistant/config,logs}
    sudo chown -R "\$USER" $REMOTE_DIR
    sudo chown -R 1883:1883 $REMOTE_DIR/mosquitto      # the broker's uid
    sudo chown -R 1654:1654 $REMOTE_DIR/logs           # the controller image's non-root uid
EOF
    exit 1
fi

# The controller writes its log files to a bind mount over /app/logs. If the host directory isn't
# writable by the image's non-root user, Serilog's file sink fails *silently*: the container runs,
# `docker logs` looks healthy, and the log files never appear. Catch it here rather than in a month.
if ! ssh_pi "sh -s '$REMOTE_DIR/logs'" <<'REMOTE_CHECK'; then
    dir=$1
    [ -d "$dir" ] || exit 1
    # Writable by uid 1654 means: owned by it, or world-writable. The ssh user's own -w test would
    # answer a different question entirely, since it is not the user that runs inside the container.
    [ "$(stat -c %u "$dir")" = 1654 ] && exit 0
    perms=$(stat -c %a "$dir")
    case ${perms#${perms%?}} in 2|3|6|7) exit 0 ;; esac
    exit 1
REMOTE_CHECK
    cat >&2 <<EOF
error: $REMOTE_DIR/logs is missing, or not writable by the controller's uid (1654).

The container would run and log to stdout, but its log files would go nowhere. On the Pi:

    sudo mkdir -p $REMOTE_DIR/logs
    sudo chown -R 1654:1654 $REMOTE_DIR/logs
EOF
    exit 1
fi

if ! ssh_pi "test -f '$REMOTE_DIR/.env'"; then
    cat >&2 <<EOF
error: $REMOTE_DIR/.env is missing on $PI_HOST.

Secrets are never copied by this script. Create it once, on the Pi:

    scp deploy/.env.example $PI_HOST:$REMOTE_DIR/.env
    ssh $PI_HOST 'chmod 600 $REMOTE_DIR/.env && nano $REMOTE_DIR/.env'
EOF
    exit 1
fi

# tar over ssh rather than rsync: one less thing that has to be installed on a Lite image. This
# directory mirrors $REMOTE_DIR exactly, so the paths need no rewriting on the way over.
say "Copying stack files"
tar -C "$script_dir" -cf - docker-compose.yml mosquitto/config/mosquitto.conf \
    | ssh_pi "tar -C '$REMOTE_DIR' -xf - --no-same-owner"

# Seeded once, never on top of edits made on the Pi. HA rewrites these files itself in normal use.
say "Seeding Home Assistant config (only files that do not exist yet)"
tar -C "$script_dir" -cf - homeassistant/config \
    | ssh_pi "tar -C '$REMOTE_DIR' -xf - --no-same-owner --skip-old-files"

if ! ssh_pi "test -f '$REMOTE_DIR/mosquitto/config/passwd'"; then
    cat >&2 <<EOF

error: the broker has no password file, and it refuses anonymous connections -- nothing would
connect. Create it once, on the Pi (username must match MQTT_USERNAME in .env):

    docker run --rm -v $REMOTE_DIR/mosquitto/config:/mosquitto/config eclipse-mosquitto:2 \\
        mosquitto_passwd -c -b /mosquitto/config/passwd solax '<password>'
    sudo chown 1883:1883 $REMOTE_DIR/mosquitto/config/passwd
EOF
    exit 1
fi

say "Pulling images${IMAGE_TAG:+ (IMAGE_TAG=$IMAGE_TAG)}"
ssh_pi "cd '$REMOTE_DIR' && ${IMAGE_TAG:+IMAGE_TAG='$IMAGE_TAG' }docker compose pull"

say "Starting"
ssh_pi "cd '$REMOTE_DIR' && ${IMAGE_TAG:+IMAGE_TAG='$IMAGE_TAG' }docker compose up -d --remove-orphans"

say "Status"
ssh_pi "cd '$REMOTE_DIR' && docker compose ps"

cat <<EOF

Deployed. Next:

    ssh $PI_HOST 'cd $REMOTE_DIR && docker compose logs -f solax-controller'
    Home Assistant: http://${PI_HOST#*@}:8123
EOF
