#!/usr/bin/env bash
# Restore drill (P4-09, NFR-REL-04): proves end to end that a backup can be restored, and measures how long it takes.
#
#   1. A throwaway PostgreSQL server (the compose.yaml image and init script) with WAL archiving. Migrations as the
#      migrator role, the demo schools through the application layer (DemoSeed) and synthetic filler rows.
#   2. Full logical backup (backup-logical.sh) and physical base backup (backup-base.sh) as the backup role.
#   3. Changes after the backup: change 1, the recovery target time, change 2 (an "accidental" delete).
#   4. Disaster: the server and its data volume are destroyed; only the backups and the WAL archive remain.
#   5. Restore A: a new empty server, the full dump restored (restore-logical.sh).
#      Restore B: a new server, base backup + WAL archive rolled forward to the target time (restore-pitr.sh).
#   6. Each restore is compared with the source (drill-snapshot.sql: rows and checksums of every table, migrations,
#      owners, grants, row level security, policies, functions, roles) and the known records are checked.
#
# Usage: bash scripts/backup/drill.sh
#   DRILL_FILLER_ROWS  synthetic filler rows (default 200000, ~40 MB), so the timings say something about size
#   DRILL_REPORT       also write the timing table (Markdown) to this file
#   DRILL_KEEP=1       keep the drill's containers and volumes for inspection (removed otherwise, also on failure)
# Needs: docker and the .NET SDK (dotnet tool restore). Reads no .env: every password is generated for this run,
# lives only in this process's environment and is never printed. The compose server and its volume are never
# touched; every container and volume of the drill is named spor-akademi-drill-<run>-*.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"
# Git Bash: keep container paths (/backup, /archive) as they are; host paths are converted explicitly.
export MSYS_NO_PATHCONV=1

filler_rows="${DRILL_FILLER_ROWS:-200000}"
if [[ ! "$filler_rows" =~ ^[0-9]+$ ]]; then
  echo "error: DRILL_FILLER_ROWS must be a number." >&2
  exit 2
fi

image="$(sed -n 's/^ *image: *\(postgres:[^ ]*\) *$/\1/p' compose.yaml | head -n 1)"
if [[ -z "$image" ]]; then
  echo "error: no postgres image in compose.yaml." >&2
  exit 1
fi

host_path() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }
scripts_mount="$(host_path "$REPO_ROOT/scripts/backup"):/opt/backup:ro"
initdb_mount="$(host_path "$REPO_ROOT/docker/postgres/initdb"):/docker-entrypoint-initdb.d:ro"

run_id="$(date -u +%Y%m%d%H%M%S)-$(openssl rand -hex 3)"
prefix="spor-akademi-drill-$run_id"
src="$prefix-source"
full="$prefix-restore-full"
pitr="$prefix-restore-pitr"
volumes=("$prefix-source-data" "$prefix-wal-archive" "$prefix-backups" "$prefix-full-data" "$prefix-pitr-data")
work_dir="$(mktemp -d)"

cleanup() {
  local status=$?
  if [[ "${DRILL_KEEP:-}" == "1" ]]; then
    echo "drill: kept containers and volumes $prefix-*" >&2
  else
    docker rm -f "$src" "$full" "$pitr" >/dev/null 2>&1 || true
    docker volume rm "${volumes[@]}" >/dev/null 2>&1 || true
  fi
  rm -rf "$work_dir"
  exit "$status"
}
trap cleanup EXIT

# Generated for this run only; handed to containers by name (docker -e NAME), so never on a command line.
POSTGRES_PASSWORD="$(openssl rand -hex 24)"
SPOR_AKADEMI_MIGRATOR_PASSWORD="$(openssl rand -hex 24)"
SPOR_AKADEMI_APP_PASSWORD="$(openssl rand -hex 24)"
BACKUP_PASSWORD="$(openssl rand -hex 24)"
DEMO_PASSWORD="$(openssl rand -hex 16)"
export POSTGRES_PASSWORD SPOR_AKADEMI_MIGRATOR_PASSWORD SPOR_AKADEMI_APP_PASSWORD BACKUP_PASSWORD DEMO_PASSWORD
export SPOR_AKADEMI_DB=spor_akademi
pg_env=(-e POSTGRES_PASSWORD -e SPOR_AKADEMI_DB -e SPOR_AKADEMI_MIGRATOR_PASSWORD -e SPOR_AKADEMI_APP_PASSWORD)

