#!/bin/sh
# Start a fresh skill-test machine and open a shell in it as "tester" (see README.md).
#
#   dev/skill-test/run.sh                 Debian 12
#   BASE=ubuntu:24.04 dev/skill-test/run.sh
#
# Every run starts from a clean machine: the previous container is removed first. Only the agent is
# kept: Claude Code's sign-in and settings (~/.claude) in the volume gleanvolt-skill-test-claude, and
# what `npm install -g` put in ~/.npm-global in gleanvolt-skill-test-npm. Neither is anything Gleanvolt
# or its skills look at. `docker volume rm` forgets either.
set -eu

base=${BASE:-debian:12}
name=gleanvolt-skill-test
image=gleanvolt-skill-test:$(echo "$base" | tr ':/' '--')
here=$(cd "$(dirname "$0")" && pwd)

docker build --build-arg BASE="$base" -t "$image" "$here"
docker rm -f "$name" >/dev/null 2>&1 || true

# --privileged and a cgroup namespace of its own: what systemd needs as PID 1.
# --network host: the LAN, mDNS and port 8090 are the host's.
docker run -d --name "$name" --hostname gleanvolt-test --add-host gleanvolt-test:127.0.1.1 \
  --privileged --cgroupns=private \
  --tmpfs /run --tmpfs /run/lock --tmpfs /tmp \
  --network host \
  ${ANTHROPIC_API_KEY:+-e ANTHROPIC_API_KEY} \
  -v gleanvolt-skill-test-claude:/home/tester/.claude \
  -v gleanvolt-skill-test-npm:/home/tester/.npm-global \
  -v "$here/../../skills:/skills:ro" \
  "$image" >/dev/null

# Wait for systemd to be up.
i=0
until docker exec "$name" systemctl is-system-running --wait >/dev/null 2>&1 \
   || [ "$(docker exec "$name" systemctl is-system-running 2>/dev/null)" = degraded ]; do
  i=$((i + 1))
  [ "$i" -lt 30 ] || { echo "systemd did not come up"; docker logs "$name" | tail -20; exit 1; }
  sleep 1
done

echo "Ready. Skills are in /skills (read-only). Remove the machine with: docker rm -f $name"
exec docker exec -it -u tester -w /home/tester "$name" bash -l
