#!/usr/bin/env bash
#
# Deploy just the controller -- and its own web UI, if WEB_ENABLED=true in .env -- to the Raspberry
# Pi: no Home Assistant, no MQTT broker (issue #51). For the full stack, use deploy/deploy.sh
# instead.
#
#   ./deploy/deploy-controller-only.sh                               # deploy IMAGE_TAG (default: latest)
#   IMAGE_TAG=1.0.0 ./deploy/deploy-controller-only.sh               # a released version -- no "v"
#   IMAGE_TAG=sha-abc1234 ./deploy/deploy-controller-only.sh         # deploy/roll back to a build
#   PI_HOST=martin@192.168.2.7 ./deploy/deploy-controller-only.sh    # non-default host or user
#
# See deploy.sh's header for what IMAGE_TAG and PI_HOST mean; both work identically here.
#
# Neither MQTT_USERNAME/MQTT_PASSWORD nor a broker password file are required for this script --
# mosquitto and Home Assistant never start, regardless of what COMPOSE_PROFILES happens to already be
# set to in .env on the Pi from a previous deploy.sh run.
#
# Preparing the Pi itself is documented in deploy/README.md ("Running without Home Assistant") and
# deliberately not automated here, with the same two exceptions deploy.sh makes: the data and logs
# directories the controller writes to, which this script creates and chowns itself.

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=deploy/_lib.sh
source "$script_dir/_lib.sh"

deploy_stack ""
