using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SporAkademi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOnlinePayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "online_payment_id",
                schema: "billing",
                table: "payments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "online_payments",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enrollment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enrollment_start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    period = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_token = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    checkout_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    checkout_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    provider_payment_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("pk_online_payments", x => x.id);
                    table.UniqueConstraint("ak_online_payments_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_online_payments_amount", "amount > 0");
                    table.CheckConstraint("ck_online_payments_checkout", "status IN ('Created', 'Failed', 'Expired') OR provider_token IS NOT NULL");
                    table.CheckConstraint("ck_online_payments_success", "(status = 'Succeeded') = (payment_id IS NOT NULL) AND (status <> 'Succeeded' OR provider_payment_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_online_payments_enrollment_tenant_id_enrollment_id_student_",
                        columns: x => new { x.tenant_id, x.enrollment_id, x.student_id, x.group_id },
                        principalSchema: "enrollments",
                        principalTable: "enrollments",
                        principalColumns: new[] { "tenant_id", "id", "student_id", "group_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_online_payments_school_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "tenancy",
                        principalTable: "schools",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_notifications",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    event_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    online_payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_notifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_payment_notifications_online_payments_tenant_id_online_paym",
                        columns: x => new { x.tenant_id, x.online_payment_id },
                        principalSchema: "billing",
                        principalTable: "online_payments",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_notifications_school_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "tenancy",
                        principalTable: "schools",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_payments_online_payment_id",
                schema: "billing",
                table: "payments",
                column: "online_payment_id",
                unique: true,
                filter: "online_payment_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_payments_tenant_id_online_payment_id",
                schema: "billing",
                table: "payments",
                columns: new[] { "tenant_id", "online_payment_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_payments_online",
                schema: "billing",
                table: "payments",
                sql: "(method = 'Online') = (online_payment_id IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_online_payments_open_due",
                schema: "billing",
                table: "online_payments",
                columns: new[] { "tenant_id", "enrollment_id", "period" },
                unique: true,
                filter: "status IN ('Created', 'Pending')");

            migrationBuilder.CreateIndex(
                name: "ix_online_payments_payment_id",
                schema: "billing",
                table: "online_payments",
                column: "payment_id",
                unique: true,
                filter: "payment_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_online_payments_provider_provider_payment_id",
                schema: "billing",
                table: "online_payments",
                columns: new[] { "provider", "provider_payment_id" },
                unique: true,
                filter: "provider_payment_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_online_payments_provider_provider_token",
                schema: "billing",
                table: "online_payments",
                columns: new[] { "provider", "provider_token" },
                unique: true,
                filter: "provider_token IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_online_payments_tenant_id_enrollment_id_student_id_group_id",
                schema: "billing",
                table: "online_payments",
                columns: new[] { "tenant_id", "enrollment_id", "student_id", "group_id" });

            migrationBuilder.CreateIndex(
                name: "ix_online_payments_tenant_id_idempotency_key",
                schema: "billing",
                table: "online_payments",
                columns: new[] { "tenant_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_online_payments_tenant_id_started_at",
                schema: "billing",
                table: "online_payments",
                columns: new[] { "tenant_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_notifications_provider_event_key",
                schema: "billing",
                table: "payment_notifications",
                columns: new[] { "provider", "event_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_notifications_tenant_id_online_payment_id",
                schema: "billing",
                table: "payment_notifications",
                columns: new[] { "tenant_id", "online_payment_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_payments_online_payments_tenant_id_online_payment_id",
                schema: "billing",
                table: "payments",
                columns: new[] { "tenant_id", "online_payment_id" },
                principalSchema: "billing",
                principalTable: "online_payments",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // DEC-342/DEC-346, NFR-REL-08: the database backs the state machine. A payment's online link never changes
            // and an online payment is never cancelled; a succeeded online payment never changes again, and none is
            // deleted (no double crediting even with a bug or a hand-written UPDATE).
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
                    IF NOT (OLD.status = 'Recorded' AND NEW.status = 'Cancelled') THEN
                        RAISE EXCEPTION 'a payment can only be cancelled, once' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF OLD.method = 'Online' THEN
                        RAISE EXCEPTION 'an online payment is not cancelled here' USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE FUNCTION billing.online_payments_guard() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'billing.online_payments rows are never deleted' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF (NEW.id, NEW.tenant_id, NEW.enrollment_id, NEW.student_id, NEW.group_id, NEW.period, NEW.amount,
                        NEW.currency, NEW.provider, NEW.idempotency_key, NEW.request_hash, NEW.started_at)
                       IS DISTINCT FROM
                       (OLD.id, OLD.tenant_id, OLD.enrollment_id, OLD.student_id, OLD.group_id, OLD.period, OLD.amount,
                        OLD.currency, OLD.provider, OLD.idempotency_key, OLD.request_hash, OLD.started_at) THEN
                        RAISE EXCEPTION 'an online payment''s due and amount never change' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF OLD.status = 'Succeeded' AND (NEW.status, NEW.payment_id, NEW.provider_payment_id)
                       IS DISTINCT FROM (OLD.status, OLD.payment_id, OLD.provider_payment_id) THEN
                        RAISE EXCEPTION 'a succeeded online payment never changes' USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER trg_online_payments_guard
                    BEFORE UPDATE OR DELETE ON billing.online_payments
                    FOR EACH ROW EXECUTE FUNCTION billing.online_payments_guard();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_online_payments_guard ON billing.online_payments;
                DROP FUNCTION IF EXISTS billing.online_payments_guard();
                CREATE OR REPLACE FUNCTION billing.payments_guard() RETURNS trigger
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
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_payments_online_payments_tenant_id_online_payment_id",
                schema: "billing",
                table: "payments");

            migrationBuilder.DropTable(
                name: "payment_notifications",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "online_payments",
                schema: "billing");

            migrationBuilder.DropIndex(
                name: "ix_payments_online_payment_id",
                schema: "billing",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "ix_payments_tenant_id_online_payment_id",
                schema: "billing",
                table: "payments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payments_online",
                schema: "billing",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "online_payment_id",
                schema: "billing",
                table: "payments");
        }
    }
}
