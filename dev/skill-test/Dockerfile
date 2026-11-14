# A clean Debian machine to try the agent skills in skills/ (#390) against, with systemd running, so
# the gleanvolt-install skill takes its real path (A, the .deb as a systemd service) as on a Pi.
#
# Nothing of Gleanvolt is in it. Node.js is, because Gemini CLI needs Node 20+ and Debian 12's own is
# 18; it lives in /usr/local and has nothing to do with Gleanvolt's prerequisites.
#
# Build and run with run.sh next to this file: systemd needs --privileged and the host network gives
# the container the LAN. See README.md for what that shares with the host.

ARG BASE=debian:12
FROM ${BASE}

ARG NODE_VERSION=22.20.0
ARG TARGETARCH

ENV container=docker \
    DEBIAN_FRONTEND=noninteractive \
    LANG=C.UTF-8

# What a fresh Debian or Raspberry Pi OS install has: systemd, sudo, curl, ss, ip, ps, an editor.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      systemd systemd-sysv dbus sudo ca-certificates curl xz-utils \
      iproute2 iputils-ping procps less nano git \
 && rm -rf /var/lib/apt/lists/*

# Units that make no sense in a container, and some that would reach the host through --privileged
# and the host network: the clock (timesyncd), devices (udev), the network and DNS, and the consoles.
RUN systemctl mask \
      systemd-timesyncd.service systemd-udevd.service systemd-udevd-control.socket \
      systemd-udevd-kernel.socket systemd-networkd.service systemd-resolved.service \
      getty@tty1.service serial-getty@.service console-getty.service \
      systemd-logind.service systemd-remount-fs.service sys-kernel-config.mount \
      sys-kernel-debug.mount sys-kernel-tracing.mount dev-hugepages.mount \
 && systemctl set-default multi-user.target

# Node.js from nodejs.org, checked against its published checksum.
RUN set -eu; \
    case "${TARGETARCH:-amd64}" in amd64) arch=x64 ;; arm64) arch=arm64 ;; *) echo "unsupported $TARGETARCH"; exit 1 ;; esac; \
    file="node-v${NODE_VERSION}-linux-${arch}.tar.xz"; \
    cd /tmp; \
    curl -fsSLO "https://nodejs.org/dist/v${NODE_VERSION}/${file}"; \
    curl -fsSLO "https://nodejs.org/dist/v${NODE_VERSION}/SHASUMS256.txt"; \
    grep " ${file}\$" SHASUMS256.txt | sha256sum -c -; \
    tar -xJf "$file" -C /usr/local --strip-components=1 --no-same-owner; \
    rm -f "$file" SHASUMS256.txt; \
    node --version

# A normal user with sudo, as on a Pi set up with Raspberry Pi Imager. The password is "tester"; sudo
# asks for it, as it would on a real machine.
RUN useradd -m -s /bin/bash -G sudo tester \
 && echo 'tester:tester' | chpasswd \
 && mkdir -p /home/tester/.npm-global /home/tester/.gemini \
 && echo 'export NPM_CONFIG_PREFIX=$HOME/.npm-global PATH=$HOME/.npm-global/bin:$PATH' >> /home/tester/.bashrc \
 && chown -R tester:tester /home/tester/.npm-global /home/tester/.gemini

STOPSIGNAL SIGRTMIN+3
CMD ["/sbin/init"]
