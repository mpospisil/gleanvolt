# shellcheck shell=bash
#
# Shared logic for deploy/deploy.sh (full stack) and deploy/deploy-controller-only.sh (controller,
# optionally with its own web UI, no Home Assistant or broker). Not meant to be run directly -- both
# scripts source this file and then call deploy_stack with the profiles they want.
#
# Copies the committed stack files to the Pi, then pulls the image from GHCR and restarts. It never
# copies secrets: /opt/gleanvolt/.env is created once, by hand, on the Pi.

set -euo pipefail

PI_HOST="${PI_HOST:-martin@192.168.2.7}"
REMOTE_DIR="${REMOTE_DIR:-/opt/gleanvolt}"
SSH_OPTS="${SSH_OPTS:-}"

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# shellcheck disable=SC2086  # SSH_OPTS is intentionally word-split
ssh_pi() { ssh $SSH_OPTS "$PI_HOST" "$@"; }

say() { printf '\n\033[1m==> %s\033[0m\n' "$*"; }

# deploy_stack <profiles-csv>
#
# profiles-csv is passed to Compose as COMPOSE_PROFILES, e.g. "mosquitto,homeassistant" or "" for the
# controller alone. It is authoritative over whatever COMPOSE_PROFILES happens to already be set to
# in .env on the Pi -- the whole point of having two entry-point scripts is that which stack you get
# is a decision made by which one you run, not a value that can go stale in a file.
deploy_stack() {
    local profiles="$1"

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

    # The controller writes to two bind mounts, and both fail badly if the host directory isn't
    # writable by the image's non-root user: Serilog's file sink fails *silently* (the container
    # runs, `docker logs` looks healthy, and the log files never appear), and the session store logs
    # an error and then records nothing for the whole run. Catch both here rather than in a month.
    #
    # SQLite puts its -wal and -shm next to the database, so for data/ it really is the directory
    # that has to be writable, not just the file.
    for subdir in logs data; do
        case $subdir in
            logs) consequence="its log files would go nowhere" ;;
            data) consequence="no charging session would ever be recorded" ;;
        esac

        if ssh_pi "sh -s '$REMOTE_DIR/$subdir'" <<'REMOTE_CHECK'; then
    dir=$1
    [ -d "$dir" ] || exit 1
    # Writable by uid 1654 means: owned by it, or world-writable. The ssh user's own -w test would
    # answer a different question entirely, since it is not the user that runs inside the container.
    [ "$(stat -c %u "$dir")" = 1654 ] && exit 0
    perms=$(stat -c %a "$dir")
    case ${perms#${perms%?}} in 2|3|6|7) exit 0 ;; esac
    exit 1
REMOTE_CHECK
            continue
        fi

        # Create it, and hand it to uid 1654. The ssh user cannot chown to another uid -- but the
        # Docker daemon is root, and Docker is already a hard requirement here, so a throwaway
        # container does what sudo would. Idempotent: this runs only when the check above said
        # something was wrong.
        say "Preparing $REMOTE_DIR/$subdir (owned by uid 1654)"
        if ! ssh_pi "docker run --rm -v '$REMOTE_DIR:/target' alpine \
                sh -c 'mkdir -p /target/$subdir && chown -R 1654:1654 /target/$subdir'"; then
            cat >&2 <<EOF
error: could not prepare $REMOTE_DIR/$subdir, and the controller needs it: $consequence.

Create it by hand on the Pi:

    sudo mkdir -p $REMOTE_DIR/$subdir
    sudo chown -R 1654:1654 $REMOTE_DIR/$subdir
EOF
            exit 1
        fi
    done

    case ",$profiles," in
        *,mosquitto,*)
            # The deploy writes mosquitto.conf into this directory, so the ssh user has to own it.
            # The broker never writes there -- its compose mount is read-only -- so uid 1883 only
            # needs to read. Only mosquitto/data has to belong to the broker. A `chown -R 1883
            # mosquitto/` (which earlier setup instructions told people to do) locks the deploy out
            # of its own config directory.
            if ! ssh_pi "test -w '$REMOTE_DIR/mosquitto/config'"; then
                say "Taking ownership of $REMOTE_DIR/mosquitto/config"
                # Not recursive, deliberately: passwd must stay owned by 1883, which is the only uid
                # allowed to read it. Chowning it to the ssh user would leave the broker unable to
                # authenticate anyone.
                if ! ssh_pi "uid=\$(id -u); gid=\$(id -g); docker run --rm -v '$REMOTE_DIR/mosquitto:/target' alpine \
                        sh -c \"mkdir -p /target/config && chown \$uid:\$gid /target/config && chmod 755 /target/config\""; then
                    cat >&2 <<EOF
