-- The two backup logins (P4-09, DEC-571). Run as a superuser, once per server and again to rotate the passwords
-- (idempotent: missing roles are created, the attributes and passwords are set every time):
--   SPOR_AKADEMI_BACKUP_DUMP_PASSWORD=... SPOR_AKADEMI_BACKUP_REPLICATION_PASSWORD=... \
--     psql -X -v ON_ERROR_STOP=1 -U postgres -d postgres -f create-backup-role.sql
-- The passwords come from the environment (\getenv), never from the command line. Statement logging is turned off
-- for this session, so the plain-text password in ALTER ROLE does not reach the server log either; the server
-- stores only the SCRAM-SHA-256 verifier. The database name defaults to spor_akademi (-v db=... to change it).
--
-- spor_akademi_backup_dump (pg_dump, backup-logical.sh): read-only. pg_read_all_data gives SELECT on every table
--   and sequence and USAGE on every schema, nothing else. BYPASSRLS: tenant tables have FORCE ROW LEVEL SECURITY
--   (P4-04); pg_dump runs with row_security=off and stops with an error instead of writing a dump that silently
--   lacks rows. The role cannot write, so bypassing the read policies opens nothing the dump does not contain.
-- spor_akademi_backup_replication (pg_basebackup, backup-base.sh): REPLICATION only; no SQL access to the data and
--   no BYPASSRLS. pg_hba.conf must allow it a replication connection (and nothing else).
\set QUIET on
\getenv dump_password SPOR_AKADEMI_BACKUP_DUMP_PASSWORD
\getenv replication_password SPOR_AKADEMI_BACKUP_REPLICATION_PASSWORD
\if :{?db}
\else
  \set db spor_akademi
\endif
-- Unset and empty both stop the script (exit 3): an empty password would leave the role without one. Checked in the
-- client shell, so the password is never sent to the server just to be tested.
\set dump_password_set `[ -n "$SPOR_AKADEMI_BACKUP_DUMP_PASSWORD" ] && echo true || echo false`
\set replication_password_set `[ -n "$SPOR_AKADEMI_BACKUP_REPLICATION_PASSWORD" ] && echo true || echo false`
\if :dump_password_set
\else
  DO $$ BEGIN RAISE EXCEPTION 'SPOR_AKADEMI_BACKUP_DUMP_PASSWORD is not set or empty'; END $$;
\endif
\if :replication_password_set
\else
  DO $$ BEGIN RAISE EXCEPTION 'SPOR_AKADEMI_BACKUP_REPLICATION_PASSWORD is not set or empty'; END $$;
\endif

SET client_min_messages = 'warning';
SET log_statement = 'none';
SET log_min_error_statement = 'panic';
SET log_min_duration_statement = -1;
SET log_duration = off;
SET password_encryption = 'scram-sha-256';

SELECT format('CREATE ROLE %I', role_name)
FROM unnest(ARRAY['spor_akademi_backup_dump', 'spor_akademi_backup_replication']) AS role_name
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = role_name)
\gexec

ALTER ROLE spor_akademi_backup_dump LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION BYPASSRLS
  PASSWORD :'dump_password';
ALTER ROLE spor_akademi_backup_replication LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE REPLICATION NOBYPASSRLS
  PASSWORD :'replication_password';
GRANT pg_read_all_data TO spor_akademi_backup_dump;
-- The database revokes CONNECT from PUBLIC (10-create-roles.sh).
GRANT CONNECT ON DATABASE :"db" TO spor_akademi_backup_dump;
