#!/bin/sh
# Install or upgrade Gleanvolt on Debian, Ubuntu or Raspberry Pi OS (issue #205).
#
#   curl -fsSL https://github.com/mpospisil/gleanvolt/releases/latest/download/install.sh | sudo sh
#
# Downloads the .deb for this machine's architecture from a GitHub release, checks it against the
# release's SHA256SUMS, and installs it with apt. Running it again is the upgrade: settings in
# /etc/gleanvolt/gleanvolt.env and the data in /var/lib/gleanvolt are kept.
#
# Optional environment:
#
#   GLEANVOLT_VERSION=1.0.7   install that release instead of the latest -- to pin, or to roll back
#                             (a downgrade is allowed)
#   GLEANVOLT_REPO=owner/repo take releases from another repository (default mpospisil/gleanvolt)
#   GLEANVOLT_BASE_URL=url    take SHA256SUMS and the .deb from this URL instead of GitHub, e.g. a
#                             local mirror; overrides the two above
#
# With sudo, pass them after it: `... | sudo GLEANVOLT_VERSION=1.0.7 sh`.

# Everything is inside main, called on the last line: a download cut off half way through defines a
# function and runs nothing, rather than running half of an installer as root.
main() {
    set -eu

    say "Gleanvolt installer"

    # --- what this machine is --------------------------------------------------------------------
    if [ "$(id -u)" -ne 0 ]; then
        fail "run this as root, e.g. with sudo:
  curl -fsSL https://github.com/mpospisil/gleanvolt/releases/latest/download/install.sh | sudo sh"
    fi

    if ! command -v apt-get >/dev/null 2>&1 || ! command -v dpkg >/dev/null 2>&1; then
        fail "this installer needs apt and dpkg (Debian, Ubuntu, Raspberry Pi OS).
On another distribution, download the self-contained linux zip from the release page instead:
  https://github.com/${GLEANVOLT_REPO:-mpospisil/gleanvolt}/releases"
    fi

    if [ ! -d /run/systemd/system ]; then
        fail "systemd is not running on this machine, and Gleanvolt is installed as a systemd service.
In a container, use the Docker image instead: ghcr.io/mpospisil/gleanvolt"
    fi

    arch=$(dpkg --print-architecture)
    case "$arch" in
        amd64 | arm64) ;;
        armhf)
            fail "this is a 32-bit ARM system (armhf), and Gleanvolt is built for 64-bit only.
On a Raspberry Pi 3B or newer, reinstall with Raspberry Pi OS (64-bit) from Raspberry Pi Imager."
            ;;
        *)
            fail "no Gleanvolt package for the '$arch' architecture; amd64 and arm64 are supported."
            ;;
    esac
    say "Architecture: $arch"

    # --- where the release is --------------------------------------------------------------------
    repo=${GLEANVOLT_REPO:-mpospisil/gleanvolt}
    version=${GLEANVOLT_VERSION:-}
    version=${version#v}
    if [ -n "${GLEANVOLT_BASE_URL:-}" ]; then
        base=${GLEANVOLT_BASE_URL%/}
    elif [ -n "$version" ]; then
        base="https://github.com/$repo/releases/download/v$version"
    else
        base="https://github.com/$repo/releases/latest/download"
    fi

    if command -v curl >/dev/null 2>&1; then
        fetch() { curl -fsSL --retry 3 -o "$2" "$1"; }
    elif command -v wget >/dev/null 2>&1; then
        fetch() { wget -q --tries=3 -O "$2" "$1"; }
    else
        say "Installing curl"
        apt-get update -qq
        apt-get install -y -qq curl ca-certificates >/dev/null
        fetch() { curl -fsSL --retry 3 -o "$2" "$1"; }
    fi

    # Readable by apt's unprivileged _apt user, which otherwise warns that the download was
    # "performed unsandboxed as root".
    work=$(mktemp -d)
    trap 'rm -rf "$work"' EXIT
    chmod 0755 "$work"

    # --- which package, and is it intact --------------------------------------------------------
    # SHA256SUMS names every file in the release, so it also says what the .deb is called -- and for
    # "latest", which version that is -- without asking the GitHub API.
    say "Reading $base/SHA256SUMS"
    fetch "$base/SHA256SUMS" "$work/SHA256SUMS" \
        || fail "could not download SHA256SUMS from $base${version:+ -- is $version a released version?}"

    line=$(grep -E "[[:space:]]\*?gleanvolt_[^[:space:]]+_${arch}\.deb\$" "$work/SHA256SUMS" || true)
    if [ -z "$line" ] || [ "$(printf '%s\n' "$line" | wc -l)" -ne 1 ]; then
        fail "the release at $base has no single .deb for $arch."
    fi
    package=$(printf '%s\n' "$line" | awk '{print $2}' | sed 's/^\*//')
    say "Package: $package"

    fetch "$base/$package" "$work/$package" || fail "could not download $base/$package"
    chmod 0644 "$work/$package"

    say "Verifying the checksum"
    (cd "$work" && printf '%s\n' "$line" | sha256sum -c --quiet -) \
        || fail "$package does not match its checksum in SHA256SUMS. Nothing was installed."

    # --- install ----------------------------------------------------------------------------------
    if installed=$(dpkg-query -W -f='${Version}' gleanvolt 2>/dev/null) && [ -n "$installed" ]; then
        say "Upgrading from $installed; your settings and data are kept"
    fi

    # confold: an edited /etc/gleanvolt/gleanvolt.env is always kept, and nothing stops to ask.
    say "Installing"
    apt-get update -qq || say "apt-get update failed; continuing with the package lists there are"
    DEBIAN_FRONTEND=noninteractive apt-get install -y --allow-downgrades \
        -o Dpkg::Options::=--force-confdef \
        -o Dpkg::Options::=--force-confold \
        "$work/$package"

    say "Done. To remove it later: sudo apt remove gleanvolt (keeps data) or sudo apt purge gleanvolt (deletes everything)."
}

say() {
    printf '==> %s\n' "$*"
}

fail() {
    printf '\nGleanvolt was not installed: %s\n' "$*" >&2
    exit 1
}

main "$@"
