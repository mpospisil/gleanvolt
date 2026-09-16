#!/bin/sh
# prerm for the gleanvolt .deb (issue #205). dpkg calls it as `prerm remove` or `prerm upgrade <new>`.
set -e

[ -d /run/systemd/system ] || exit 0

case "$1" in
    upgrade)
        # Stop before dpkg replaces the files under a running process, and remember whether it was
        # running: the new postinst starts it again only then, so a service somebody stopped on purpose
        # is still stopped after the upgrade. The stop is a SIGTERM, so the charger is released.
        if systemctl is-active --quiet gleanvolt.service; then
            touch /run/gleanvolt.restart-after-upgrade
        fi
        systemctl stop gleanvolt.service || true
        ;;
    remove | deconfigure)
        systemctl disable --now gleanvolt.service || true
        # So a later reinstall enables it again: dpkg calls that postinst with the old version, which
        # would otherwise read as an upgrade of a service somebody had switched off.
        if [ -d /var/lib/gleanvolt ]; then
            touch /var/lib/gleanvolt/.removed
        fi
        ;;
esac

exit 0
