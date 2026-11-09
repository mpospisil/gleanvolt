#!/usr/bin/env bash
# Copies shared Markdown fragments into the pages that show them.
#
# GitHub's Markdown has no include, so a page marks where a fragment goes:
#
#   <!-- include: docs/SUPPORTED_INVERTERS.md -->
#   ...whatever this script wrote last time...
#   <!-- end include -->
#
# and this script replaces everything between the two markers with the fragment's current text. The
# path is relative to the repository root. The fragment's own "# title" line is left out, so a fragment
# also reads as a page of its own. Edit the fragment, never the copy: a test fails while
# a copy in the repository differs from its fragment.
#
# Usage: scripts/sync-doc-includes.sh [page.md ...]
#   With no arguments it updates README.md and docs/SUPPORTED_PV_SYSTEMS.md. Pass the wiki's pages to update them too, e.g.
#   scripts/sync-doc-includes.sh README.md ../solax-controller.wiki/Home.md
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [ "$#" -eq 0 ]; then
  set -- "$root/README.md" "$root/docs/SUPPORTED_PV_SYSTEMS.md"
fi

for page in "$@"; do
  tmp="$(mktemp)"
  awk -v root="$root" -v page="$page" '
    function fail(message) { print page ": " message > "/dev/stderr"; failed = 1; exit 1 }
    match($0, /^<!-- include: [^ ]+ -->$/) {
      if (inside) fail("include opened at line " NR " inside another")
      print
      fragment = $0
      sub(/^<!-- include: /, "", fragment)
      sub(/ -->$/, "", fragment)
      file = root "/" fragment
      # The fragment is a page of its own, so it has a "# title"; the page it goes into has its own.
      if ((getline line < file) <= 0) fail("cannot read " fragment)
      while (line ~ /^# / || line == "") { if ((getline line < file) <= 0) break }
      do { print line } while ((getline line < file) > 0)
      close(file)
      inside = 1
      next
    }
    /^<!-- end include -->$/ {
      if (!inside) fail("end include at line " NR " without an include")
      inside = 0
      print
      next
    }
    !inside { print }
    END { if (!failed && inside) fail("include is never closed") }
  ' "$page" > "$tmp" || { rm -f "$tmp"; exit 1; }

  if cmp -s "$tmp" "$page"; then
    rm -f "$tmp"
  else
    cat "$tmp" > "$page"
    rm -f "$tmp"
    echo "updated $page"
  fi
done
