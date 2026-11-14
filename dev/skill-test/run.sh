#!/bin/sh
# Start a fresh skill-test machine and open a shell in it as "tester" (see README.md).
#
#   dev/skill-test/run.sh                 Debian 12
#   BASE=ubuntu:24.04 dev/skill-test/run.sh
#
# Every run starts from a clean machine: the previous container is removed first.
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
  ${GEMINI_API_KEY:+-e GEMINI_API_KEY} \
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
