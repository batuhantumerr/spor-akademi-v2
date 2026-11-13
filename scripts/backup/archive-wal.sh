#!/usr/bin/env bash
# WAL archiving for point-in-time recovery (P4-09). PostgreSQL's archive_command:
#   archive_command = 'bash /path/to/archive-wal.sh %p %f /path/to/wal-archive'
# Copies one finished WAL segment into the archive directory. A segment that is already there with the same
# content counts as archived (PostgreSQL may retry after a crash); a different file with the same name is an
# error, so a second server can never overwrite another server's archive. The copy is written under a temporary
# name and renamed, so a half-written segment never carries the final name.
# Off-site copy (encrypted, Deploy block) is a separate step that reads the finished archive directory.
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "usage: archive-wal.sh <%p> <%f> <archive-dir>" >&2
  exit 2
fi
source_path="$1"
file_name="$2"
archive_dir="$3"
target="$archive_dir/$file_name"

if [[ -f "$target" ]]; then
  if cmp -s "$source_path" "$target"; then
    exit 0
  fi
  echo "error: $file_name is already archived with different content." >&2
  exit 1
fi

umask 077
cp "$source_path" "$archive_dir/.$file_name.partial"
mv "$archive_dir/.$file_name.partial" "$target"
