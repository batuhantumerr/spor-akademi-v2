#!/usr/bin/env bash
# Runs the Playwright end-to-end suite (P1-36, DEC-240) against a real API and PostgreSQL.
#
# Every run recreates a throwaway database (spor_akademi_e2e) on the compose PostgreSQL, applies the
# migrations as the migrator role, builds the API and hands over to Playwright, which starts the API
# (http://127.0.0.1:5180) and the admin app (https://localhost:4300, self-signed) and stops them afterwards. The
# global setup seeds its own school and accounts with credentials generated for this run only; the
# development database (spor_akademi) and a running dev API/app are never touched.
#
# Usage: bash scripts/e2e.sh [playwright args]
#   bash scripts/e2e.sh --project=chromium --project=mobile-chromium
# Needs: docker compose up -d --wait, dotnet tool restore, npm ci and npx playwright install in web/admin, npm ci in
# web/parent. Starts the compose Mailpit (mail profile) for the parent invitation e-mail.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib/env.sh"

load_env
require_env SPOR_AKADEMI_MIGRATOR_PASSWORD SPOR_AKADEMI_APP_PASSWORD JWT_SIGNING_KEY

# Only this database is ever dropped; the name is fixed on purpose.
export SPOR_AKADEMI_DB_NAME=spor_akademi_e2e
cd "$REPO_ROOT"

# Scratch space for build logs and the TLS certificate, removed when the run ends.
work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT

# Runs a step with its output kept aside; prints the output only when the step fails.
quietly() {
  local log="$work_dir/step.log"
  if ! "$@" >"$log" 2>&1; then
    cat "$log" >&2
    echo "error: step failed: $*" >&2
    exit 1
  fi
}

if ! docker compose exec -T postgres pg_isready -U postgres -d postgres >/dev/null 2>&1; then
  echo "error: PostgreSQL is not running. Start it with: docker compose up -d --wait" >&2
  exit 1
fi

echo "e2e: recreating database $SPOR_AKADEMI_DB_NAME"
# Same ownership and grants as docker/postgres/initdb/10-create-roles.sh (ADR-0011); the roles exist already.
docker compose exec -T postgres psql -q -v ON_ERROR_STOP=1 -U postgres -d postgres -v db="$SPOR_AKADEMI_DB_NAME" <<'SQL'
DROP DATABASE IF EXISTS :"db" WITH (FORCE);
CREATE DATABASE :"db" OWNER spor_akademi_migrator;
REVOKE ALL ON DATABASE :"db" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"db" TO spor_akademi_app;
\connect :"db"
REVOKE ALL ON SCHEMA public FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE spor_akademi_migrator GRANT USAGE ON SCHEMAS TO spor_akademi_app;
ALTER DEFAULT PRIVILEGES FOR ROLE spor_akademi_migrator GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO spor_akademi_app;
ALTER DEFAULT PRIVILEGES FOR ROLE spor_akademi_migrator GRANT USAGE, SELECT ON SEQUENCES TO spor_akademi_app;
SQL

echo "e2e: building the API (Release, so a running dev API does not lock the build output)"
quietly dotnet build src/SporAkademi.Api -c Release --nologo

echo "e2e: applying migrations as the migrator role"
quietly bash scripts/ef.sh database update

# Handed to playwright.config.ts through the environment only; never printed or written to disk.
E2E_API_CONNECTION="$(connection_string spor_akademi_app SPOR_AKADEMI_APP_PASSWORD)"
E2E_JWT_SIGNING_KEY="$JWT_SIGNING_KEY"
E2E_PLATFORM_ADMIN_EMAIL="platform-$(openssl rand -hex 4)@e2e.test"
E2E_PLATFORM_ADMIN_PASSWORD="$(openssl rand -hex 24)"
export E2E_API_CONNECTION E2E_JWT_SIGNING_KEY E2E_PLATFORM_ADMIN_EMAIL E2E_PLATFORM_ADMIN_PASSWORD

# A throwaway self-signed certificate for https://localhost (the refresh cookie is Secure). On Git
# Bash, paths are passed in Windows form and MSYS_NO_PATHCONV keeps "/CN=localhost" from being
# rewritten into a path.
native_dir="$work_dir"
if command -v cygpath >/dev/null 2>&1; then native_dir="$(cygpath -m "$work_dir")"; fi
export E2E_TLS_KEY="$native_dir/key.pem" E2E_TLS_CERT="$native_dir/cert.pem"
quietly env MSYS_NO_PATHCONV=1 openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj "/CN=localhost" \
  -addext "subjectAltName=DNS:localhost,IP:127.0.0.1,IP:::1" \
  -keyout "$E2E_TLS_KEY" -out "$E2E_TLS_CERT"

# P3-05: the parent flow reads its invitation e-mail from Mailpit (compose "mail" profile: loopback only, relays
# nothing). Starting it is idempotent; a Mailpit already running for development is reused.
echo "e2e: starting Mailpit"
quietly docker compose --profile mail up -d --wait mailpit
export E2E_SMTP_PORT="${MAILPIT_SMTP_HOST_PORT:-51025}"
export E2E_MAILPIT_URL="http://127.0.0.1:${MAILPIT_UI_HOST_PORT:-58025}"

echo "e2e: building the parent app (production)"
quietly npm --prefix web/parent run build

cd web/admin
echo "e2e: building the admin app (production)"
quietly npm run build
npx playwright test "$@"
