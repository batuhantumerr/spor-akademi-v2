#!/usr/bin/env bash
# Physical base backup for point-in-time recovery (P4-09): pg_basebackup as compressed tar with the WAL needed
# to make it consistent (streamed), and a backup manifest checked with pg_verifybackup. Together with the WAL
# archive (archive-wal.sh) it can be rolled forward to any moment after the backup ended.
#
# Usage: backup-base.sh <output-dir>
# Connection: the standard libpq variables. PGUSER defaults to spor_akademi_backup, which has REPLICATION
# (create-backup-role.sql); pg_hba.conf must allow it a replication connection. The password comes from a
# .pgpass / PGPASSFILE. Row level security does not apply: files are copied, not rows.
# BACKUP_POST_HOOK: as in backup-logical.sh, called with the finished backup directory.
# Prints the backup directory on the last line of output.
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: backup-base.sh <output-dir>" >&2
  exit 2
fi
out_dir="$1"
export PGUSER="${PGUSER:-spor_akademi_backup}"

umask 077
mkdir -p "$out_dir"
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
name="base-$stamp"
partial="$out_dir/.$name.partial"
trap 'rm -rf "$partial"' EXIT

pg_basebackup --pgdata="$partial" --format=tar --gzip --wal-method=stream --checkpoint=fast \
  --manifest-checksums=SHA256 --label="spor-akademi $stamp" --no-password
# Every file against the manifest's checksums. The streamed WAL is in a compressed tar, which
# pg_verifybackup cannot parse; the restore replays it and stops with an error if anything is missing.
pg_verifybackup --no-parse-wal --quiet "$partial"
mv "$partial" "$out_dir/$name"

if [[ -n "${BACKUP_POST_HOOK:-}" ]]; then
  "$BACKUP_POST_HOOK" "$out_dir/$name"
fi
echo "$out_dir/$name"
