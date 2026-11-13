#!/usr/bin/env bash
# P4-08 (DEC-560 – DEC-565): runs the k6 load test against the Release API image on the load test database.
#
#   bash scripts/perf/run-k6.sh                       one API instance, 300 virtual users (PROFILE=load)
#   bash scripts/perf/run-k6.sh --instances 2         two instances behind Caddy (NFR-SCL-01), same test
#   bash scripts/perf/run-k6.sh --profile smoke       12 virtual users for a minute
#   bash scripts/perf/run-k6.sh --checks              session, SignalR, backplane and rate limit checks behind the proxy
#                                                     with the production limits (add --hub-lb round_robin to see why
#                                                     the hub needs affinity)
#
# The API instances, Valkey, Caddy and k6 run from compose (profiles cache, scale and k6); k6 (AGPL-3.0) is only ever run
# as that separate container (DEC-560). Rate limits are raised only in this environment, through the compose variables
# (DEC-561). Results: perf/k6/out/summary-<label>.md (+ .json) and the slowest and most frequent statements of the run
# from pg_stat_statements in perf/k6/out/pg-stat-<label>.txt. The services are stopped at the end unless --keep.
# Needs: docker compose up -d --wait and bash scripts/perf/prepare-db.sh (once); DEMO_PASSWORD, JWT_SIGNING_KEY and
# VALKEY_PASSWORD in .env.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../lib/env.sh"

instances=1
profile=load
checks=false
keep=false
label=""
hub_lb=cookie
while [[ $# -gt 0 ]]; do
  case "$1" in
    --instances) instances="$2"; shift 2 ;;
    --profile) profile="$2"; shift 2 ;;
    --checks) checks=true; instances=2; shift ;;
    --hub-lb) hub_lb="$2"; shift 2 ;;
    --label) label="$2"; shift 2 ;;
    --keep) keep=true; shift ;;
    *) echo "error: unknown argument $1" >&2; exit 2 ;;
  esac
done
[[ "$instances" == 1 || "$instances" == 2 ]] || { echo "error: --instances is 1 or 2" >&2; exit 2; }
[[ "$profile" == load || "$profile" == smoke ]] || { echo "error: --profile is load or smoke" >&2; exit 2; }
[[ "$hub_lb" == cookie || "$hub_lb" == round_robin ]] || { echo "error: --hub-lb is cookie or round_robin" >&2; exit 2; }
[[ -z "$label" || "$label" =~ ^[a-z0-9-]+$ ]] || { echo "error: --label may only contain a-z, 0-9 and -" >&2; exit 2; }

load_env
require_env SPOR_AKADEMI_APP_PASSWORD JWT_SIGNING_KEY VALKEY_PASSWORD DEMO_PASSWORD
cd "$REPO_ROOT"

if [[ ! -f perf/k6/out/manifest.json ]]; then
  echo "error: no load test data. Run bash scripts/perf/prepare-db.sh first." >&2
  exit 1
fi

export PERF_HUB_LB_POLICY="$hub_lb"
if $checks; then
  # The production limits (DEC-504, DEC-388), to observe how two instances count them.
  export PERF_USER_PERMITS=300 PERF_SCHOOL_WRITE_PERMITS=300 PERF_ANONYMOUS_PERMITS=120 PERF_AUTH_PERMITS=10 PERF_PARENT_PERMITS=60
  label="${label:-checks-$hub_lb}"
else
  label="${label:-$profile-${instances}x}"
fi

services=(api-1)
target=http://api-1:8080
if [[ "$instances" == 2 ]]; then
  services=(api-1 api-2 proxy)
  target=http://proxy:8080
fi

compose() { docker compose --profile cache --profile scale --profile k6 "$@"; }

stop_services() {
  if ! $keep; then compose stop api-1 api-2 proxy >/dev/null 2>&1 || true; fi
}
trap stop_services EXIT

echo "perf: building the API image and starting ${services[*]}"
compose build api-1 >/dev/null
# Recreated, so the limit settings of this run apply and every run starts with empty in-memory caches and limiters.
compose up -d --wait --force-recreate valkey "${services[@]}" >/dev/null

# The chiseled image has no shell or curl: readiness is asked from the host through the published ports.
for port in "${PERF_API_1_HOST_PORT:-58181}" $([[ "$instances" == 2 ]] && echo "${PERF_API_2_HOST_PORT:-58182}"); do
  for _ in $(seq 1 60); do
    if curl -fsS "http://127.0.0.1:$port/health/ready" >/dev/null 2>&1; then break; fi
    sleep 1
  done
  curl -fsS "http://127.0.0.1:$port/health/ready" >/dev/null || { echo "error: the API on port $port is not ready" >&2; compose logs --tail 50 api-1 >&2; exit 1; }
done

# Statement statistics of this run only (DEC-563).
compose exec -T postgres psql -q -U postgres -d spor_akademi_perf -c "SELECT pg_stat_statements_reset()" >/dev/null

script=load.js
if $checks; then script=scale-checks.js; fi
echo "perf: k6 $script ($label) against $target"
status=0
# DEMO_PASSWORD is passed by name, from this environment: it never appears on a command line.
compose run --rm -e DEMO_PASSWORD -e BASE_URL="$target" -e DIRECT_URL=http://api-1:8080 -e PROFILE="$profile" -e LABEL="$label" \
  k6 run --quiet --include-system-env-vars "$script" || status=$?

compose exec -T postgres psql -q -U postgres -d spor_akademi_perf > "perf/k6/out/pg-stat-$label.txt" <<'SQL'
\pset pager off
\echo 'Most time in total (pg_stat_statements, this run; the app role only)'
SELECT calls, round(total_exec_time::numeric, 1) AS total_ms, round(mean_exec_time::numeric, 3) AS mean_ms,
       round(max_exec_time::numeric, 1) AS max_ms, rows, left(regexp_replace(query, '\s+', ' ', 'g'), 220) AS query
FROM pg_stat_statements s JOIN pg_roles r ON r.oid = s.userid
WHERE r.rolname = 'spor_akademi_app' ORDER BY total_exec_time DESC LIMIT 25;
\echo 'Most frequent'
SELECT calls, round(mean_exec_time::numeric, 3) AS mean_ms, left(regexp_replace(query, '\s+', ' ', 'g'), 220) AS query
FROM pg_stat_statements s JOIN pg_roles r ON r.oid = s.userid
WHERE r.rolname = 'spor_akademi_app' ORDER BY calls DESC LIMIT 15;
SQL
echo "perf: results in perf/k6/out/summary-$label.md and perf/k6/out/pg-stat-$label.txt"
exit $status
