using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SporAkademi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChargeSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "charge_snapshots",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enrollment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    adjustment = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    discount_percent = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    discount_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    fee_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    fee_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    list_fee_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    list_fee_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_charge_snapshots", x => x.id);
                    table.CheckConstraint("ck_charge_snapshots_amounts", "fee_amount >= 0 AND discount_amount >= 0 AND fee_amount + discount_amount = list_fee_amount");
                    table.CheckConstraint("ck_charge_snapshots_discount_percent", "discount_percent BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "fk_charge_snapshots_enrollment_tenant_id_enrollment_id_student",
                        columns: x => new { x.tenant_id, x.enrollment_id, x.student_id, x.group_id },
                        principalSchema: "enrollments",
                        principalTable: "enrollments",
                        principalColumns: new[] { "tenant_id", "id", "student_id", "group_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_charge_snapshots_school_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "tenancy",
                        principalTable: "schools",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "closed_periods",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    snapshot_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_closed_periods", x => x.id);
                    table.CheckConstraint("ck_closed_periods_snapshot_count", "snapshot_count >= 0");
                    table.ForeignKey(
                        name: "fk_closed_periods_school_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "tenancy",
                        principalTable: "schools",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_charge_snapshots_tenant_id_enrollment_id_period",
                schema: "billing",
                table: "charge_snapshots",
                columns: new[] { "tenant_id", "enrollment_id", "period" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_charge_snapshots_tenant_id_enrollment_id_student_id_group_id",
                schema: "billing",
                table: "charge_snapshots",
                columns: new[] { "tenant_id", "enrollment_id", "student_id", "group_id" });

            migrationBuilder.CreateIndex(
                name: "ix_charge_snapshots_tenant_id_period",
                schema: "billing",
                table: "charge_snapshots",
                columns: new[] { "tenant_id", "period" });

            migrationBuilder.CreateIndex(
                name: "ix_charge_snapshots_tenant_id_student_id_period",
                schema: "billing",
                table: "charge_snapshots",
                columns: new[] { "tenant_id", "student_id", "period" });

            migrationBuilder.CreateIndex(
                name: "ix_closed_periods_tenant_id_period",
                schema: "billing",
                table: "closed_periods",
                columns: new[] { "tenant_id", "period" },
                unique: true);

            // DEC-375: a closed month never changes, whichever role connects (like trg_payments_guard, DEC-186).
            migrationBuilder.Sql("""
                CREATE FUNCTION billing.closed_billing_guard() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'closed billing periods and their charge snapshots are immutable'
                        USING ERRCODE = 'restrict_violation';
                END;
                $$;

                CREATE TRIGGER trg_charge_snapshots_guard
                    BEFORE UPDATE OR DELETE ON billing.charge_snapshots
                    FOR EACH ROW EXECUTE FUNCTION billing.closed_billing_guard();

                CREATE TRIGGER trg_closed_periods_guard
                    BEFORE UPDATE OR DELETE ON billing.closed_periods
                    FOR EACH ROW EXECUTE FUNCTION billing.closed_billing_guard();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP TRIGGER IF EXISTS trg_charge_snapshots_guard ON billing.charge_snapshots; "
                + "DROP TRIGGER IF EXISTS trg_closed_periods_guard ON billing.closed_periods; "
                + "DROP FUNCTION IF EXISTS billing.closed_billing_guard();");
            migrationBuilder.DropTable(
                name: "charge_snapshots",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "closed_periods",
                schema: "billing");
        }
    }
}
