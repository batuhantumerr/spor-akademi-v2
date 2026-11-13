#!/usr/bin/env bash
# P4-08 (DEC-524, DEC-567): what row level security costs, measured on the load test database (prepare-db.sh) with pgbench
# from the compose PostgreSQL image. Four variants of the same typical reads, eight clients for 20 seconds each:
#   rls+set_config   the application's way: set_config before every query (EF opens a connection per query, DEC-520)
#   rls              the school set once per connection (PGOPTIONS): the policy alone, without the extra round trip
#   no-rls           the same query as the superuser, who is not subject to the policies (EF's tenant filter only)
# for a tenant table (students list page) and a platform table (identity.users by e-mail: the "OR is_platform_scope()"
# arm, DEC-521), plus EXPLAIN ANALYZE of both. Output: perf/k6/out/rls-bench.txt.
# Usage: bash scripts/perf/rls-bench.sh [seconds]
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../lib/env.sh"

seconds="${1:-20}"
# Git Bash would rewrite the container paths (/tmp/...) into Windows paths.
export MSYS_NO_PATHCONV=1
[[ "$seconds" =~ ^[0-9]+$ ]] || { echo "error: seconds must be a whole number" >&2; exit 2; }
load_env
require_env SPOR_AKADEMI_APP_PASSWORD POSTGRES_PASSWORD
cd "$REPO_ROOT"
db=spor_akademi_perf
out=perf/k6/out/rls-bench.txt
mkdir -p perf/k6/out

psql_su() { docker compose exec -T postgres psql -q -At -U postgres -d "$db" "$@"; }

# The largest school and one of its users (synthetic .test addresses).
tenant="$(psql_su -c "SELECT tenant_id FROM students.students GROUP BY tenant_id ORDER BY count(*) DESC LIMIT 1")"
email="$(psql_su -c "SELECT email FROM identity.users WHERE tenant_id = '$tenant' AND role = 'Coach' ORDER BY email LIMIT 1")"
[[ "$tenant" =~ ^[0-9a-f-]{36}$ && "$email" =~ ^[a-z0-9.@-]+$ ]] || { echo "error: no load test data; run scripts/perf/prepare-db.sh" >&2; exit 1; }

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
students_query="SELECT id, first_name, last_name FROM students.students WHERE tenant_id = :tenant::uuid ORDER BY last_name, first_name, id LIMIT 20;"
users_query="SELECT id, role, tenant_id FROM identity.users WHERE email = :email;"
set_tenant="SELECT set_config('app.tenant_id', :tenant, false), set_config('app.platform', '', false);"
set_platform="SELECT set_config('app.tenant_id', '', false), set_config('app.platform', 'on', false);"
printf '%s\n%s\n' "$set_tenant" "$students_query" > "$work/students-set.sql"
printf '%s\n' "$students_query" > "$work/students.sql"
printf '%s\n%s\n' "$set_tenant" "$users_query" > "$work/users-tenant-set.sql"
printf '%s\n%s\n' "$set_platform" "$users_query" > "$work/users-platform-set.sql"
printf '%s\n' "$users_query" > "$work/users.sql"
for f in "$work"/*.sql; do
  src="$f"
  if command -v cygpath >/dev/null 2>&1; then src="$(cygpath -m "$f")"; fi
  docker compose cp "$src" "postgres:/tmp/$(basename "$f")" >/dev/null 2>&1; done

# $1 label, $2 role, $3 script, $4 PGOPTIONS. The app role's password goes by name through the environment.
bench() {
  local tps latency
  local result
  local password=SPOR_AKADEMI_APP_PASSWORD
  if [[ "$2" == postgres ]]; then password=POSTGRES_PASSWORD; fi
  result="$(PGPASSWORD="${!password}" PGOPTIONS="$4" docker compose exec -T -e PGPASSWORD -e PGOPTIONS postgres \
    pgbench -n -h 127.0.0.1 -U "$2" -d "$db" -M extended -c 8 -j 4 -T "$seconds" -D "tenant=$tenant" -D "email=$email" -f "/tmp/$3" 2>&1)"
  tps="$(grep -oE 'tps = [0-9.]+' <<<"$result" | head -1 | cut -d' ' -f3)"
  latency="$(grep -oE 'latency average = [0-9.]+ ms' <<<"$result" | cut -d' ' -f4)"
  printf '| %s | %s | %s |\n' "$1" "$tps" "$latency"
}

{
  echo "# RLS ölçümü ($(date -u +%Y-%m-%dT%H:%MZ), pgbench, 8 istemci, ${seconds} sn, okul: en büyük okul)"
  echo
  echo "| variant | transactions/s | average latency (ms) |"
  echo "| --- | ---: | ---: |"
  bench "students page: rls + set_config before the query" spor_akademi_app students-set.sql ""
  bench "students page: rls, school set once per connection" spor_akademi_app students.sql "-c app.tenant_id=$tenant"
  bench "students page: superuser, no rls" postgres students.sql ""
  bench "users by e-mail, school scope: rls + set_config" spor_akademi_app users-tenant-set.sql ""
  bench "users by e-mail, platform scope (OR arm): rls + set_config" spor_akademi_app users-platform-set.sql ""
  bench "users by e-mail, platform scope (OR arm): rls, set once" spor_akademi_app users.sql "-c app.platform=on"
  bench "users by e-mail: superuser, no rls" postgres users.sql ""
  echo
  echo "## EXPLAIN ANALYZE"
  for scope in "app.tenant_id=$tenant" "app.platform=on"; do
    echo
    echo "### users by e-mail as the app role ($scope)"
    PGPASSWORD="$SPOR_AKADEMI_APP_PASSWORD" PGOPTIONS="-c $scope" docker compose exec -T -e PGPASSWORD -e PGOPTIONS postgres \
      psql -q -h 127.0.0.1 -U spor_akademi_app -d "$db" -c "EXPLAIN (ANALYZE, COSTS OFF, TIMING ON, SUMMARY ON) SELECT id, role, tenant_id FROM identity.users WHERE email = '$email'"
  done
  echo
  echo "### students page as the app role (app.tenant_id set)"
  PGPASSWORD="$SPOR_AKADEMI_APP_PASSWORD" PGOPTIONS="-c app.tenant_id=$tenant" docker compose exec -T -e PGPASSWORD -e PGOPTIONS postgres \
    psql -q -h 127.0.0.1 -U spor_akademi_app -d "$db" -c "EXPLAIN (ANALYZE, COSTS OFF, TIMING ON, SUMMARY ON) SELECT id, first_name, last_name FROM students.students WHERE tenant_id = '$tenant' ORDER BY last_name, first_name, id LIMIT 20"
  echo
  echo "### students page as the superuser (no rls)"
  psql_su -c "EXPLAIN (ANALYZE, COSTS OFF, TIMING ON, SUMMARY ON) SELECT id, first_name, last_name FROM students.students WHERE tenant_id = '$tenant' ORDER BY last_name, first_name, id LIMIT 20"
} | tee "$out"
