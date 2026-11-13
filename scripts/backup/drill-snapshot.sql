-- What the restore drill compares between the source and a restored server (P4-09). Run as a superuser, so row
-- level security hides no rows. One line per fact, sorted, so two snapshots are compared with diff.

-- Every table: row count and a checksum of its content.
SELECT format('table %I.%I rows=%s md5=%s', schemaname, tablename,
         (xpath('/row/c/text()', query_to_xml(format('SELECT count(*) AS c FROM %I.%I', schemaname, tablename), false, true, '')))[1]::text,
         (xpath('/row/h/text()', query_to_xml(format(
           'SELECT md5(coalesce(string_agg(t::text, E''\n'' ORDER BY t::text), '''')) AS h FROM %I.%I t', schemaname, tablename),
           false, true, '')))[1]::text)
FROM pg_tables
WHERE schemaname NOT IN ('pg_catalog', 'information_schema')
ORDER BY 1;

-- Applied EF Core migrations.
SELECT format('migration %s', migration_id) FROM public.__ef_migrations_history ORDER BY 1;

-- Owners, row level security flags and table grants (ACL) of every table.
SELECT format('relation %I.%I owner=%s rls=%s forced=%s acl=%s', n.nspname, c.relname, pg_get_userbyid(c.relowner),
         c.relrowsecurity, c.relforcerowsecurity, coalesce(c.relacl::text, 'default'))
FROM pg_class c
JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE c.relkind IN ('r', 'p', 'v', 'm', 'S')
  AND n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg_toast%'
ORDER BY 1;

-- Row level security policies.
SELECT format('policy %I.%I %s %s roles=%s using=%s check=%s', schemaname, tablename, policyname, cmd, roles::text,
         coalesce(qual, ''), coalesce(with_check, ''))
FROM pg_policies
ORDER BY 1;

-- Schemas with owner and grants, and the default privileges new tables get.
SELECT format('schema %I owner=%s acl=%s', nspname, pg_get_userbyid(nspowner), coalesce(nspacl::text, 'default'))
FROM pg_namespace
WHERE nspname NOT IN ('pg_catalog', 'information_schema') AND nspname NOT LIKE 'pg_t%'
ORDER BY 1;
SELECT format('default-acl %s %s %s', pg_get_userbyid(defaclrole), defaclobjtype, defaclacl::text) FROM pg_default_acl ORDER BY 1;

-- Functions (security definer flag and body), triggers and indexes.
SELECT format('function %s secdef=%s md5=%s', p.oid::regprocedure, p.prosecdef, md5(p.prosrc))
FROM pg_proc p
JOIN pg_namespace n ON n.oid = p.pronamespace
WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
ORDER BY 1;
SELECT format('trigger %s %s', tgrelid::regclass, tgname) FROM pg_trigger WHERE NOT tgisinternal ORDER BY 1;
SELECT format('index %I.%I', schemaname, indexname) FROM pg_indexes WHERE schemaname NOT IN ('pg_catalog', 'information_schema') ORDER BY 1;

-- The application roles keep their attributes: the app role must never become superuser or bypass RLS.
SELECT format('role %s super=%s bypassrls=%s login=%s', rolname, rolsuper, rolbypassrls, rolcanlogin)
FROM pg_roles
WHERE rolname IN ('spor_akademi_app', 'spor_akademi_migrator')
ORDER BY 1;
