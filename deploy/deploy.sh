#!/usr/bin/env bash
#
# Deploy the full SolaX stack -- controller, Home Assistant and the MQTT broker -- to the Raspberry
# Pi (issue #26). For the controller alone (optionally with its own web UI, no Home Assistant, no
# broker), use deploy/deploy-controller-only.sh instead.
#
#   ./deploy/deploy.sh                               # deploy whatever IMAGE_TAG pins (default: latest)
#   IMAGE_TAG=1.0.0 ./deploy/deploy.sh               # a released version -- no "v", see below
#   IMAGE_TAG=sha-abc1234 ./deploy/deploy.sh         # deploy/roll back to a specific build
#   PI_HOST=martin@192.168.2.7 ./deploy/deploy.sh    # non-default host or user
#
# IMAGE_TAG names an IMAGE tag, not a git tag: releases are cut as git tag v1.0.0 and published as
# image 1.0.0, because the publish workflow strips the prefix. The image is a multi-platform
# manifest list, so the tag never needs an architecture in it either.
#
# Requires MQTT_USERNAME/MQTT_PASSWORD in .env and a broker password file (see deploy/README.md) --
# this script fails fast if the latter is missing rather than starting a broker nothing can
# authenticate against.
#
# Preparing the Pi itself -- Docker, cgroups, swap, secrets, broker credentials -- is documented in
# deploy/README.md and deliberately not automated here: it needs sudo and it needs decisions. The two
# directories the app writes to are the exception. They are deterministic, a new release can add one
# (data/ did), and getting them wrong is silent, so this script creates them itself.

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=deploy/_lib.sh
source "$script_dir/_lib.sh"

deploy_stack "mosquitto,homeassistant"
