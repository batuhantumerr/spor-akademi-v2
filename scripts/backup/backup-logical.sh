#!/usr/bin/env bash
# Full logical backup of one database (P4-09): pg_dump in custom format (-Fc), checked with pg_restore --list,
# with a SHA-256 file next to it.
#
# Usage: backup-logical.sh <output-dir>
# Connection: the standard libpq variables (PGHOST, PGPORT, PGUSER, PGDATABASE). PGUSER defaults to
# spor_akademi_backup (create-backup-role.sql) and PGDATABASE to spor_akademi. The password comes from a
# .pgpass / PGPASSFILE, never from the command line.
# Row level security: pg_dump keeps row_security=off, so a role that would see only part of a table makes the
# dump fail instead of producing an incomplete one. The backup role has BYPASSRLS for that reason (DEC-571).
# BACKUP_POST_HOOK: an optional executable called with the finished dump path, e.g. to encrypt it and copy it
# off the server. Encryption, off-site copies and retention belong to the Deploy block.
# Prints the dump path on the last line of output.
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: backup-logical.sh <output-dir>" >&2
  exit 2
fi
out_dir="$1"
export PGUSER="${PGUSER:-spor_akademi_backup}"
export PGDATABASE="${PGDATABASE:-spor_akademi}"

umask 077
mkdir -p "$out_dir"
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
name="$PGDATABASE-$stamp.dump"
partial="$out_dir/.$name.partial"
trap 'rm -f "$partial"' EXIT

pg_dump --format=custom --no-password --file="$partial"
# A readable table of contents: the archive is complete and not truncated.
pg_restore --list "$partial" >/dev/null
mv "$partial" "$out_dir/$name"
(cd "$out_dir" && sha256sum "$name" >"$name.sha256")

if [[ -n "${BACKUP_POST_HOOK:-}" ]]; then
  "$BACKUP_POST_HOOK" "$out_dir/$name"
fi
echo "$out_dir/$name"