error: $REMOTE_DIR/mosquitto/config is not writable by $PI_HOST's login user, and could not be fixed.

The deploy writes mosquitto.conf there; the broker only reads it. On the Pi:

    sudo chown "\$USER" $REMOTE_DIR/mosquitto/config    # the directory only, not its contents
    sudo chmod 755 $REMOTE_DIR/mosquitto/config
    sudo chown 1883:1883 $REMOTE_DIR/mosquitto/config/passwd
EOF
                    exit 1
                fi
            fi
            ;;
    esac

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
    # docker-compose.web.yml and mosquitto.conf travel unconditionally too, even for a
    # controller-only deploy: which files git ships never depends on which script you ran, only
    # which containers actually start.
    say "Copying stack files"
    tar -C "$script_dir" -cf - docker-compose.yml docker-compose.web.yml mosquitto/config/mosquitto.conf \
        | ssh_pi "tar -C '$REMOTE_DIR' -xf - --no-same-owner"

    # Seeded once, never on top of edits made on the Pi. HA rewrites these files itself in normal
    # use. Harmless to seed even when Home Assistant is never deployed -- it's a handful of files on
    # disk, and doing it unconditionally means switching to deploy.sh later needs no extra step.
    say "Seeding Home Assistant config (only files that do not exist yet)"
    tar -C "$script_dir" -cf - homeassistant/config \
        | ssh_pi "tar -C '$REMOTE_DIR' -xf - --no-same-owner --skip-old-files"

    case ",$profiles," in
        *,mosquitto,*)
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
            ;;
    esac

    say "Pulling images${IMAGE_TAG:+ (IMAGE_TAG=$IMAGE_TAG)}"
    ssh_pi "cd '$REMOTE_DIR' && COMPOSE_PROFILES='$profiles' ${IMAGE_TAG:+IMAGE_TAG='$IMAGE_TAG' }docker compose pull"

    say "Starting"
    ssh_pi "cd '$REMOTE_DIR' && COMPOSE_PROFILES='$profiles' ${IMAGE_TAG:+IMAGE_TAG='$IMAGE_TAG' }docker compose up -d --remove-orphans"

    say "Status"
    ssh_pi "cd '$REMOTE_DIR' && COMPOSE_PROFILES='$profiles' docker compose ps"

    next="    ssh $PI_HOST 'cd $REMOTE_DIR && docker compose logs -f gleanvolt-controller'"
    case ",$profiles," in
        *,homeassistant,*)
            next="$next
    Home Assistant: http://${PI_HOST#*@}:8123"
            ;;
    esac
    # The UI is on unless .env explicitly turns it off, so the test is for the opt-out, not the
    # opt-in: a fresh .env says nothing about the web UI at all and must still get this line.
    if ! ssh_pi "grep -qE '^WEB_ENABLED=false' '$REMOTE_DIR/.env' 2>/dev/null"; then
        # `|| true` because no WEB_PORT line is the normal case -- it is commented out in
        # .env.example, so the default port applies and grep exits 1. Under `set -e` that status
        # would propagate out of the assignment and kill the script *after* a completely successful
        # deploy, reporting failure and swallowing the summary below. The `if !` above guards the
        # same hazard on the line before; this one needs it just as much.
        web_port=$(ssh_pi "grep -E '^WEB_PORT=' '$REMOTE_DIR/.env' 2>/dev/null" | cut -d= -f2- || true)
        next="$next
    Web UI: http://${PI_HOST#*@}:${web_port:-8090}"
    fi

    cat <<EOF

Deployed. Next:

$next
EOF
}
