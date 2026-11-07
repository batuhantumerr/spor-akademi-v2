#!/usr/bin/env bash
# Runs the API locally against the docker compose database as the least-privileged app role.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib/env.sh"

load_env
require_env SPOR_AKADEMI_APP_PASSWORD JWT_SIGNING_KEY STORAGE_ACCESS_KEY STORAGE_SECRET_KEY

ConnectionStrings__App="$(connection_string spor_akademi_app SPOR_AKADEMI_APP_PASSWORD)"
Jwt__SigningKey="$JWT_SIGNING_KEY"
export ConnectionStrings__App Jwt__SigningKey
# Optional: seeds the first Platform Admin when none exists (P1-12). Passed through as set, so the API
# warns about a missing setting or a placeholder password instead of silently skipping the seed.
if [[ -n "${PLATFORM_ADMIN_EMAIL:-}" ]]; then export PlatformAdmin__Email="$PLATFORM_ADMIN_EMAIL"; fi
if [[ -n "${PLATFORM_ADMIN_PASSWORD:-}" ]]; then export PlatformAdmin__InitialPassword="$PLATFORM_ADMIN_PASSWORD"; fi
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://127.0.0.1:5080}"
export Seq__ServerUrl="http://127.0.0.1:${SEQ_HOST_PORT:-5341}"
# Document storage (compose service "storage", DEC-320). The browser opens the signed links at the same address.
export Storage__ServiceUrl="http://127.0.0.1:${STORAGE_HOST_PORT:-58333}"
export Storage__AccessKey="$STORAGE_ACCESS_KEY"
export Storage__SecretKey="$STORAGE_SECRET_KEY"
export Storage__Bucket="${STORAGE_BUCKET:-spor-akademi-documents}"

cd "$REPO_ROOT"
exec dotnet run --project src/SporAkademi.Api --no-launch-profile "$@"
