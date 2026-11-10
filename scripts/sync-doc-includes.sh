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
# A path ending in #anchor copies one section instead: the lines under the heading with that anchor (as
# GitHub spells it), up to the next heading of the same or a higher level. The heading itself is left
# out, so the page gives the section a heading of its own:
#
#   <!-- include: docs/SUPPORTED_INVERTERS.md#inverters-at-a-glance -->
#
# Usage: scripts/sync-doc-includes.sh [page.md ...]
#   With no arguments it updates README.md and docs/SUPPORTED_PV_SYSTEMS.md.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [ "$#" -eq 0 ]; then
  set -- "$root/README.md" "$root/docs/SUPPORTED_PV_SYSTEMS.md"
fi

for page in "$@"; do
  tmp="$(mktemp)"
  awk -v root="$root" -v page="$page" '
    function fail(message) { print page ": " message > "/dev/stderr"; failed = 1; exit 1 }
    # The anchor GitHub gives a heading: lower case, punctuation dropped, spaces turned into hyphens.
    function slug(text) {
      text = tolower(text)
      gsub(/[^a-z0-9 _-]/, "", text)
      gsub(/ /, "-", text)
      return text
    }
    match($0, /^<!-- include: [^ ]+ -->$/) {
      if (inside) fail("include opened at line " NR " inside another")
      print
      fragment = $0
      sub(/^<!-- include: /, "", fragment)
      sub(/ -->$/, "", fragment)
      anchor = ""
      if (index(fragment, "#") > 0) {
        anchor = substr(fragment, index(fragment, "#") + 1)
        fragment = substr(fragment, 1, index(fragment, "#") - 1)
      }
      file = root "/" fragment
      if ((getline line < file) <= 0) fail("cannot read " fragment)
      if (anchor == "") {
        # The fragment is a page of its own, so it has a "# title"; the page it goes into has its own.
        while (line ~ /^# / || line == "") { if ((getline line < file) <= 0) break }
        do { print line } while ((getline line < file) > 0)
      } else {
        found = 0; level = 0; fence = 0; n = 0
        do {
          if (line ~ /^```/) fence = !fence
          heading = !fence && match(line, /^#+ /)
          if (found && heading && RLENGTH - 1 <= level) break
          if (found) section[++n] = line
          if (!found && heading && slug(substr(line, RLENGTH + 1)) == anchor) { found = 1; level = RLENGTH - 1 }
        } while ((getline line < file) > 0)
        if (!found) fail(fragment " has no section #" anchor)
        # Blank lines at either end belong to the page around the include, not to the section.
        first = 1; while (first <= n && section[first] == "") first++
        last = n; while (last >= first && section[last] == "") last--
        for (i = first; i <= last; i++) print section[i]
        delete section
      }
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
