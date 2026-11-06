#!/usr/bin/env bash
# Loads .env as data: each KEY=VALUE line is validated and exported; nothing is executed
# and no value is ever printed. Source it, then call load_env.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

load_env() {
  local file="$REPO_ROOT/.env"
  if [[ ! -f "$file" ]]; then
    echo "error: .env not found. Copy .env.example to .env and fill it in (see README)." >&2
    return 1
  fi
  local line key value
  while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line%$'\r'}"
    [[ -z "$line" || "$line" == \#* ]] && continue
    if [[ ! "$line" =~ ^([A-Z][A-Z0-9_]*)=([A-Za-z0-9._-]*)$ ]]; then
      echo "error: .env has a malformed line (values may only contain letters, digits, '.', '_' and '-')." >&2
      return 1
    fi
    key="${BASH_REMATCH[1]}"
    value="${BASH_REMATCH[2]}"
    export "$key=$value"
  done < "$file"
}

require_env() {
  local name
  for name in "$@"; do
    if [[ -z "${!name:-}" || "${!name}" == change-me* ]]; then
      echo "error: $name is not set in .env." >&2
      return 1
    fi
  done
}

connection_string() {
  # $1 = role name, $2 = password variable name
  local port="${POSTGRES_HOST_PORT:-55432}"
  printf 'Host=127.0.0.1;Port=%s;Database=spor_akademi;Username=%s;Password=%s' "$port" "$1" "${!2}"
}
