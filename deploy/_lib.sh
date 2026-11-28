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

# The plugins released with Gleanvolt (#426): each is the compose service gleanvolt-plugin-<name> under
# the profile <name>, with its data in plugins/<name>. GLEANVOLT_PLUGINS in the Pi's .env picks which run. The
# charger plugins (#444) come from gleanvolt-chargers; at most one runs, and since 1.4 (#445) it is the only way the
# controller reaches the charger. The car plugins (#476) come from gleanvolt-cars; at most one runs, read with "A car plugin"
# chosen on the Car page.
CHARGER_PLUGINS="solax huawei goe shelly"
CAR_PLUGINS="volkswagen skoda vwgroup"
KNOWN_PLUGINS="openweather $CHARGER_PLUGINS $CAR_PLUGINS"

# env_value <name>: the value the Pi's .env gives <name> on its last line for it, without quotes or a
# trailing comment. Empty when it is not set.
env_value() {
    ssh_pi "grep -E '^$1=' '$REMOTE_DIR/.env' 2>/dev/null | tail -n 1" \
        | cut -d= -f2- \
        | sed -e 's/[[:space:]]#.*$//' -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//' -e 's/^["'\'']//' -e 's/["'\'']$//' \
        || true
}

# built_in_car_plugin: reads an overrides file (data/pv-system.json, nested as appsettings.json is) on stdin and
# prints the car plugin replacing the built-in feed it switches on (#477), or nothing. The Website, Skoda and
# DataAct sections hold no nested objects, so the section a line is in is the last one opened.
built_in_car_plugin() {
    awk '
        /"Website"[[:space:]]*:[[:space:]]*\{/ { section = "volkswagen"; next }
        /"Skoda"[[:space:]]*:[[:space:]]*\{/   { section = "skoda"; next }
        /"DataAct"[[:space:]]*:[[:space:]]*\{/ { section = "vwgroup"; next }
        /\}/ { section = "" }
        section != "" && /"Enabled"[[:space:]]*:[[:space:]]*"?[Tt]rue"?/ { found[section] = 1 }
        END {
            if ("volkswagen" in found) print "volkswagen"
            else if ("skoda" in found) print "skoda"
            else if ("vwgroup" in found) print "vwgroup"
        }'
}

# ensure_owned_dir <subdir> <mode> <consequence>
#
# Makes sure $REMOTE_DIR/<subdir> exists and uid 1654, the images' non-root user, can write to it,
# setting <mode> (e.g. 700) when it has to be created or fixed. The ssh user cannot chown to another
# uid -- but the Docker daemon is root, and Docker is already a hard requirement here, so a throwaway
# container does what sudo would. Idempotent: it changes something only when the check says so.
ensure_owned_dir() {
    local subdir=$1 mode=$2 consequence=$3

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
        return 0
    fi

    local chmod_step="" chmod_hint=""
    if [ -n "$mode" ]; then
        chmod_step=" && chmod $mode /target/$subdir"
        chmod_hint="
    sudo chmod $mode $REMOTE_DIR/$subdir"
    fi

    say "Preparing $REMOTE_DIR/$subdir (owned by uid 1654)"
    if ! ssh_pi "docker run --rm -v '$REMOTE_DIR:/target' alpine \
            sh -c 'mkdir -p /target/$subdir && chown -R 1654:1654 /target/$subdir$chmod_step'"; then
        cat >&2 <<EOF
error: could not prepare $REMOTE_DIR/$subdir, and it is needed: $consequence.

Create it by hand on the Pi:

    sudo mkdir -p $REMOTE_DIR/$subdir
    sudo chown -R 1654:1654 $REMOTE_DIR/$subdir$chmod_hint
EOF
        exit 1
    fi
}

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
    ensure_owned_dir logs "" "its log files would go nowhere"
    ensure_owned_dir data "" "no charging session would ever be recorded"

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

    # docker-compose.web.yml is gone. An .env from an old release may still merge it through
    # COMPOSE_FILE, and then every docker compose command would fail with "no such file".
    if ssh_pi "grep -q '^COMPOSE_FILE=.*docker-compose\.web\.yml' '$REMOTE_DIR/.env'"; then
        cat >&2 <<EOF
error: $REMOTE_DIR/.env on $PI_HOST still has a COMPOSE_FILE line naming docker-compose.web.yml.

That file no longer exists. Delete the line, then deploy again:

    ssh $PI_HOST "sed -i '/^COMPOSE_FILE=/d' $REMOTE_DIR/.env"
EOF
        exit 1
    fi

    # The shipped plugins to run (#426), from .env, on top of the script's own profiles: under either
    # script, since a plugin belongs to the controller's side of the stack, not Home Assistant's.
    local plugin enabled=""
    for plugin in $(env_value GLEANVOLT_PLUGINS | tr ',' ' '); do
        case " $KNOWN_PLUGINS " in
            *" $plugin "*) enabled="$enabled $plugin" ;;
            *)
                cat >&2 <<EOF
