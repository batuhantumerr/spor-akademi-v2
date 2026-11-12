#!/usr/bin/env bash
# Smoke check of the admin and parent nginx images (P3-08, DEC-402): /api and /hubs belong to the API behind the
# reverse proxy. If such a request reaches the static image anyway it must get 404 with the security headers and
# no-store, never the SPA's index.html. Used by the CI images job.
#
# Usage: bash scripts/check-api-paths.sh <base-url>
set -euo pipefail

base="${1:?usage: check-api-paths.sh <base-url>}"
required=(
  "cache-control: no-store"
  "referrer-policy: no-referrer"
  "x-content-type-options: nosniff"
  "x-frame-options: deny"
  "content-security-policy: frame-ancestors 'none'"
)

for path in /hubs /hubs/notifications /hubs/notifications/negotiate /api /api/v1/me; do
  headers="$(curl -sSI --max-time 10 "$base$path" | tr -d '\r' | tr '[:upper:]' '[:lower:]')"
  if ! grep -qE '^http/[0-9.]+ 404' <<<"$headers"; then
    echo "$base$path is not 404: $(head -1 <<<"$headers")" >&2
    exit 1
  fi
  for header in "${required[@]}"; do
    if ! grep -qxF "$header" <<<"$headers"; then
      echo "$base$path is missing the header: $header" >&2
      exit 1
    fi
  done
done

# Paths that only start like the API's stay client routes.
curl -fsS --max-time 10 "$base/hubsx" >/dev/null

echo "API paths are never served as the app on $base."
