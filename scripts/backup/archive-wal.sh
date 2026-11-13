#!/usr/bin/env bash
# WAL archiving for point-in-time recovery (P4-09). PostgreSQL's archive_command:
#   archive_command = 'bash /path/to/archive-wal.sh %p %f /path/to/wal-archive'
# Copies one finished WAL segment into the archive directory and reports success only once it is durable:
#   1. copy to a temporary name in the archive directory and fsync that file;
#   2. hard-link it to the final name: link() fails if the name exists, so an archived segment is never
#      overwritten, also when two servers race for the same name (no check-then-rename gap);
#   3. fsync the directory, so the new name survives a crash too.
# A segment that is already there with the same content counts as archived (PostgreSQL retries after a crash);
# a different file with the same name is an error, so a second server can never overwrite another's archive.
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

already_archived() {
  if cmp -s "$source_path" "$target"; then
    return 0
  fi
  echo "error: $file_name is already archived with different content." >&2
  exit 1
}

if [[ -e "$target" ]]; then
  already_archived
  exit 0
fi

umask 077
partial="$archive_dir/.$file_name.$$.partial"
trap 'rm -f "$partial"' EXIT
cp "$source_path" "$partial"
sync "$partial"
if ! ln "$partial" "$target" 2>/dev/null; then
  # Another archiver linked the name first (or linking failed): the existing file must be this segment.
  [[ -e "$target" ]] || { echo "error: could not link $file_name into the archive." >&2; exit 1; }
  already_archived
  exit 0
fi
rm -f "$partial"
sync "$archive_dir"