error: GLEANVOLT_PLUGINS in $REMOTE_DIR/.env names "$plugin", which is not a shipped plugin.

The shipped plugins are: $KNOWN_PLUGINS
EOF
                exit 1
                ;;
        esac
    done

    # One charger plugin at most: one charger is supported, and two plugins would both want it.
    local charger_plugins=""
    for plugin in $enabled; do
        case " $CHARGER_PLUGINS " in
            *" $plugin "*) charger_plugins="$charger_plugins $plugin" ;;
        esac
    done
    if [ "$(echo $charger_plugins | wc -w)" -gt 1 ]; then
        cat >&2 <<EOF
error: GLEANVOLT_PLUGINS in $REMOTE_DIR/.env names more than one charger plugin:$charger_plugins

One charger is supported. Keep the one for your charger and deploy again.
EOF
        exit 1
    fi

    # One car plugin at most, for the same reason: one car is supported, and Gleanvolt reads it from one plugin.
    local car_plugins=""
    for plugin in $enabled; do
        case " $CAR_PLUGINS " in
            *" $plugin "*) car_plugins="$car_plugins $plugin" ;;
        esac
    done
    if [ "$(echo $car_plugins | wc -w)" -gt 1 ]; then
        cat >&2 <<EOF
error: GLEANVOLT_PLUGINS in $REMOTE_DIR/.env names more than one car plugin:$car_plugins

One car is supported. Keep the one for your car and deploy again.
EOF
        exit 1
    fi

    # An .env from before 1.4 (#445): the charger ran on a built-in driver, which is gone. Its address is still
    # EV_CHARGER_HOST and its brand PV_CHARGER_DRIVER, so run the plugin that replaced that driver, and say so.
    if [ -z "$charger_plugins" ] && [ -n "$(env_value EV_CHARGER_HOST)" ]; then
        local replacing
        case "$(env_value PV_CHARGER_DRIVER | tr '[:upper:]' '[:lower:]')" in
            ""|solax) replacing=solax ;;
            huawei-7ks|huawei-22kt) replacing=huawei ;;
            goe) replacing=goe ;;
            *) replacing="" ;;
        esac
        if [ -n "$replacing" ]; then
            say "Running the $replacing charger plugin: Gleanvolt 1.4 has no built-in charger drivers"
            cat <<EOF
EV_CHARGER_HOST is set in $REMOTE_DIR/.env, and GLEANVOLT_PLUGINS names no charger plugin. The $replacing plugin
replaced the built-in driver this .env ran on. Add it to GLEANVOLT_PLUGINS to say so yourself:

    GLEANVOLT_PLUGINS=$(echo $enabled $replacing | tr ' ' ',')
EOF
            enabled="$enabled $replacing"
        fi
    fi

    # An .env or a Car page from before the built-in car feeds were removed (#477): run the plugin that replaced the
    # one switched on, and say so. Read from .env, then from the overrides file the Car page writes (owned by the
    # image's user, so read through a throwaway container, as ensure_owned_dir does). Nothing is removed.
    if [ -z "$car_plugins" ]; then
        local car_replacing=""
        if [ "$(env_value VW_WEBSITE_ENABLED)" = true ]; then car_replacing=volkswagen
        elif [ "$(env_value SKODA_ENABLED)" = true ]; then car_replacing=skoda
        elif [ "$(env_value VW_ENABLED)" = true ]; then car_replacing=vwgroup
        else
            car_replacing=$(ssh_pi "test -f '$REMOTE_DIR/data/pv-system.json' && docker run --rm -v '$REMOTE_DIR/data:/data:ro' alpine \
                    cat /data/pv-system.json" 2>/dev/null | built_in_car_plugin || true)
        fi

        if [ -n "$car_replacing" ]; then
            if ssh_pi "docker container inspect gleanvolt-$car_replacing >/dev/null 2>&1"; then
                # A plugin started by hand under its own name (the Pi's gleanvolt-volkswagen) holds the port.
                say "Not starting the $car_replacing car plugin: a container named gleanvolt-$car_replacing runs already"
                cat <<EOF
