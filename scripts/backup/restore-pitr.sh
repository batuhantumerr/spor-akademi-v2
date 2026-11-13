#!/usr/bin/env bash
# Prepares a data directory for point-in-time recovery (P4-09) from a backup-base.sh backup and the WAL archive.
# Starting PostgreSQL on the directory then replays the archive up to the target time and promotes the server.
#
# Usage: restore-pitr.sh <base-backup-dir> <wal-archive-dir> <pgdata> [<target-time>]
#   <target-time>  e.g. "2026-11-13 10:15:00+03"; recovery stops before the first commit after it. Without it,
#                  recovery replays every archived segment (the latest state the archive holds).
# Run as the operating system user that runs PostgreSQL, with that server stopped. <pgdata> must be empty or
# absent: the script never writes over an existing cluster. The restored server keeps the original roles,
# passwords, owners, grants and row level security; it is a copy of the whole cluster, not one database.
set -euo pipefail

if [[ $# -lt 3 || $# -gt 4 ]]; then
  echo "usage: restore-pitr.sh <base-backup-dir> <wal-archive-dir> <pgdata> [<target-time>]" >&2
  exit 2
fi
base_dir="$1"
archive_dir="$2"
pgdata="$3"
target="${4:-}"

for file in base.tar.gz pg_wal.tar.gz; do
  if [[ ! -f "$base_dir/$file" ]]; then
    echo "error: $base_dir/$file not found (expected a backup-base.sh directory)." >&2
    exit 1
  fi
done
if [[ ! -d "$archive_dir" ]]; then
  echo "error: WAL archive $archive_dir not found." >&2
  exit 1
fi
# Both values end up in postgresql.auto.conf between single quotes.
if [[ "$archive_dir" == *"'"* || "$archive_dir" == *'"'* ]]; then
  echo "error: the archive path may not contain quotes." >&2
  exit 1
fi
if [[ -n "$target" && ! "$target" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}[\ T][0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?([+-][0-9]{2}(:?[0-9]{2})?|Z)?$ ]]; then
  echo "error: target time must look like 2026-11-13 10:15:00+03." >&2
  exit 1
fi
if [[ -d "$pgdata" && -n "$(ls -A "$pgdata")" ]]; then
  echo "error: $pgdata is not empty; recovery only goes into an empty data directory." >&2
  exit 1
fi

umask 077
mkdir -p "$pgdata"
chmod 700 "$pgdata"
tar -xzf "$base_dir/base.tar.gz" -C "$pgdata"
mkdir -p "$pgdata/pg_wal"
tar -xzf "$base_dir/pg_wal.tar.gz" -C "$pgdata/pg_wal"

{
  echo "# Point-in-time recovery (scripts/backup/restore-pitr.sh, $(date -u +%Y-%m-%dT%H:%M:%SZ))"
  echo "restore_command = 'cp \"$archive_dir/%f\" \"%p\"'"
  if [[ -n "$target" ]]; then
    echo "recovery_target_time = '$target'"
  fi
  echo "recovery_target_action = 'promote'"
} >>"$pgdata/postgresql.auto.conf"
touch "$pgdata/recovery.signal"