now_ms() { date +%s%3N; }
seconds_since() { awk -v s="$1" -v e="$(now_ms)" 'BEGIN { printf "%.1f", (e - s) / 1000 }'; }
timings=()
record() { timings+=("$1|$2"); printf 'drill:   %-46s %8s s\n' "$1" "$2"; }

# Runs a step with its output kept aside; prints the output only when the step fails.
quietly() {
  local log="$work_dir/step.log"
  if ! "$@" >"$log" 2>&1; then
    cat "$log" >&2
    echo "error: step failed: $*" >&2
    exit 1
  fi
}

# psql as the superuser inside a drill container (local socket).
sql() { docker exec -i --user postgres "$1" psql -X -A -t -q -v ON_ERROR_STOP=1 -d spor_akademi -c "$2"; }

wait_ready() {
  local container="$1" tries=0
  until docker exec --user postgres "$container" pg_isready -q -h 127.0.0.1 -d spor_akademi >/dev/null 2>&1 \
    && [[ "$(sql "$container" 'SELECT pg_is_in_recovery()' 2>/dev/null)" == "f" ]]; do
    if [[ -z "$(docker ps -q --filter "name=^$container\$")" ]] || ((tries++ > 600)); then
      docker logs --tail 40 "$container" >&2 || true
      echo "error: $container did not become ready." >&2
      exit 1
    fi
    sleep 0.5
  done
}

host_port() { docker port "$1" 5432/tcp | head -n 1 | sed 's/.*://'; }

snapshot() { docker exec -i --user postgres "$1" psql -X -A -t -q -v ON_ERROR_STOP=1 -d spor_akademi <scripts/backup/drill-snapshot.sql >"$2"; }

compare() {
  local expected="$1" actual="$2" label="$3"
  if ! diff -u "$expected" "$actual" >"$work_dir/diff.txt"; then
    head -n 60 "$work_dir/diff.txt" >&2
    echo "error: $label differs from the source (first lines of the diff above)." >&2
    exit 1
  fi
  echo "drill:   $label matches the source: $(grep -c '^table ' "$actual") tables (rows and checksums)," \
    "$(grep -c '^migration ' "$actual") migrations, $(grep -c '^policy ' "$actual") RLS policies," \
    "$(grep -c 'forced=t' "$actual") tables with FORCE RLS, grants and roles"
}

# The app role logs in with its password (over the container's network address, where pg_hba.conf asks for
# scram-sha-256) and reads a tenant table: the password and the grants work, not just exist.
check_app_login() {
  PGPASSWORD="$SPOR_AKADEMI_APP_PASSWORD" docker exec -i --user postgres -e PGPASSWORD "$1" sh -c \
    'psql -X -A -t -q -v ON_ERROR_STOP=1 -h "$(hostname -i)" -U spor_akademi_app -d spor_akademi -c "SELECT count(*) >= 0 FROM tenancy.schools"' \
    >/dev/null
  echo "drill:   ok: the app role logs in with its password and reads tenancy.schools"
}

expect() {
  local actual="$1" wanted="$2" label="$3"
  if [[ "$actual" != "$wanted" ]]; then
    echo "error: $label: expected '$wanted', got '$actual'." >&2
    exit 1
  fi
  echo "drill:   ok: $label"
}

echo "drill: run $run_id, image $image"
if ! docker image ls --digests --format '{{.Repository}}:{{.Tag}}@{{.Digest}}' | grep -qxF "$image"; then
  quietly docker pull "$image"
fi

# ---------------------------------------------------------------- 1. source server with data
echo "drill: 1/6 source server with WAL archiving"
t="$(now_ms)"
for volume in "${volumes[@]}"; do quietly docker volume create "$volume"; done
# The archive and backup volumes belong to the postgres user of the image.
quietly docker run --rm -v "$prefix-wal-archive:/archive" -v "$prefix-backups:/backup" "$image" chown postgres:postgres /archive /backup
quietly docker run -d --name "$src" "${pg_env[@]}" -p 127.0.0.1::5432 \
  -v "$prefix-source-data:/var/lib/postgresql" -v "$prefix-wal-archive:/archive" -v "$prefix-backups:/backup" \
  -v "$initdb_mount" -v "$scripts_mount" \
  "$image" postgres -c wal_level=replica -c archive_mode=on -c archive_timeout=300 \
  -c "archive_command=bash /opt/backup/archive-wal.sh %p %f /archive"
