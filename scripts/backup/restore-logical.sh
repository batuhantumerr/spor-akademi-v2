#!/usr/bin/env bash
# Restores a backup-logical.sh dump into an empty database (P4-09), in one transaction: it either restores
# completely or leaves the database as it was.
#
# Usage: restore-logical.sh <dump-file>
# Connection: the standard libpq variables. PGUSER defaults to postgres and must be a superuser: the dump keeps
# the owners and grants (migrator owns the tables, the app role gets CRUD) and the tables have FORCE ROW LEVEL
# SECURITY (P4-04), which only a superuser passes while loading every school's rows. PGDATABASE defaults to
# spor_akademi.
# The dump holds one database only: no roles, no passwords, no database-level grants (cluster objects). The target
# must be a fresh server prepared like docker/postgres/initdb/10-create-roles.sh: the roles (with their passwords
# from the secret store) and the empty database with its owner and CONNECT grant exist. The script refuses a
# database that already has tables, so it never merges into or overwrites live data.
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: restore-logical.sh <dump-file>" >&2
  exit 2
fi
dump="$1"
export PGUSER="${PGUSER:-postgres}"
export PGDATABASE="${PGDATABASE:-spor_akademi}"

if [[ ! -f "$dump" ]]; then
  echo "error: $dump not found." >&2
  exit 1
fi
if [[ -f "$dump.sha256" ]]; then
  (cd "$(dirname "$dump")" && sha256sum -c "$(basename "$dump").sha256" >/dev/null) || {
    echo "error: $dump does not match its SHA-256 file." >&2
    exit 1
  }
fi

query() { psql -X -A -t -q --no-password -v ON_ERROR_STOP=1 -c "$1"; }

if [[ "$(query "SELECT rolsuper FROM pg_roles WHERE rolname = current_user")" != "t" ]]; then
  echo "error: $PGUSER is not a superuser." >&2
  exit 1
fi
# Empty means: no user schema besides public, and no relation, function or type of its own in any schema
# (tables, views, sequences, indexes; extension members excluded).
objects="$(query "
  SELECT (SELECT count(*) FROM pg_namespace
          WHERE nspname NOT IN ('public', 'information_schema') AND nspname NOT LIKE 'pg\_%')
       + (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
          WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg\_%'
            AND NOT EXISTS (SELECT FROM pg_depend d WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e'))
       + (SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
          WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg\_%'
            AND NOT EXISTS (SELECT FROM pg_depend d WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e'))")"
if [[ "$objects" != "0" ]]; then
  echo "error: database $PGDATABASE is not empty ($objects schemas, relations or functions); restore only into an empty database." >&2
  exit 1
fi

pg_restore --exit-on-error --single-transaction --no-password --dbname="$PGDATABASE" "$dump"
# Planner statistics are not part of a dump.
vacuumdb --analyze-only --no-password --quiet --dbname="$PGDATABASE"
