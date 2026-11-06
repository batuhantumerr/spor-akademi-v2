#!/usr/bin/env bash
# Seeds two demo schools with synthetic data into the local database (P1-38, DEC-220), through the
# application layer as the least-privileged app role. Refuses if the demo schools exist; never runs in
# Production. The demo password comes from DEMO_PASSWORD in .env, or is generated and printed once.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib/env.sh"

load_env
require_env SPOR_AKADEMI_APP_PASSWORD

ConnectionStrings__App="$(connection_string spor_akademi_app SPOR_AKADEMI_APP_PASSWORD)"
export ConnectionStrings__App
export DOTNET_ENVIRONMENT="${DOTNET_ENVIRONMENT:-Development}"
if [[ -n "${DEMO_PASSWORD:-}" ]]; then export DEMO_PASSWORD; fi

cd "$REPO_ROOT"
exec dotnet run --project src/SporAkademi.DemoSeed "$@"