Gleanvolt no longer reads the car itself (#477), and the $car_replacing plugin replaces the built-in feed this
installation used. A container named gleanvolt-$car_replacing, started outside compose, already uses its port. Stop
it, keep its data (copy its volume into $REMOTE_DIR/plugins/$car_replacing), then add the plugin to GLEANVOLT_PLUGINS:

    GLEANVOLT_PLUGINS=$(echo $enabled $car_replacing | tr ' ' ',')
EOF
            else
                say "Running the $car_replacing car plugin: Gleanvolt no longer reads the car itself"
                cat <<EOF
A built-in car feed is switched on, and GLEANVOLT_PLUGINS names no car plugin. The $car_replacing plugin replaced
it: accept it under Settings -> Plugins, then enter the password or API key on its page. Add it to
GLEANVOLT_PLUGINS to say so yourself:

    GLEANVOLT_PLUGINS=$(echo $enabled $car_replacing | tr ' ' ',')
EOF
                enabled="$enabled $car_replacing"
            fi
        fi
    fi

    if [ -n "$enabled" ]; then
        # Without the token a plugin asks to be paired (#464) and waits, quietly, until the owner accepts it;
        # the token it is given is kept in its plugins/ directory. GLEANVOLT_PROVIDER_TOKEN still wins when set.
        if [ -z "$(env_value GLEANVOLT_PROVIDER_TOKEN)" ]; then
            cat <<EOF
GLEANVOLT_PROVIDER_TOKEN is not set, so each new plugin asks to connect: accept it in the web UI, under
Settings -> Plugins. A plugin accepted before keeps its token.
EOF
        fi

        for plugin in $enabled; do
            # 700, as the image makes it: the directory holds the plugin's secrets, such as an API key.
            ensure_owned_dir "plugins/$plugin" 700 "the $plugin plugin could not keep its settings"
            profiles="${profiles:+$profiles,}$plugin"
        done
    fi

    # tar over ssh rather than rsync: one less thing that has to be installed on a Lite image. This
    # directory mirrors $REMOTE_DIR exactly, so the paths need no rewriting on the way over.
    # mosquitto.conf travels unconditionally too, even for a controller-only deploy: which files
    # git ships never depends on which script you ran, only which containers actually start.
    say "Copying stack files"
    tar -C "$script_dir" -cf - docker-compose.yml mosquitto/config/mosquitto.conf \
        | ssh_pi "tar -C '$REMOTE_DIR' -xf - --no-same-owner"
    ssh_pi "rm -f '$REMOTE_DIR/docker-compose.web.yml'"

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

    # A plugin taken off GLEANVOLT_PLUGINS is stopped and removed: `up` leaves a service whose profile
    # is not active alone, and --remove-orphans only removes services the file no longer has. Its data
    # directory stays, so putting it back on the list brings it back as it was.
    for plugin in $KNOWN_PLUGINS; do
        case " $enabled " in
            *" $plugin "*) ;;
            *)
                if ssh_pi "docker container inspect gleanvolt-plugin-$plugin >/dev/null 2>&1"; then
                    say "Removing the $plugin plugin (not in GLEANVOLT_PLUGINS)"
                    ssh_pi "docker rm -f gleanvolt-plugin-$plugin >/dev/null"
                fi
                ;;
        esac
    done

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
    Web UI: http://gleanvolt.local:${web_port:-8090}  (or http://${PI_HOST#*@}:${web_port:-8090})"
    fi

    for plugin in $enabled; do
        next="$next
    The $plugin plugin: its page is on Settings in the web UI; logs: docker compose logs -f gleanvolt-plugin-$plugin"
    done

    cat <<EOF

Deployed. Next:

$next
EOF
}
