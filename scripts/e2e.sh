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
# Needs: docker compose up -d --wait, dotnet tool restore, npm ci and npx playwright install in web/admin.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib/env.sh"

load_env
require_env SPOR_AKADEMI_MIGRATOR_PASSWORD SPOR_AKADEMI_APP_PASSWORD JWT_SIGNING_KEY

# Only this database is ever dropped; the name is fixed on purpose.
export SPOR_AKADEMI_DB_NAME=spor_akademi_e2e
cd "$REPO_ROOT"

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
dotnet build src/SporAkademi.Api -c Release --nologo -v quiet

echo "e2e: applying migrations as the migrator role"
bash scripts/ef.sh database update >/dev/null

# Handed to playwright.config.ts through the environment only; never printed or written to disk.
E2E_API_CONNECTION="$(connection_string spor_akademi_app SPOR_AKADEMI_APP_PASSWORD)"
E2E_JWT_SIGNING_KEY="$JWT_SIGNING_KEY"
E2E_PLATFORM_ADMIN_EMAIL="platform-$(openssl rand -hex 4)@e2e.test"
E2E_PLATFORM_ADMIN_PASSWORD="$(openssl rand -hex 24)"
export E2E_API_CONNECTION E2E_JWT_SIGNING_KEY E2E_PLATFORM_ADMIN_EMAIL E2E_PLATFORM_ADMIN_PASSWORD

# A throwaway self-signed certificate for https://localhost (the refresh cookie is Secure), removed
# when the run ends. On Git Bash, paths are passed in Windows form and MSYS_NO_PATHCONV keeps
# "/CN=localhost" from being rewritten into a path.
tls_dir="$(mktemp -d)"
trap 'rm -rf "$tls_dir"' EXIT
native_tls_dir="$tls_dir"
if command -v cygpath >/dev/null 2>&1; then native_tls_dir="$(cygpath -m "$tls_dir")"; fi
export E2E_TLS_KEY="$native_tls_dir/key.pem" E2E_TLS_CERT="$native_tls_dir/cert.pem"
if ! MSYS_NO_PATHCONV=1 openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj "/CN=localhost" \
  -addext "subjectAltName=DNS:localhost,IP:127.0.0.1,IP:::1" \
  -keyout "$E2E_TLS_KEY" -out "$E2E_TLS_CERT" >/dev/null 2>&1; then
  echo "error: openssl could not create the e2e TLS certificate." >&2
  exit 1
fi

cd web/admin
echo "e2e: building the admin app (production)"
npm run --silent build >/dev/null
npx playwright test "$@"
