#!/bin/sh
# postinst for the gleanvolt .deb (issue #205). dpkg calls it as `postinst configure <old-version>`,
# where <old-version> is empty on a first install.
set -e

[ "$1" = "configure" ] || exit 0

old_version="$2"

# The service user. System account, no login, home is the data directory -- ASP.NET keeps its
# data-protection keys under $HOME, and they must survive a restart or every login cookie is void.
if ! getent group gleanvolt >/dev/null; then
    groupadd --system gleanvolt
fi
if ! getent passwd gleanvolt >/dev/null; then
    useradd --system --gid gleanvolt --home-dir /var/lib/gleanvolt --no-create-home \
        --shell /usr/sbin/nologin --comment "Gleanvolt controller" gleanvolt
fi

# The two directories the service writes to. Created here rather than shipped in the package, so that
# `apt remove` leaves the session history alone and only `apt purge` takes it.
install -d -o gleanvolt -g gleanvolt -m 0750 /var/lib/gleanvolt
install -d -o gleanvolt -g gleanvolt -m 0750 /var/log/gleanvolt

# Readable by the service, not by every local user: it is where the Solcast key and the web password
# hash go. Set on every configure, because dpkg ships the file root:root and resets an untouched one.
if [ -f /etc/gleanvolt/gleanvolt.env ]; then
    chown root:gleanvolt /etc/gleanvolt/gleanvolt.env
    chmod 0640 /etc/gleanvolt/gleanvolt.env
fi

if [ ! -d /run/systemd/system ]; then
    # Unpacked into a chroot or a container with no systemd running. Enable it for when it does run,
    # and say that nothing was started rather than leaving it to be discovered.
    systemctl enable gleanvolt.service >/dev/null 2>&1 || true
    echo "Gleanvolt is installed but systemd is not running here, so it has not been started."
    exit 0
fi

systemctl daemon-reload

if [ -z "$old_version" ] || [ -e /var/lib/gleanvolt/.removed ]; then
    # First install -- or the first since `apt remove`, which disabled the unit and left the data and
    # this marker behind (dpkg still passes the old version then): enabled and started with the
    # configuration there is, no questions asked.
    rm -f /var/lib/gleanvolt/.removed
    systemctl enable gleanvolt.service
    start=yes
elif [ -e /run/gleanvolt.restart-after-upgrade ]; then
    # An upgrade of a service that was running. prerm stopped it before the files were replaced and
    # left this marker; one that was deliberately stopped left none and stays stopped.
    rm -f /run/gleanvolt.restart-after-upgrade
    start=yes
fi

# A service that will not start is the operator's to fix, not a broken package: failing here would
# leave dpkg half-configured and every later apt command complaining about it.
if [ "${start:-}" = yes ] && ! systemctl start gleanvolt.service; then
    echo "WARNING: gleanvolt did not start. See: journalctl -u gleanvolt -e" >&2
fi

port=$(sed -n 's/^[[:space:]]*Web__Port=\([0-9][0-9]*\).*/\1/p' /etc/gleanvolt/gleanvolt.env 2>/dev/null | tail -n 1)
address=$(hostname -I 2>/dev/null | awk '{print $1}')

cat <<EOF

Gleanvolt is installed.

  Web UI     http://${address:-<this machine>}:${port:-8090}   (plain HTTP, no login until you set one)
  Settings   /etc/gleanvolt/gleanvolt.env   -- set your inverter and charger addresses first
  Data       /var/lib/gleanvolt
  Logs       journalctl -u gleanvolt -e   and   /var/log/gleanvolt
  Service    sudo systemctl status|restart|stop gleanvolt

EOF

exit 0
