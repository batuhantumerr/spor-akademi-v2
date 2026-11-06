#!/usr/bin/env bash
# The only supported way to run dotnet-ef. Always connects as spor_akademi_migrator (ADR-0011):
# running migrations as postgres would leave tables the app role cannot access.
#
# Usage: scripts/ef.sh migrations add <Name> | migrations list | migrations remove
#        scripts/ef.sh migrations script [args] | migrations has-pending-model-changes
#        scripts/ef.sh database update [<Migration>]
#        scripts/ef.sh --check-env
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib/env.sh"

for arg in "$@"; do
  case "$arg" in
    --verbose|-v|--connection|--connection=*)
      echo "error: $arg is not allowed (it can print or override the connection string)." >&2
      exit 2 ;;
  esac
done

if [[ "${1:-}" == "--check-env" ]]; then
  load_env
  require_env SPOR_AKADEMI_MIGRATOR_PASSWORD
  echo "ok: .env provides the migrator credentials."
  exit 0
fi

case "${1:-} ${2:-}" in
  "migrations add"|"migrations list"|"migrations remove"|"migrations script"|"migrations has-pending-model-changes"|"database update") ;;
  *)
    echo "error: unsupported command. Allowed: migrations add|list|remove|script|has-pending-model-changes, database update." >&2
    exit 2 ;;
esac

needs_db=false
case "${1:-} ${2:-}" in
  "migrations list"|"database update") needs_db=true ;;
esac

if $needs_db; then
  load_env
  require_env SPOR_AKADEMI_MIGRATOR_PASSWORD
  SPOR_AKADEMI_DESIGN_CONNECTION="$(connection_string spor_akademi_migrator SPOR_AKADEMI_MIGRATOR_PASSWORD)"
  export SPOR_AKADEMI_DESIGN_CONNECTION
fi

cd "$REPO_ROOT"
exec dotnet tool run dotnet-ef "$@" \
  --project src/SporAkademi.Infrastructure \
  --startup-project src/SporAkademi.Infrastructure
