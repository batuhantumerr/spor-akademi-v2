using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SporAkademi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentRefunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_payments_tenant_id_id",
                schema: "billing",
                table: "payments",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateTable(
                name: "payment_refunds",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    online_payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enrollment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_payment_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    provider_refund_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    needs_review = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("pk_payment_refunds", x => x.id);
                    table.CheckConstraint("ck_payment_refunds_amount", "amount > 0");
                    table.CheckConstraint("ck_payment_refunds_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_payment_refunds_result", "(status = 'Succeeded') = (provider_refund_id IS NOT NULL) AND (status = 'Failed') = (failure_code IS NOT NULL) AND (status = 'Pending') = (completed_at IS NULL)");
                    table.CheckConstraint("ck_payment_refunds_review", "NOT needs_review OR status = 'Pending'");
                    table.ForeignKey(
                        name: "fk_payment_refunds_enrollment_tenant_id_enrollment_id",
                        columns: x => new { x.tenant_id, x.enrollment_id },
                        principalSchema: "enrollments",
                        principalTable: "enrollments",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_refunds_online_payments_tenant_id_online_payment_id",
                        columns: x => new { x.tenant_id, x.online_payment_id },
                        principalSchema: "billing",
                        principalTable: "online_payments",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_refunds_payments_tenant_id_payment_id",
                        columns: x => new { x.tenant_id, x.payment_id },
                        principalSchema: "billing",
                        principalTable: "payments",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_refunds_school_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "tenancy",
                        principalTable: "schools",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_payments_refunded",
                schema: "billing",
                table: "payments",
                sql: "status <> 'Refunded' OR method = 'Online'");

            migrationBuilder.CreateIndex(
                name: "ix_payment_refunds_active_payment",
                schema: "billing",
                table: "payment_refunds",
                column: "payment_id",
                unique: true,
                filter: "status IN ('Pending', 'Succeeded')");

            migrationBuilder.CreateIndex(
                name: "ix_payment_refunds_tenant_id_enrollment_id",
                schema: "billing",
                table: "payment_refunds",
                columns: new[] { "tenant_id", "enrollment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_refunds_tenant_id_idempotency_key",
                schema: "billing",
                table: "payment_refunds",
                columns: new[] { "tenant_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_refunds_tenant_id_online_payment_id",
                schema: "billing",
                table: "payment_refunds",
                columns: new[] { "tenant_id", "online_payment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_refunds_tenant_id_payment_id_requested_at",
                schema: "billing",
                table: "payment_refunds",
                columns: new[] { "tenant_id", "payment_id", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_refunds_tenant_id_requested_at",
                schema: "billing",
                table: "payment_refunds",
                columns: new[] { "tenant_id", "requested_at" },
                filter: "status = 'Pending'");

            // P3-13 (DEC-417): the database backs the refund state machine. A recorded online payment may become Refunded
            // (once, never back); a manual payment is still only cancelled. A refund request's payment, amount, reason and
            // key never change, a request starts pending and untried, a final result (Succeeded or Failed) never changes, and no
            // row is deleted.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION billing.payments_guard() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'billing.payments rows are never deleted' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF (NEW.id, NEW.tenant_id, NEW.enrollment_id, NEW.student_id, NEW.group_id, NEW.period,
                        NEW.amount, NEW.currency, NEW.method, NEW.paid_on, NEW.created_at, NEW.created_by, NEW.online_payment_id)
                       IS DISTINCT FROM
                       (OLD.id, OLD.tenant_id, OLD.enrollment_id, OLD.student_id, OLD.group_id, OLD.period,
                        OLD.amount, OLD.currency, OLD.method, OLD.paid_on, OLD.created_at, OLD.created_by, OLD.online_payment_id) THEN
                        RAISE EXCEPTION 'billing.payments rows are never edited' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF OLD.status = 'Recorded' AND NEW.status = 'Refunded' THEN
                        IF OLD.method <> 'Online' THEN
                            RAISE EXCEPTION 'only an online payment is refunded' USING ERRCODE = 'restrict_violation';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF NOT (OLD.status = 'Recorded' AND NEW.status = 'Cancelled') THEN
                        RAISE EXCEPTION 'a payment can only be cancelled, once' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF OLD.method = 'Online' THEN
                        RAISE EXCEPTION 'an online payment is not cancelled here' USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE FUNCTION billing.payment_refunds_guard() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'billing.payment_refunds rows are never deleted' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF TG_OP = 'INSERT' THEN
                        IF NEW.status <> 'Pending' OR NEW.attempts <> 0 OR NEW.needs_review OR NEW.last_attempt_at IS NOT NULL
                           OR NEW.provider_refund_id IS NOT NULL OR NEW.failure_code IS NOT NULL OR NEW.completed_at IS NOT NULL THEN
                            RAISE EXCEPTION 'a refund request starts pending, untried' USING ERRCODE = 'restrict_violation';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF (NEW.id, NEW.tenant_id, NEW.payment_id, NEW.online_payment_id, NEW.enrollment_id, NEW.student_id,
                        NEW.amount, NEW.currency, NEW.provider, NEW.provider_payment_id, NEW.reason, NEW.idempotency_key,
                        NEW.request_hash, NEW.requested_by, NEW.requested_at, NEW.created_at, NEW.created_by)
                       IS DISTINCT FROM
                       (OLD.id, OLD.tenant_id, OLD.payment_id, OLD.online_payment_id, OLD.enrollment_id, OLD.student_id,
                        OLD.amount, OLD.currency, OLD.provider, OLD.provider_payment_id, OLD.reason, OLD.idempotency_key,
                        OLD.request_hash, OLD.requested_by, OLD.requested_at, OLD.created_at, OLD.created_by) THEN
                        RAISE EXCEPTION 'a refund request never changes' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF OLD.status <> 'Pending'
                       AND (NEW.status, NEW.provider_refund_id, NEW.failure_code, NEW.completed_at, NEW.attempts, NEW.last_attempt_at, NEW.needs_review)
                       IS DISTINCT FROM
                       (OLD.status, OLD.provider_refund_id, OLD.failure_code, OLD.completed_at, OLD.attempts, OLD.last_attempt_at, OLD.needs_review) THEN
                        RAISE EXCEPTION 'a refund with a final result never changes' USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER trg_payment_refunds_guard
                    BEFORE INSERT OR UPDATE OR DELETE ON billing.payment_refunds
                    FOR EACH ROW EXECUTE FUNCTION billing.payment_refunds_guard();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Refunded payments cannot be represented before this migration (the old guard and enum have no Refunded): stop.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM billing.payments WHERE status = 'Refunded') THEN
                        RAISE EXCEPTION 'refunded payments exist; AddPaymentRefunds cannot be reverted' USING ERRCODE = 'restrict_violation';
                    END IF;
                END;
                $$;
                """);

            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_payment_refunds_guard ON billing.payment_refunds;
                DROP FUNCTION IF EXISTS billing.payment_refunds_guard();
                CREATE OR REPLACE FUNCTION billing.payments_guard() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'billing.payments rows are never deleted' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF (NEW.id, NEW.tenant_id, NEW.enrollment_id, NEW.student_id, NEW.group_id, NEW.period,
                        NEW.amount, NEW.currency, NEW.method, NEW.paid_on, NEW.created_at, NEW.created_by, NEW.online_payment_id)
                       IS DISTINCT FROM
                       (OLD.id, OLD.tenant_id, OLD.enrollment_id, OLD.student_id, OLD.group_id, OLD.period,
                        OLD.amount, OLD.currency, OLD.method, OLD.paid_on, OLD.created_at, OLD.created_by, OLD.online_payment_id) THEN
                        RAISE EXCEPTION 'billing.payments rows are never edited' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF NOT (OLD.status = 'Recorded' AND NEW.status = 'Cancelled') THEN
                        RAISE EXCEPTION 'a payment can only be cancelled, once' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF OLD.method = 'Online' THEN
                        RAISE EXCEPTION 'an online payment is not cancelled here' USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                """);

            migrationBuilder.DropTable(
                name: "payment_refunds",
                schema: "billing");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_payments_tenant_id_id",
                schema: "billing",
                table: "payments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payments_refunded",
                schema: "billing",
                table: "payments");
        }
    }
}
