#!/usr/bin/env bash
# P4-08 (DEC-562): prepares the load test database spor_akademi_perf on the compose PostgreSQL. Recreates it, applies
# the migrations as the migrator role, turns on pg_stat_statements there and seeds the demo schools plus larger load
# test schools and parent accounts through the application layer (scripts/seed-demo.sh, synthetic data only). Writes
# the sign-in addresses (no passwords) to perf/k6/out/manifest.json for the k6 scenarios. The development database
# (spor_akademi) is never touched; only this fixed name is ever dropped.
#
# Usage: bash scripts/perf/prepare-db.sh [--load-schools N] [--parents N]
#   defaults: 12 load schools (about 5 000 students with the two demo schools) and 20 parent accounts per school.
#   Seeding replays three months of history day by day: count on about two minutes per load school.
# Needs: docker compose up -d --wait, dotnet tool restore, DEMO_PASSWORD in .env (the k6 scenarios sign in with it).
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../lib/env.sh"

load_schools=12
parents=20
while [[ $# -gt 0 ]]; do
  case "$1" in
    --load-schools) load_schools="$2"; shift 2 ;;
    --parents) parents="$2"; shift 2 ;;
    *) echo "error: unknown argument $1" >&2; exit 2 ;;
  esac
done
[[ "$load_schools" =~ ^[0-9]+$ && "$parents" =~ ^[0-9]+$ ]] || { echo "error: counts must be whole numbers" >&2; exit 2; }

load_env
require_env SPOR_AKADEMI_MIGRATOR_PASSWORD SPOR_AKADEMI_APP_PASSWORD DEMO_PASSWORD

export SPOR_AKADEMI_DB_NAME=spor_akademi_perf
cd "$REPO_ROOT"

if ! docker compose exec -T postgres pg_isready -U postgres -d postgres >/dev/null 2>&1; then
  echo "error: PostgreSQL is not running. Start it with: docker compose up -d --wait" >&2
  exit 1
fi

echo "perf: recreating database $SPOR_AKADEMI_DB_NAME"
# Same ownership and grants as docker/postgres/initdb/10-create-roles.sh (ADR-0011), as in scripts/e2e.sh.
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
-- DEC-563: the statistics view, readable by the superuser only (the app role gets nothing new).
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;
SQL

echo "perf: applying migrations as the migrator role"
bash scripts/ef.sh database update >/dev/null

mkdir -p perf/k6/out
manifest="$REPO_ROOT/perf/k6/out/manifest.json"
if command -v cygpath >/dev/null 2>&1; then manifest="$(cygpath -m "$manifest")"; fi
echo "perf: seeding 2 demo schools + $load_schools load test schools, $parents parent accounts each (takes a while)"
start=$(date +%s)
bash scripts/seed-demo.sh --DemoSeed:LoadSchools="$load_schools" --DemoSeed:ParentAccounts="$parents" --DemoSeed:Manifest="$manifest"
echo "perf: seeded in $(( $(date +%s) - start )) s"

# The seeded history leaves its outbox messages (attendance and payment notifications, e-mails of three months) pending;
# delivering them now would mean nothing and the first load test would mostly drain that backlog (DEC-562). They are
# marked processed, so a run measures the steady state. The superuser is not subject to row level security, so these
# are the totals over every school.
docker compose exec -T postgres psql -q -At -U postgres -d "$SPOR_AKADEMI_DB_NAME" <<'SQL'
WITH done AS (UPDATE outbox.messages SET processed_at = now() WHERE processed_at IS NULL AND dead_lettered_at IS NULL RETURNING 1)
SELECT 'outbox messages of the seeded history marked processed: ' || count(*) FROM done;
VACUUM ANALYZE;
SELECT 'schools: ' || count(*) FROM tenancy.schools;
SELECT 'students: ' || count(*) FROM students.students;
SELECT 'enrollments: ' || count(*) FROM enrollments.enrollments;
SELECT 'attendance records: ' || count(*) FROM attendance.attendance_records;
SELECT 'payments: ' || count(*) FROM billing.payments;
SELECT 'users: ' || count(*) FROM identity.users;
SQL
