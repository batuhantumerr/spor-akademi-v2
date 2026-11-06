#!/usr/bin/env bash
# Creates .env with fresh random secrets. Refuses to overwrite an existing .env.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ -e "$ROOT/.env" ]]; then
  echo "error: .env already exists; remove it first if you want new secrets." >&2
  exit 1
fi
umask 077
{
  echo "POSTGRES_PASSWORD=$(openssl rand -hex 24)"
  echo "SPOR_AKADEMI_MIGRATOR_PASSWORD=$(openssl rand -hex 24)"
  echo "SPOR_AKADEMI_APP_PASSWORD=$(openssl rand -hex 24)"
  echo "JWT_SIGNING_KEY=$(openssl rand -hex 32)"
  echo "POSTGRES_HOST_PORT=55432"
  echo "SEQ_HOST_PORT=5341"
} > "$ROOT/.env"
echo "created .env with random secrets."
