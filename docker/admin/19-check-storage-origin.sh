#!/bin/sh
# P4-01 (DEC-502): STORAGE_PUBLIC_ORIGIN goes into the Content-Security-Policy (img-src) by envsubst in the next
# entrypoint step (20-envsubst-on-templates.sh). Only empty or space-separated http(s) origins are accepted: a quote,
# a semicolon, a path or a line break would change the policy or break the header, so the container refuses to start.
set -eu

value="${STORAGE_PUBLIC_ORIGIN:-}"
if [ -z "$value" ]; then
  echo "$0: warning: STORAGE_PUBLIC_ORIGIN is empty; student photo thumbnails from the document store will be blocked (img-src)." >&2
  exit 0
fi

# The whole value at once: grep would accept a value of several lines when any one line matched.
newline="$(printf '\n_')"
newline="${newline%_}"
carriage_return="$(printf '\r')"
case "$value" in
  *"$newline"* | *"$carriage_return"*)
    echo "$0: STORAGE_PUBLIC_ORIGIN must be on one line." >&2
    exit 1
    ;;
esac
origin='https?://[A-Za-z0-9.-]+(:[0-9]{1,5})?'
if ! printf '%s\n' "$value" | grep -Eqx "$origin( $origin)*"; then
  echo "$0: STORAGE_PUBLIC_ORIGIN must be empty or http(s) origins separated by spaces (no path, quote or ';')." >&2
  exit 1
fi
