#!/usr/bin/env bash
# Smoke check of the admin and parent nginx images (docker/*/nginx.conf): every kind of response carries the same
# security headers. Used by the CI images job.
#
# Usage: bash scripts/check-security-headers.sh <base-url> [<extra path>...]
#   bash scripts/check-security-headers.sh http://127.0.0.1:8081
#   bash scripts/check-security-headers.sh http://127.0.0.1:8082 /sw.js /manifest.webmanifest   # parent (P3-07)
set -euo pipefail

base="${1:?usage: check-security-headers.sh <base-url> [<extra path>...]}"
shift
required=(
  "referrer-policy: no-referrer"
  "x-content-type-options: nosniff"
  "x-frame-options: deny"
  "content-security-policy: frame-ancestors 'none'"
)

# The SPA fallback (a client route), index.html itself, a static file, and the image's own locations (the parent
# app's service worker and manifest have their own nginx locations, which must repeat the headers).
for path in /some/deep/link /index.html /favicon.ico "$@"; do
  headers="$(curl -fsSI --max-time 10 "$base$path" | tr -d '\r' | tr '[:upper:]' '[:lower:]')"
  for header in "${required[@]}"; do
    if ! grep -qxF "$header" <<<"$headers"; then
      echo "$base$path is missing the header: $header" >&2
      exit 1
    fi
  done
done

echo "Security headers present on $base."
