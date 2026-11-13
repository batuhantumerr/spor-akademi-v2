#!/usr/bin/env bash
# Smoke check of the security headers (NFR-SEC-13, P4-01) on the running images: the admin and parent nginx images
# (docker/*/nginx.conf, docker/*/security-headers.inc*) and the API. Every kind of response carries the same headers,
# and the Content-Security-Policy is exactly the expected one. Used by the CI images job.
#
# Usage: bash scripts/check-security-headers.sh <admin|parent|api> <base-url> [<extra path>...]
#   STORAGE_PUBLIC_ORIGIN=https://files.example.test bash scripts/check-security-headers.sh admin http://127.0.0.1:8081
#   bash scripts/check-security-headers.sh parent http://127.0.0.1:8082 /sw.js /manifest.webmanifest   # P3-07
#   bash scripts/check-security-headers.sh api http://127.0.0.1:8080
# For admin, STORAGE_PUBLIC_ORIGIN is the value the container was started with (empty by default).
set -euo pipefail

app="${1:?usage: check-security-headers.sh <admin|parent|api> <base-url> [<extra path>...]}"
base="${2:?usage: check-security-headers.sh <admin|parent|api> <base-url> [<extra path>...]}"
shift 2

permissions="accelerometer=(), camera=(), display-capture=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), midi=(), payment=(), usb=()"
case "$app" in
  admin)
    csp="default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob: ${STORAGE_PUBLIC_ORIGIN:-}; connect-src 'self'; font-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'"
    # The SPA fallback (a client route), index.html itself, the pre-paint files and a hashed-style static file.
    paths=(/some/deep/link /index.html /boot.js /boot.css /favicon.ico /api/v1/misrouted)
    ;;
  parent)
    csp="default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; connect-src 'self'; font-src 'self'; manifest-src 'self'; worker-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'"
    paths=(/some/deep/link /index.html /boot.js /boot.css /favicon.ico /api/v1/misrouted)
    ;;
  api)
    csp="default-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'"
    # A health check, a refused API call, an unknown route.
    paths=(/health/live /api/v1/me /api/v1/no-such-route)
    ;;
  *)
    echo "unknown app: $app (admin, parent or api)" >&2
    exit 2
    ;;
esac

# Header values compared with runs of spaces collapsed and none before ';' (an empty origin leaves "blob: ;").
normalize() { sed -E 's/ +/ /g; s/ ;/;/g; s/ +$//'; }

required=(
  "referrer-policy: no-referrer"
  "x-content-type-options: nosniff"
  "x-frame-options: deny"
  "content-security-policy: $(normalize <<<"$csp" | tr '[:upper:]' '[:lower:]')"
)
if [[ "$app" != api ]]; then
  # The SPA images always send HSTS (DEC-501): browsers ignore it over plain HTTP, the proxy serves them over HTTPS.
  required+=("permissions-policy: $permissions" "strict-transport-security: max-age=31536000")
fi

for path in "${paths[@]}" "$@"; do
  # -f is not used: the API's refusals (401, 404) and the images' /api 404 must carry the headers too.
  headers="$(curl -sSI --max-time 10 "$base$path" | tr -d '\r' | tr '[:upper:]' '[:lower:]' | normalize)"
  if [[ -z "$headers" ]]; then
    echo "$base$path did not answer" >&2
    exit 1
  fi
  for header in "${required[@]}"; do
    if ! grep -qxF "$header" <<<"$headers"; then
      echo "$base$path is missing the header: $header" >&2
      grep -i '^content-security-policy:' <<<"$headers" >&2 || true
      exit 1
    fi
  done
  # Nothing tells which version runs here (nginx: server_tokens off; the API sends no Server header at all), and
  # the API sends HSTS only over HTTPS (NFR-SEC-02: Production, directly or through a known proxy).
  forbidden='^(x-powered-by):'
  if [[ "$app" == api ]]; then forbidden='^(server|x-powered-by|strict-transport-security):'; fi
  if grep -E "$forbidden" <<<"$headers" >&2 || grep -E '^server: .*[0-9]' <<<"$headers" >&2; then
    echo "$base$path sends the header above" >&2
    exit 1
  fi
done

echo "Security headers present on $base ($app)."
