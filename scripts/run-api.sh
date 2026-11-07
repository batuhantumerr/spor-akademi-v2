#!/usr/bin/env bash
# Runs the API locally against the docker compose database as the least-privileged app role.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib/env.sh"

load_env
require_env SPOR_AKADEMI_APP_PASSWORD JWT_SIGNING_KEY

ConnectionStrings__App="$(connection_string spor_akademi_app SPOR_AKADEMI_APP_PASSWORD)"
Jwt__SigningKey="$JWT_SIGNING_KEY"
export ConnectionStrings__App Jwt__SigningKey
# Optional: seeds the first Platform Admin when none exists (P1-12). Passed through as set, so the API
# warns about a missing setting or a placeholder password instead of silently skipping the seed.
if [[ -n "${PLATFORM_ADMIN_EMAIL:-}" ]]; then export PlatformAdmin__Email="$PLATFORM_ADMIN_EMAIL"; fi
if [[ -n "${PLATFORM_ADMIN_PASSWORD:-}" ]]; then export PlatformAdmin__InitialPassword="$PLATFORM_ADMIN_PASSWORD"; fi
# Optional (DEC-315, DEC-316): the Valkey L2 cache, used only when VALKEY_PASSWORD is set and the compose "cache"
# profile's valkey service is running; otherwise the cache is in memory only.
if [[ -n "${VALKEY_PASSWORD:-}" && "$VALKEY_PASSWORD" != change-me* ]] \
  && (cd "$REPO_ROOT" && docker compose --profile cache ps --status running --services 2>/dev/null | grep -qx valkey); then
  export ConnectionStrings__Cache="127.0.0.1:${VALKEY_HOST_PORT:-56379},password=$VALKEY_PASSWORD"
  echo "run-api: using the Valkey cache on 127.0.0.1:${VALKEY_HOST_PORT:-56379}"
else
  echo "run-api: Valkey not running (docker compose --profile cache up -d --wait); in-memory cache only"
fi
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://127.0.0.1:5080}"
export Seq__ServerUrl="http://127.0.0.1:${SEQ_HOST_PORT:-5341}"

cd "$REPO_ROOT"
exec dotnet run --project src/SporAkademi.Api --no-launch-profile "$@"