wait_ready "$src"
docker exec -i --user postgres "$src" psql -X -q -v ON_ERROR_STOP=1 -d postgres \
  -v db=spor_akademi -v backup_password="$BACKUP_PASSWORD" <scripts/backup/create-backup-role.sql
record "Source server start" "$(seconds_since "$t")"
src_port="$(host_port "$src")"

t="$(now_ms)"
quietly dotnet build src/SporAkademi.DemoSeed -c Release --nologo
record "Build (DemoSeed, Infrastructure)" "$(seconds_since "$t")"

t="$(now_ms)"
SPOR_AKADEMI_DESIGN_CONNECTION="Host=127.0.0.1;Port=$src_port;Database=spor_akademi;Username=spor_akademi_migrator;Password=$SPOR_AKADEMI_MIGRATOR_PASSWORD" \
  quietly dotnet tool run dotnet-ef database update --no-build --configuration Release \
  --project src/SporAkademi.Infrastructure --startup-project src/SporAkademi.Infrastructure
record "Migrations (migrator role)" "$(seconds_since "$t")"

t="$(now_ms)"
ConnectionStrings__App="Host=127.0.0.1;Port=$src_port;Database=spor_akademi;Username=spor_akademi_app;Password=$SPOR_AKADEMI_APP_PASSWORD" \
  DOTNET_ENVIRONMENT=Development quietly dotnet run --project src/SporAkademi.DemoSeed -c Release --no-build
record "Demo data (DemoSeed, app role)" "$(seconds_since "$t")"

t="$(now_ms)"
sql "$src" "CREATE SCHEMA drill;
  CREATE TABLE drill.filler AS SELECT g AS id, md5(g::text) AS a, md5((g * 7)::text) AS b, now() AS created_at
    FROM generate_series(1, $filler_rows) AS g;
  ALTER TABLE drill.filler ADD PRIMARY KEY (id);
  CREATE TABLE drill.markers (label text PRIMARY KEY, created_at timestamptz NOT NULL DEFAULT clock_timestamp());"
record "Synthetic filler ($filler_rows rows)" "$(seconds_since "$t")"
db_size="$(sql "$src" "SELECT pg_size_pretty(pg_database_size('spor_akademi'))")"
school_id="$(sql "$src" "SELECT id FROM tenancy.schools ORDER BY slug LIMIT 1")"
school_name="$(sql "$src" "SELECT name FROM tenancy.schools WHERE id = '$school_id'")"
echo "drill:   database size $db_size; $(sql "$src" "SELECT count(*) FROM tenancy.schools") schools"

# ---------------------------------------------------------------- 2. backups
echo "drill: 2/6 backups (backup role)"
snapshot "$src" "$work_dir/source-at-backup.txt"
t="$(now_ms)"
dump="$(docker exec --user postgres -e PGUSER=spor_akademi_backup "$src" bash /opt/backup/backup-logical.sh /backup/logical | tail -n 1)"
record "Logical backup (pg_dump -Fc)" "$(seconds_since "$t")"
t="$(now_ms)"
base="$(docker exec --user postgres -e PGUSER=spor_akademi_backup "$src" bash /opt/backup/backup-base.sh /backup/base | tail -n 1)"
record "Base backup (pg_basebackup)" "$(seconds_since "$t")"
dump_size="$(docker exec "$src" du -h "$dump" | cut -f1)"
base_size="$(docker exec "$src" du -sh "$base" | cut -f1)"
echo "drill:   dump $dump_size, base backup $base_size"

# ---------------------------------------------------------------- 3. changes after the backup
echo "drill: 3/6 changes after the backup"
sql "$src" "INSERT INTO drill.markers (label) VALUES ('before-target');
  UPDATE tenancy.schools SET name = name || ' (PITR)' WHERE id = '$school_id';"
snapshot "$src" "$work_dir/source-at-target.txt"
sleep 1
target="$(sql "$src" "SELECT clock_timestamp()")"
sleep 1
sql "$src" "INSERT INTO drill.markers (label) VALUES ('after-target');
  DELETE FROM drill.filler WHERE id % 2 = 0;"
# Closes the current WAL segment and waits until it is archived: everything up to here is recoverable.
last_segment="$(sql "$src" "SELECT pg_walfile_name(pg_switch_wal())")"
tries=0
until [[ "$(sql "$src" "SELECT coalesce(last_archived_wal, '') >= '$last_segment' FROM pg_stat_archiver")" == "t" ]]; do
  ((tries++ < 120)) || { echo "error: WAL segment $last_segment was not archived." >&2; exit 1; }
  sleep 0.5
