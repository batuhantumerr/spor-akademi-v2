#!/usr/bin/env bash
# Runs the API locally against the docker compose database as the least-privileged app role.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib/env.sh"

load_env
require_env SPOR_AKADEMI_APP_PASSWORD JWT_SIGNING_KEY

ConnectionStrings__App="$(connection_string spor_akademi_app SPOR_AKADEMI_APP_PASSWORD)"
Jwt__SigningKey="$JWT_SIGNING_KEY"
export ConnectionStrings__App Jwt__SigningKey
# Optional: seeds the first Platform Admin when none exists (P1-12). Ignored once one exists.
if [[ -n "${PLATFORM_ADMIN_EMAIL:-}" && -n "${PLATFORM_ADMIN_PASSWORD:-}" && "${PLATFORM_ADMIN_PASSWORD}" != change-me* ]]; then
  export PlatformAdmin__Email="$PLATFORM_ADMIN_EMAIL"
  export PlatformAdmin__InitialPassword="$PLATFORM_ADMIN_PASSWORD"
fi
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://127.0.0.1:5080}"
export Seq__ServerUrl="http://127.0.0.1:${SEQ_HOST_PORT:-5341}"

cd "$REPO_ROOT"
exec dotnet run --project src/SporAkademi.Api --no-launch-profile "$@"
