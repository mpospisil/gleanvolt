#!/usr/bin/env python3
"""Assert a published manifest list covers every platform, Windows included, with its build number.

Reads `docker buildx imagetools inspect --raw` output on stdin and exits non-zero if anything is
missing. It exists because both failures it checks for are invisible in a green workflow run: the
first release published a Windows entry with no `os.version` at all, and nothing noticed until the
images were inspected by hand afterwards.

    docker buildx imagetools inspect "$image:$version" --raw | .github/scripts/verify-manifest.py
"""

import json
import sys

REQUIRED = {"linux/amd64", "linux/arm64", "windows/amd64"}


def main() -> int:
    index = json.load(sys.stdin)

    platforms = {}
    for manifest in index.get("manifests", []):
        platform = manifest.get("platform", {})
        # Attestation manifests ride along in the index as unknown/unknown; they are not platforms.
        if platform.get("architecture") == "unknown":
            continue
        key = "{}/{}".format(platform.get("os", "?"), platform.get("architecture", "?"))
        platforms[key] = platform

    missing = REQUIRED - set(platforms)
    if missing:
        print(
            "Manifest list is missing {}; it has {}.".format(sorted(missing), sorted(platforms)),
            file=sys.stderr,
        )
        return 1

    # os.version is how a Windows host picks an image it can run. `docker buildx imagetools create`
    # drops it when composing an index from tags, which is why the manifest job annotates it back.
    windows = platforms["windows/amd64"]
    if not windows.get("os.version"):
        print(
            "The windows/amd64 entry has no os.version, so Windows hosts cannot match it "
            "reliably and a second Windows version could not be told apart from it.",
            file=sys.stderr,
        )
        return 1

    print(
        "OK: {} present; windows os.version={}".format(
            sorted(platforms), windows["os.version"]
        )
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