done
archive_size="$(docker exec "$src" du -sh /archive | cut -f1)"
echo "drill:   target time $target; WAL archive $(docker exec "$src" sh -c 'ls /archive | wc -l') segments, $archive_size"

# ---------------------------------------------------------------- 4. disaster
echo "drill: 4/6 disaster: the source server and its data volume are destroyed"
quietly docker rm -f "$src"
quietly docker volume rm "$prefix-source-data"

# ---------------------------------------------------------------- 5a. restore A: full logical backup
echo "drill: 5/6 restore A: full backup into a new empty server"
rto_start="$(now_ms)"
t="$(now_ms)"
quietly docker run -d --name "$full" "${pg_env[@]}" \
  -v "$prefix-full-data:/var/lib/postgresql" -v "$prefix-backups:/backup:ro" -v "$initdb_mount" -v "$scripts_mount" "$image"
wait_ready "$full"
record "A: new server (initdb, roles, empty database)" "$(seconds_since "$t")"
t="$(now_ms)"
quietly docker exec --user postgres -e PGUSER=postgres "$full" bash /opt/backup/restore-logical.sh "$dump"
record "A: pg_restore" "$(seconds_since "$t")"
t="$(now_ms)"
snapshot "$full" "$work_dir/restore-full.txt"
compare "$work_dir/source-at-backup.txt" "$work_dir/restore-full.txt" "restore A"
check_app_login "$full"
expect "$(sql "$full" "SELECT name FROM tenancy.schools WHERE id = '$school_id'")" "$school_name" "restore A has the school as it was at backup time"
expect "$(sql "$full" "SELECT count(*) FROM drill.markers")" "0" "restore A has no change made after the backup"
record "A: verification" "$(seconds_since "$t")"
record "A: total (disaster to verified)" "$(seconds_since "$rto_start")"

# ---------------------------------------------------------------- 5b. restore B: point in time
echo "drill: 5/6 restore B: base backup + WAL archive to the target time"
rto_start="$(now_ms)"
t="$(now_ms)"
quietly docker run --rm --user postgres -v "$prefix-pitr-data:/var/lib/postgresql" -v "$prefix-backups:/backup:ro" \
  -v "$prefix-wal-archive:/archive:ro" -v "$scripts_mount" "$image" \
  bash /opt/backup/restore-pitr.sh "$base" /archive /var/lib/postgresql/18/docker "$target"
record "B: prepare data directory" "$(seconds_since "$t")"
t="$(now_ms)"
quietly docker run -d --name "$pitr" -e POSTGRES_PASSWORD \
  -v "$prefix-pitr-data:/var/lib/postgresql" -v "$prefix-wal-archive:/archive:ro" "$image"
wait_ready "$pitr"
record "B: WAL replay and promote" "$(seconds_since "$t")"
t="$(now_ms)"
snapshot "$pitr" "$work_dir/restore-pitr.txt"
compare "$work_dir/source-at-target.txt" "$work_dir/restore-pitr.txt" "restore B"
check_app_login "$pitr"
expect "$(sql "$pitr" "SELECT name FROM tenancy.schools WHERE id = '$school_id'")" "$school_name (PITR)" "restore B has change 1 (before the target)"
expect "$(sql "$pitr" "SELECT string_agg(label, ',') FROM drill.markers")" "before-target" "restore B has no change after the target"
expect "$(sql "$pitr" "SELECT count(*) FROM drill.filler")" "$filler_rows" "restore B undid the accidental delete"
record "B: verification" "$(seconds_since "$t")"
record "B: total (disaster to verified)" "$(seconds_since "$rto_start")"

# ---------------------------------------------------------------- 6. report
echo "drill: 6/6 done: both restores match the source."
{
  echo "| Adım | Süre (sn) |"
  echo "|---|---:|"
  for row in "${timings[@]}"; do echo "| ${row%%|*} | ${row#*|} |"; done
  echo
  echo "Veritabanı $db_size, dump $dump_size, taban yedek $base_size, WAL arşivi $archive_size; dolgu $filler_rows satır; $image."
} >"$work_dir/report.md"
if [[ -n "${DRILL_REPORT:-}" ]]; then cp "$work_dir/report.md" "$DRILL_REPORT"; fi
cat "$work_dir/report.md"
