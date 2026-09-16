#!/bin/sh
# postrm for the gleanvolt .deb (issue #205). dpkg calls it as `postrm remove` and, for
# `apt purge`, again as `postrm purge`.
set -e

case "$1" in
    remove)
        # The data, the logs, the settings and the user all stay: the session history cannot be
        # regenerated, and reinstalling should pick up exactly where this left off.
        ;;
    purge)
        rm -rf /var/lib/gleanvolt /var/log/gleanvolt /etc/gleanvolt
        rm -f /run/gleanvolt.restart-after-upgrade
        if getent passwd gleanvolt >/dev/null; then
            userdel gleanvolt || true
        fi
        if getent group gleanvolt >/dev/null; then
            groupdel gleanvolt || true
        fi
        ;;
esac

if [ -d /run/systemd/system ]; then
    systemctl daemon-reload || true
fi

exit 0
