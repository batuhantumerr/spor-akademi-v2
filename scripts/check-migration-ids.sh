#!/usr/bin/env bash
# ADR-0011 (V1-02, DEC-680): the migrator image ships an efbundle (what runs) and an idempotent SQL script (what is
# reviewed), both built from the same source. This fails when their migration IDs differ from the migration files.
#
# Usage: scripts/check-migration-ids.sh <migrations.sql> [applied-ids.txt]
#   <migrations.sql>   idempotent script from the migrator image (/migrator/migrations.sql)
#   [applied-ids.txt]  one MigrationId per line, read from __EFMigrationsHistory after the bundle ran on a fresh database
set -euo pipefail

sql_file="${1:?usage: check-migration-ids.sh <migrations.sql> [applied-ids.txt]}"
applied_file="${2:-}"
if [ -n "${CI:-}" ] && [ -z "$applied_file" ]; then
  echo "In CI the applied-ids file is required (the bundle check must not be skipped)." >&2
  exit 1
fi
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
migrations_dir="$root/src/SporAkademi.Infrastructure/Migrations"

# <timestamp>_<Name>.cs, without the .Designer.cs companions and the model snapshot.
files_ids=$(find "$migrations_dir" -maxdepth 1 -name '[0-9]*_*.cs' ! -name '*.Designer.cs' -printf '%f\n' | sed 's/\.cs$//' | LC_ALL=C sort)
sql_ids=$(grep -oE "VALUES \('[0-9]{14}_[A-Za-z0-9_]+'" "$sql_file" | sed -E "s/VALUES \('//; s/'$//" | LC_ALL=C sort -u || true)

fail=0
compare() {
  local label="$1" expected="$2" actual="$3"
  if ! diff <(printf '%s\n' "$expected") <(printf '%s\n' "$actual") >/dev/null; then
    echo "Migration IDs differ: migration files (<) vs $label (>)" >&2
    diff <(printf '%s\n' "$expected") <(printf '%s\n' "$actual") >&2 || true
    fail=1
  fi
}

[ -n "$files_ids" ] || { echo "No migration files found in $migrations_dir" >&2; exit 1; }
[ -n "$sql_ids" ] || { echo "No migration IDs found in $sql_file" >&2; exit 1; }
compare "idempotent SQL script" "$files_ids" "$sql_ids"
if [ -n "$applied_file" ]; then
  compare "bundle (applied history)" "$files_ids" "$(LC_ALL=C sort -u "$applied_file" | sed '/^$/d')"
fi

[ "$fail" -eq 0 ] || exit 1
echo "Migration IDs consistent: $(printf '%s\n' "$files_ids" | wc -l | tr -d ' ') files, SQL script${applied_file:+, bundle}."
