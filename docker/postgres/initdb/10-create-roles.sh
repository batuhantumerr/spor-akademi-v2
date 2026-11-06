#!/bin/sh
# Creates the database and its two roles (ADR-0011):
#   spor_akademi_migrator  owns the database and runs migrations
#   spor_akademi_app       the API's login; CRUD on tables, cannot change the schema
# Runs once, on an empty data directory, before any migration.
set -eu

: "${SPOR_AKADEMI_DB:?}"
: "${SPOR_AKADEMI_MIGRATOR_PASSWORD:?}"
: "${SPOR_AKADEMI_APP_PASSWORD:?}"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
  -v db="$SPOR_AKADEMI_DB" \
  -v migrator_password="$SPOR_AKADEMI_MIGRATOR_PASSWORD" \
  -v app_password="$SPOR_AKADEMI_APP_PASSWORD" <<'SQL'
CREATE ROLE spor_akademi_migrator LOGIN PASSWORD :'migrator_password';
CREATE ROLE spor_akademi_app LOGIN PASSWORD :'app_password';
CREATE DATABASE :"db" OWNER spor_akademi_migrator;
REVOKE ALL ON DATABASE :"db" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"db" TO spor_akademi_app;
\connect :"db"
-- The public schema only holds the migrations history table; the app needs no access to it.
REVOKE ALL ON SCHEMA public FROM PUBLIC;
-- Module schemas and tables are created by the migrator; the app gets usage and CRUD on them.
ALTER DEFAULT PRIVILEGES FOR ROLE spor_akademi_migrator GRANT USAGE ON SCHEMAS TO spor_akademi_app;
ALTER DEFAULT PRIVILEGES FOR ROLE spor_akademi_migrator GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO spor_akademi_app;
ALTER DEFAULT PRIVILEGES FOR ROLE spor_akademi_migrator GRANT USAGE, SELECT ON SEQUENCES TO spor_akademi_app;
SQL
