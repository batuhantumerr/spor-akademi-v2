using Microsoft.EntityFrameworkCore.Migrations;
using SporAkademi.Infrastructure.Persistence;

#nullable disable

namespace SporAkademi.Infrastructure.Migrations
{
    /// <summary>
    /// P4-04 (DEC-520 – DEC-523): PostgreSQL row level security, the database's own tenant isolation layer under the
    /// query filters and the write guard (ADR-0003). Every school table is enabled and forced (the owner too); the
    /// platform tables also open for deliberate work without a school; two <c>SECURITY DEFINER</c> functions are the
    /// only reads across schools (the payment webhook's school, the billing-close health check).
    /// </summary>
    public partial class AddRowLevelSecurity : Migration
    {
        private static readonly (string Schema, string Table)[] TenantTables =
        [
            ("announcements", "announcements"),
            ("attendance", "attendance_changes"),
            ("attendance", "attendance_records"),
            ("attendance", "sessions"),
            ("billing", "billing_rules"),
            ("billing", "charge_snapshots"),
            ("billing", "closed_periods"),
            ("billing", "dues_reminders"),
            ("billing", "online_payments"),
            ("billing", "payment_notifications"),
            ("billing", "payment_refunds"),
            ("billing", "payments"),
            ("enrollments", "enrollments"),
            ("groups", "groups"),
            ("organization", "facilities"),
            ("organization", "sports"),
            ("students", "anonymization_runs"),
            ("students", "guardians"),
            ("students", "student_documents"),
            ("students", "student_guardians"),
            ("students", "students"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The session settings written by TenantSessionInterceptor. Unset or empty means "no school" (NULL), so a
            // connection that never set them sees no school rows and is not in platform scope: it fails closed.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION tenancy.current_tenant_id() RETURNS uuid
                    LANGUAGE sql STABLE PARALLEL SAFE
                    AS $$ SELECT NULLIF(current_setting('app.tenant_id', true), '')::uuid $$;

                CREATE FUNCTION tenancy.is_platform_scope() RETURNS boolean
                    LANGUAGE sql STABLE PARALLEL SAFE
                    AS $$ SELECT coalesce(current_setting('app.platform', true) = 'on', false)
                                 AND NULLIF(current_setting('app.tenant_id', true), '') IS NULL $$;

                CREATE FUNCTION tenancy.is_maintenance() RETURNS boolean
                    LANGUAGE sql STABLE PARALLEL SAFE
                    AS $$ SELECT coalesce(current_setting('app.maintenance', true) = 'on', false) $$;
                """);

            foreach (var (schema, table) in TenantTables)
            {
                migrationBuilder.Sql(RowLevelSecurity.ForTenantTable(schema, table));
            }

            migrationBuilder.Sql(RowLevelSecurity.ForPlatformTable("identity", "users"));
            migrationBuilder.Sql(RowLevelSecurity.ForPlatformTable("outbox", "messages"));
            migrationBuilder.Sql(RowLevelSecurity.ForPlatformTable("tenancy", "schools", tenantColumn: "id"));

            // Tables without a school column follow their parent row (DEC-527).
            migrationBuilder.Sql(RowLevelSecurity.ForOwnedTable("groups", "group_fees", "groups.groups", "group_id"));
            migrationBuilder.Sql(RowLevelSecurity.ForOwnedTable("identity", "refresh_tokens", "identity.users", "user_id"));
            migrationBuilder.Sql(RowLevelSecurity.ForOwnedTable("identity", "password_tokens", "identity.users", "user_id"));

            // The only reads across schools. They run as the owner (the migrator) and turn the maintenance switch on
            // only around their one query, and return what their caller needs and nothing else. The switch is
            // transaction-local and opens nothing for the application role (the maintenance policy is the owner's).
            // The application role may call them; nobody else may.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION billing.find_checkout_target(p_provider text, p_token text)
                    RETURNS TABLE (school_id uuid, online_payment_id uuid)
                    LANGUAGE plpgsql VOLATILE SECURITY DEFINER
                    SET search_path = pg_catalog, pg_temp
                    AS $$
                    DECLARE previous text := current_setting('app.maintenance', true);
                    BEGIN
                        PERFORM set_config('app.maintenance', 'on', true);
                        RETURN QUERY SELECT p.tenant_id, p.id FROM billing.online_payments p
                                     WHERE p.provider = p_provider AND p.provider_token = p_token;
                        PERFORM set_config('app.maintenance', coalesce(previous, ''), true);
                    END $$;
                REVOKE ALL ON FUNCTION billing.find_checkout_target(text, text) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION billing.find_checkout_target(text, text) TO spor_akademi_app;

                CREATE FUNCTION billing.count_schools_with_open_period(p_period text)
                    RETURNS integer
                    LANGUAGE plpgsql VOLATILE SECURITY DEFINER
                    SET search_path = pg_catalog, pg_temp
                    AS $$
                    DECLARE
                        previous text := current_setting('app.maintenance', true);
                        open_schools integer;
                    BEGIN
                        PERFORM set_config('app.maintenance', 'on', true);
                        SELECT count(DISTINCT r.tenant_id)::int INTO open_schools FROM billing.billing_rules r
                        WHERE r.effective_from <= p_period
                          AND NOT EXISTS (SELECT 1 FROM billing.closed_periods c
                                          WHERE c.tenant_id = r.tenant_id AND c.period = p_period);
                        PERFORM set_config('app.maintenance', coalesce(previous, ''), true);
                        RETURN open_schools;
                    END $$;
                REVOKE ALL ON FUNCTION billing.count_schools_with_open_period(text) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION billing.count_schools_with_open_period(text) TO spor_akademi_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP FUNCTION IF EXISTS billing.count_schools_with_open_period(text);
                DROP FUNCTION IF EXISTS billing.find_checkout_target(text, text);
                """);

            migrationBuilder.Sql(RowLevelSecurity.Drop("identity", "password_tokens"));
            migrationBuilder.Sql(RowLevelSecurity.Drop("identity", "refresh_tokens"));
            migrationBuilder.Sql(RowLevelSecurity.Drop("groups", "group_fees"));
            migrationBuilder.Sql(RowLevelSecurity.Drop("tenancy", "schools"));
            migrationBuilder.Sql(RowLevelSecurity.Drop("outbox", "messages"));
            migrationBuilder.Sql(RowLevelSecurity.Drop("identity", "users"));
            foreach (var (schema, table) in TenantTables)
            {
                migrationBuilder.Sql(RowLevelSecurity.Drop(schema, table));
            }

            migrationBuilder.Sql(
                """
                DROP FUNCTION IF EXISTS tenancy.is_maintenance();
                DROP FUNCTION IF EXISTS tenancy.is_platform_scope();
                DROP FUNCTION IF EXISTS tenancy.current_tenant_id();
                """);
        }
    }
}
