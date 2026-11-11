#!/usr/bin/env bash
# Admin and parent apps share one set of theme tokens (docs/ui-tema.md). Each app keeps its own copy so
# the builds stay independent; this check fails when the copies drift. Source of truth: web/admin.
# Fix a drift by copying the block between the theme-tokens markers from web/admin/src/styles.css.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
admin="$root/web/admin/src/styles.css"
parent="$root/web/parent/src/styles/tokens.css"

block() {
  # Lines strictly between the start and end markers.
  awk '/theme-tokens:end/ { inside = 0 } inside { print } /theme-tokens:start/ { inside = 1 }' "$1"
}

# Each marker exactly once per file: a second start or a stray end would make the copied block ambiguous.
for file in "$admin" "$parent"; do
  for marker in theme-tokens:start theme-tokens:end; do
    count="$(grep -c "$marker" "$file" || true)"
    if [ "$count" != "1" ]; then
      echo "$file must contain '$marker' exactly once (found $count)." >&2
      exit 1
    fi
  done
done

admin_block="$(block "$admin")"
parent_block="$(block "$parent")"

if [ -z "$admin_block" ]; then
  echo "No theme-tokens block found in $admin." >&2
  exit 1
fi

if [ "$admin_block" != "$parent_block" ]; then
  echo "Theme tokens drifted between web/admin and web/parent:" >&2
  diff <(echo "$admin_block") <(echo "$parent_block") >&2 || true
  exit 1
fi

echo "Theme tokens match ($(echo "$admin_block" | wc -l) lines)."
