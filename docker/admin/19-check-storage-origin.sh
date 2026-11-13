#!/bin/sh
# P4-01 (DEC-502): STORAGE_PUBLIC_ORIGIN goes into the Content-Security-Policy (img-src) by envsubst in the next
# entrypoint step (20-envsubst-on-templates.sh). Only empty or space-separated http(s) origins are accepted: a quote,
# a semicolon or a path would change the policy or break the header, so the container refuses to start instead.
set -eu

origin='https?://[A-Za-z0-9.-]+(:[0-9]{1,5})?'
value="${STORAGE_PUBLIC_ORIGIN:-}"
if [ -n "$value" ] && ! printf '%s' "$value" | grep -Eqx "$origin( $origin)*"; then
  echo "$0: STORAGE_PUBLIC_ORIGIN must be empty or http(s) origins separated by spaces (no path, quote or ';')." >&2
  exit 1
fi
