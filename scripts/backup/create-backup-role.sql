-- The backup login (P4-09, DEC-571). Run once per server as a superuser:
--   psql -v ON_ERROR_STOP=1 -U postgres -d postgres -v db=spor_akademi -v backup_password="..." -f create-backup-role.sql
--
-- Read-only: pg_read_all_data gives SELECT on every table and sequence and USAGE on every schema, nothing else.
-- BYPASSRLS: tenant tables have FORCE ROW LEVEL SECURITY (P4-04). pg_dump runs with row_security=off and stops
-- with an error instead of writing a dump that silently lacks rows; with BYPASSRLS it sees every row. The role
-- cannot write, so bypassing the read policies opens nothing the dump itself does not already contain.
-- REPLICATION: pg_basebackup (physical base backup for point-in-time recovery) needs a replication connection.
CREATE ROLE spor_akademi_backup LOGIN REPLICATION BYPASSRLS PASSWORD :'backup_password';
GRANT pg_read_all_data TO spor_akademi_backup;
-- The database revokes CONNECT from PUBLIC (10-create-roles.sh).
GRANT CONNECT ON DATABASE :"db" TO spor_akademi_backup;
