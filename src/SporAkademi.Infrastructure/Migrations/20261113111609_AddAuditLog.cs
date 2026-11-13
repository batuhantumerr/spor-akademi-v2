using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SporAkademi.Infrastructure.Persistence;

#nullable disable

namespace SporAkademi.Infrastructure.Migrations
{
    /// <summary>
    /// P4-05 (DEC-530 – DEC-537): the personal data audit log. Row level security like every school table, as a platform
    /// table: work without a school (a password set from an e-mail link, the Platform Admin creating a school's admin)
    /// records into the school of the changed user. The application role may add and read entries only; old entries are
    /// deleted by <c>audit.purge_expired</c>, a function owned by the migrator with the period fixed inside it.
    /// </summary>
    public partial class AddAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "audit");

            migrationBuilder.CreateTable(
                name: "audit_entries",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    fields = table.Column<string[]>(type: "text[]", nullable: false),
                    reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_audit_entries_school_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "tenancy",
                        principalTable: "schools",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_occurred_at",
                schema: "audit",
                table: "audit_entries",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_tenant_id_entity_type_occurred_at",
                schema: "audit",
                table: "audit_entries",
                columns: new[] { "tenant_id", "entity_type", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_tenant_id_occurred_at_id",
                schema: "audit",
                table: "audit_entries",
                columns: new[] { "tenant_id", "occurred_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_tenant_id_user_id_occurred_at",
                schema: "audit",
                table: "audit_entries",
                columns: new[] { "tenant_id", "user_id", "occurred_at" });

            migrationBuilder.Sql(RowLevelSecurity.ForPlatformTable("audit", "audit_entries"));

            // DEC-535: insert and read only. The default privileges (10-create-roles.sh) also grant UPDATE and DELETE.
            migrationBuilder.Sql(
                """
                REVOKE UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON audit.audit_entries FROM spor_akademi_app;
                GRANT SELECT, INSERT ON audit.audit_entries TO spor_akademi_app;
                """);

            // DEC-536: the two-year retention. The period is fixed here, so the application cannot shorten it; changing it
            // takes a migration. Runs as the owner, with the maintenance switch on only around its one statement (DEC-523).
            migrationBuilder.Sql(
                """
                CREATE FUNCTION audit.purge_expired(p_limit integer)
                    RETURNS integer
                    LANGUAGE plpgsql VOLATILE SECURITY DEFINER
                    SET search_path = pg_catalog, pg_temp
                    AS $$
                    DECLARE
                        previous text := current_setting('app.maintenance', true);
                        deleted integer;
                    BEGIN
                        IF p_limit IS NULL OR p_limit < 1 OR p_limit > 10000 THEN
                            RAISE EXCEPTION 'p_limit must be between 1 and 10000';
                        END IF;
                        PERFORM set_config('app.maintenance', 'on', true);
                        DELETE FROM audit.audit_entries
                        WHERE id IN (SELECT e.id FROM audit.audit_entries e
                                     WHERE e.occurred_at < now() - interval '2 years'
                                     ORDER BY e.occurred_at
                                     LIMIT p_limit);
                        GET DIAGNOSTICS deleted = ROW_COUNT;
                        PERFORM set_config('app.maintenance', coalesce(previous, ''), true);
                        RETURN deleted;
                    END $$;
                REVOKE ALL ON FUNCTION audit.purge_expired(integer) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION audit.purge_expired(integer) TO spor_akademi_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS audit.purge_expired(integer);");
            migrationBuilder.Sql(RowLevelSecurity.Drop("audit", "audit_entries"));

            migrationBuilder.DropTable(
                name: "audit_entries",
                schema: "audit");
        }
    }
}
