#!/usr/bin/env bash
# Creates .env with fresh random secrets. Never overwrites an existing .env: it only appends keys added
# since it was created (the optional Valkey cache, DEC-316, and Mailpit ports, DEC-357); existing values are neither
# changed nor printed.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
umask 077
if [[ -e "$ROOT/.env" ]]; then
  added=0
  # A last line without a newline would otherwise be joined with the first appended key.
  if [[ -s "$ROOT/.env" && -n "$(tail -c 1 "$ROOT/.env")" ]]; then echo >> "$ROOT/.env"; fi
  if ! grep -q '^VALKEY_PASSWORD=' "$ROOT/.env"; then
    echo "VALKEY_PASSWORD=$(openssl rand -hex 24)" >> "$ROOT/.env"
    added=$((added + 1))
  fi
  if ! grep -q '^VALKEY_HOST_PORT=' "$ROOT/.env"; then
    echo "VALKEY_HOST_PORT=56379" >> "$ROOT/.env"
    added=$((added + 1))
  fi
  if ! grep -q '^MAILPIT_SMTP_HOST_PORT=' "$ROOT/.env"; then
    echo "MAILPIT_SMTP_HOST_PORT=51025" >> "$ROOT/.env"
    added=$((added + 1))
  fi
  if ! grep -q '^MAILPIT_UI_HOST_PORT=' "$ROOT/.env"; then
    echo "MAILPIT_UI_HOST_PORT=58025" >> "$ROOT/.env"
    added=$((added + 1))
  fi
  if [[ $added -eq 0 ]]; then
    echo ".env already exists and has every key; nothing changed (remove it first if you want new secrets)."
  else
    echo ".env already exists; appended $added missing key(s) (Valkey cache, Mailpit ports). Existing values are unchanged."
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
  echo "MAILPIT_SMTP_HOST_PORT=51025"
  echo "MAILPIT_UI_HOST_PORT=58025"
  echo "POSTGRES_HOST_PORT=55432"
  echo "SEQ_HOST_PORT=5341"
  echo "PLATFORM_ADMIN_EMAIL=platform@spor-akademi.local"
  echo "PLATFORM_ADMIN_PASSWORD=$platform_admin_password"
} > "$ROOT/.env"
echo "created .env with random secrets."
echo
echo "First Platform Admin (created when the API starts and no Platform Admin exists):"
echo "  e-mail:   platform@spor-akademi.local"
echo "  password: $platform_admin_password"
echo "This is shown only once. You must change it at the first login."
