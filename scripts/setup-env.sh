#!/usr/bin/env bash
# Creates .env with fresh random secrets. Never overwrites an existing .env: it only appends keys added
# since it was created (today the optional Valkey cache, DEC-316, and document storage, DEC-326); existing values are
# neither changed nor printed.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
umask 077
if [[ -e "$ROOT/.env" ]]; then
  added=0
  # A last line without a newline would otherwise be joined with the first appended key.
  if [[ -s "$ROOT/.env" && -n "$(tail -c 1 "$ROOT/.env")" ]]; then echo >> "$ROOT/.env"; fi
  append_missing() {
    if ! grep -q "^$1=" "$ROOT/.env"; then
      echo "$1=$2" >> "$ROOT/.env"
      added=$((added + 1))
    fi
  }
  append_missing VALKEY_PASSWORD "$(openssl rand -hex 24)"
  append_missing VALKEY_HOST_PORT 56379
  append_missing STORAGE_ACCESS_KEY "$(openssl rand -hex 10)"
  append_missing STORAGE_SECRET_KEY "$(openssl rand -hex 20)"
  append_missing STORAGE_HOST_PORT 58333
  append_missing STORAGE_BUCKET spor-akademi-documents
  if [[ $added -eq 0 ]]; then
    echo ".env already exists and has every key; nothing changed (remove it first if you want new secrets)."
  else
    echo ".env already exists; appended $added missing key(s) (Valkey cache, document storage). Existing values are unchanged."
  fi
  exit 0
fi
platform_admin_password="$(openssl rand -hex 16)"
{
  echo "POSTGRES_PASSWORD=$(openssl rand -hex 24)"
  echo "SPOR_AKADEMI_MIGRATOR_PASSWORD=$(openssl rand -hex 24)"
  echo "SPOR_AKADEMI_APP_PASSWORD=$(openssl rand -hex 24)"
  echo "JWT_SIGNING_KEY=$(openssl rand -hex 32)"
  echo "VALKEY_PASSWORD=$(openssl rand -hex 24)"
  echo "VALKEY_HOST_PORT=56379"
  echo "POSTGRES_HOST_PORT=55432"
  echo "SEQ_HOST_PORT=5341"
  echo "STORAGE_ACCESS_KEY=$(openssl rand -hex 10)"
  echo "STORAGE_SECRET_KEY=$(openssl rand -hex 20)"
  echo "STORAGE_HOST_PORT=58333"
  echo "STORAGE_BUCKET=spor-akademi-documents"
  echo "PLATFORM_ADMIN_EMAIL=platform@spor-akademi.local"
  echo "PLATFORM_ADMIN_PASSWORD=$platform_admin_password"
} > "$ROOT/.env"
echo "created .env with random secrets."
echo
echo "First Platform Admin (created when the API starts and no Platform Admin exists):"
echo "  e-mail:   platform@spor-akademi.local"
echo "  password: $platform_admin_password"
echo "This is shown only once. You must change it at the first login."
