using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SporAkademi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "billing");

            migrationBuilder.AddUniqueConstraint(
                name: "ak_enrollments_tenant_id_id_student_id_group_id",
                schema: "enrollments",
                table: "enrollments",
                columns: new[] { "tenant_id", "id", "student_id", "group_id" });

            migrationBuilder.CreateTable(
                name: "group_fees",
                schema: "groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    effective_from = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_fees", x => x.id);
                    table.CheckConstraint("ck_group_fees_amount", "amount >= 0");
                    table.ForeignKey(
                        name: "fk_group_fees_groups_group_id",
                        column: x => x.group_id,
                        principalSchema: "groups",
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enrollment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    method = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    paid_on = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    cancel_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    cancelled_by = table.Column<Guid>(type: "uuid", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payments", x => x.id);
                    table.CheckConstraint("ck_payments_amount", "amount > 0");
                    table.CheckConstraint("ck_payments_cancellation", "(status = 'Cancelled') = (cancel_reason IS NOT NULL) AND (status = 'Cancelled') = (cancelled_at IS NOT NULL) AND (status = 'Cancelled') = (cancelled_by IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_payments_enrollment_tenant_id_enrollment_id_student_id_grou",
                        columns: x => new { x.tenant_id, x.enrollment_id, x.student_id, x.group_id },
                        principalSchema: "enrollments",
                        principalTable: "enrollments",
                        principalColumns: new[] { "tenant_id", "id", "student_id", "group_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payments_group_tenant_id_group_id",
                        columns: x => new { x.tenant_id, x.group_id },
                        principalSchema: "groups",
                        principalTable: "groups",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payments_school_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "tenancy",
                        principalTable: "schools",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payments_student_tenant_id_student_id",
                        columns: x => new { x.tenant_id, x.student_id },
                        principalSchema: "students",
                        principalTable: "students",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_group_fees_group_id_effective_from",
                schema: "groups",
                table: "group_fees",
                columns: new[] { "group_id", "effective_from" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payments_tenant_id_enrollment_id_period",
                schema: "billing",
                table: "payments",
                columns: new[] { "tenant_id", "enrollment_id", "period" });

            migrationBuilder.CreateIndex(
                name: "ix_payments_tenant_id_enrollment_id_student_id_group_id",
                schema: "billing",
                table: "payments",
                columns: new[] { "tenant_id", "enrollment_id", "student_id", "group_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payments_tenant_id_group_id_period",
                schema: "billing",
                table: "payments",
                columns: new[] { "tenant_id", "group_id", "period" });

            migrationBuilder.CreateIndex(
                name: "ix_payments_tenant_id_paid_on",
                schema: "billing",
                table: "payments",
                columns: new[] { "tenant_id", "paid_on" });

            migrationBuilder.CreateIndex(
                name: "ix_payments_tenant_id_student_id",
                schema: "billing",
                table: "payments",
                columns: new[] { "tenant_id", "student_id" });

            // DEC-186: payments are audit records. Whoever connects, a row is never deleted and only the cancel
            // transition (Recorded -> Cancelled with its reason, author and time) may change it.
            migrationBuilder.Sql("""
                CREATE FUNCTION billing.payments_guard() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'billing.payments rows are never deleted' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF (NEW.id, NEW.tenant_id, NEW.enrollment_id, NEW.student_id, NEW.group_id, NEW.period,
                        NEW.amount, NEW.currency, NEW.method, NEW.paid_on, NEW.created_at, NEW.created_by)
                       IS DISTINCT FROM
                       (OLD.id, OLD.tenant_id, OLD.enrollment_id, OLD.student_id, OLD.group_id, OLD.period,
                        OLD.amount, OLD.currency, OLD.method, OLD.paid_on, OLD.created_at, OLD.created_by) THEN
                        RAISE EXCEPTION 'billing.payments rows are never edited' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF NOT (OLD.status = 'Recorded' AND NEW.status = 'Cancelled') THEN
                        RAISE EXCEPTION 'a payment can only be cancelled, once' USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER trg_payments_guard
                    BEFORE UPDATE OR DELETE ON billing.payments
                    FOR EACH ROW EXECUTE FUNCTION billing.payments_guard();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_payments_guard ON billing.payments; DROP FUNCTION IF EXISTS billing.payments_guard();");

            migrationBuilder.DropTable(
                name: "group_fees",
                schema: "groups");

            migrationBuilder.DropTable(
                name: "payments",
                schema: "billing");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_enrollments_tenant_id_id_student_id_group_id",
                schema: "enrollments",
                table: "enrollments");
        }
    }
}
